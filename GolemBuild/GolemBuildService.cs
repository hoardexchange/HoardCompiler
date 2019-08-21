using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using GURestApi.Api;
using GURestApi.Model;

namespace GolemBuild
{
    public class GolemBuildService : IBuildService
    {
        public class Configuration
        {
            public string GolemHubUrl
            {
                get;
                set;
            } = "http://10.30.10.121:6162";


            public int GolemServerPort
            {
                get;
                set;
            } = 6000;

            public override bool Equals(object obj)
            {
                return base.Equals(obj as Configuration);
            }

            public bool Equals(Configuration input)
            {
                if (input == null)
                    return false;

                return
                    (
                        GolemHubUrl == input.GolemHubUrl ||
                        (GolemHubUrl != null &&
                        GolemHubUrl.Equals(input.GolemHubUrl))
                    ) &&
                    (
                        GolemServerPort == input.GolemServerPort
                    );
            }

            public override int GetHashCode()
            {
                int hashCode = 41;
                if (GolemHubUrl != null)
                    hashCode = hashCode * 59 + GolemHubUrl.GetHashCode();
                hashCode = hashCode * 59 + GolemServerPort.GetHashCode();
                return hashCode;
            }
        }

        public static GolemBuildService Instance = null;
        public event EventHandler<BuildTaskStatusChangedArgs> BuildTaskStatusChanged;

        public string BuildPath = "";
        public bool compilationSuccessful = false;
        public Configuration Options = new Configuration();

        public bool IsRunning
        {
            get { return mainLoop != null; }
        }

        private Task hubInfoLoop = null;
        private Task mainLoop = null;

        private System.Threading.CancellationTokenSource cancellationSource = null;
        private ConcurrentQueue<CompilationTask> taskQueue = null;
        private int ServerPort = 6000;
        private string myIP = null;
        private GolemHttpService httpService = null;

        public HubInfo HubInfo { get; private set; }

        private PeerApi golemApi = null;
        private List<PeerInfo> knownPeers = new List<PeerInfo>();
        private ConcurrentQueue<GolemWorker> workerPool = new ConcurrentQueue<GolemWorker>();

        private int runningWorkers = 0;

        public GolemBuildService()
        {
            // TODO: Configure API key authorization: serviceToken
            //Configuration.Default.AddApiKey("X-GU-APIKEY", "YOUR_API_KEY");
            // Uncomment below to setup prefix (e.g. Bearer) for API key, if needed
            // Configuration.Default.AddApiKeyPrefix("X-GU-APIKEY", "Bearer");
            // TODO: Configure API key authorization: systemName
            GURestApi.Client.Configuration.Default.AddApiKey("X-GU-APPNAME", "GolemCompiler");
            // Uncomment below to setup prefix (e.g. Bearer) for API key, if needed
            GURestApi.Client.Configuration.Default.AddApiKeyPrefix("X-GU-APPNAME", "Bearer");

            string output = "";
            foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (item.NetworkInterfaceType == NetworkInterfaceType.Ethernet && item.OperationalStatus == OperationalStatus.Up)
                {
                    foreach (UnicastIPAddressInformation ip in item.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            output = ip.Address.ToString();
                        }
                    }
                }
            }

            myIP = output;
                        
            Instance = this;
        }

        internal string GetHttpDownloadUri(string fileName)
        {
            return "http://" + myIP + ":" + ServerPort + "/requestID/tasks/" + fileName;
        }

        internal string GetHttpUploadUri(string fileName)
        {
            return "http://" + myIP + ":" + ServerPort + "/requestID/upload/" + fileName;
        }

        public void AddTask(CompilationTask task)
        {
            taskQueue.Enqueue(task);
        }

        public int GetTaskCount()
        {
            return taskQueue.Count;
        }

        public bool WaitTasks()
        {
            while(!taskQueue.IsEmpty || runningWorkers > 0)
            {
                System.Threading.Thread.Sleep(100);
            }

            GolemCache.Reset();

            return compilationSuccessful;
        }

        public bool Start()
        {
            if (mainLoop != null)
                throw new Exception("Service is already running!");

            golemApi = new PeerApi(Options.GolemHubUrl);
            ServerPort = Options.GolemServerPort;

            taskQueue = new ConcurrentQueue<CompilationTask>();

            cancellationSource = new System.Threading.CancellationTokenSource();

            //run the hub info loop (peer discovery)
            hubInfoLoop = GolemHubQueryTask(cancellationSource.Token);
            //hubInfoLoop.Start();

            //run main task loop
            mainLoop = TaskDispatcher(cancellationSource.Token);

            //run the http server
            httpService = new GolemHttpService();
            httpService.Start();

            return mainLoop!=null;
        }

        public bool Stop()
        {
            if (cancellationSource!=null)
            {
                cancellationSource.Cancel();//this will throw
                try
                {
                    hubInfoLoop.Wait();
                    mainLoop.Wait();
                }
                catch(Exception ex)
                {
                    Logger.LogError(ex.Message);
                }
            }
            cancellationSource = null;

            hubInfoLoop = null;
            mainLoop = null;

            if (httpService != null)
                httpService.Stop();
            httpService = null;

            return true;
        }

        private async Task GolemHubQueryTask(System.Threading.CancellationToken token)
        {
            //first try to connect to the hub
            HubInfo = await golemApi.GetHubInfoAsync();
            Logger.LogMessage($"Connected to Golem Hub\n{HubInfo.ToJson()}");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    //get all peers
                    var peers = await golemApi.ListPeersAsync();

                    //synchronize peers with the knownPeers
                    foreach (var p in peers)
                    {
                        if (!knownPeers.Contains(p))
                        {
                            //add new workers to workerPool                        
                            workerPool.Enqueue(new GolemWorker(this, p, await golemApi.GetPeerHardwareAsync(p.NodeId)));
                        }
                    }
                    //TODO: do we need to do sth with workers that are not in the hub anymore? or will they die automatically?

                    knownPeers = peers;

                    await Task.Delay(10 * 1000, token);//do this once per 10 seconds
                }
                catch (TaskCanceledException)
                {
                }
                catch (Exception ex)
                {
                    //probably timed out request
                    Logger.LogMessage(ex.Message);
                }
            }
        }

        private async Task TaskDispatcher(System.Threading.CancellationToken token)
        {
            try
            {
                //we need to distribute work from the queue
                while (true)
                {
                    while (taskQueue.Count > 0 && !token.IsCancellationRequested)
                    {
                        //get number of available tasks (this can only increase from another thread, so should be fine)
                        int taskQueueSize = taskQueue.Count;
                        //get first available worker
                        GolemWorker worker = null;
                        if (workerPool.TryDequeue(out worker))
                        {
                            System.Threading.Interlocked.Increment(ref runningWorkers);
                            Logger.LogMessage("Worker " + worker.Peer.NodeId + " started");

                            worker.ClearTasks();

                            List<string> compilersUsed = new List<string>();

                            //get the number of tasks to process
                            int taskCount = Math.Min(worker.TaskCapacity, taskQueueSize);
                            for (int i = 0; i < taskCount; ++i)
                            {
                                CompilationTask task = null;
                                if (taskQueue.TryDequeue(out task))
                                {
                                    if (!compilersUsed.Contains(task.Compiler))
                                        compilersUsed.Add(task.Compiler);

                                    worker.AddTask(task);
                                }
                            }

                            string hash = GolemCache.GetCompilerPackageHash(compilersUsed);
                            DeploymentSpecImage specImg = new DeploymentSpecImage("SHA1:" + hash, "http://" + myIP + ":" + ServerPort + "/requestID/compiler/" + hash);
                            //create deployment
                            DeploymentSpec spec = new DeploymentSpec(EnvType.Hd, specImg, "Compiler", new List<string>() { });
                            worker.Dispatch(golemApi, spec, () => 
                            {
                                workerPool.Enqueue(worker);
                                Logger.LogMessage("Worker " + worker.Peer.NodeId + " finished");
                                System.Threading.Interlocked.Decrement(ref runningWorkers);
                            });
                        }
                    }
                    await Task.Delay(1000, token);
                }
            }
            catch(TaskCanceledException)
            {
            }
        }
    }
}
