using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;
using GURestApi.Api;
using GURestApi.Model;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;

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
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
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
            return "http://" + myIP + ":" + ServerPort + "/upload/" + fileName;
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
            while(!taskQueue.IsEmpty)
            {
                System.Threading.Thread.Sleep(100);
            }
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
                            worker.Dispatch(golemApi, spec, () => { workerPool.Enqueue(worker); });
                        }
                    }
                    await Task.Delay(1000, token);
                }
            }
            catch(TaskCanceledException)
            {
            }
        }

        private async Task TaskProcessor()
        {
            //first try to connect to the hub
            HubInfo = await golemApi.GetHubInfoAsync();
            Logger.LogMessage($"Connected to Golem Hub\n{HubInfo.ToJson()}");

            while (true)
            {
                CompilationTask task = null;
                if (taskQueue.TryDequeue(out task))
                {
                    var peers = await golemApi.ListPeersAsync();

                    //TODO: we want each task to be distributed to a different peer
                    //note that number of peers might change in time
                    //we might first grab all tasks and then grab all peers and then distribute tasks for those that are not occupied

                    int myPeer = -1;

                    const bool useOwnProvider = true;

                    if (useOwnProvider)
                    {
                        for (int i = 0; i < peers.Count; i++)
                        {
                            if (peers[i].PeerAddr.Contains(myIP))
                            {
                                myPeer = i;
                                break;
                            }
                        }

                        if (myPeer == -1)
                        {
                            return;
                        }
                    }
                    else
                    {
                        myPeer = 0; // Just use first one for now, using more providers is a TODO
                    }

                    try
                    {
                        //1. Create deployment
                        string fileName = Path.GetFileNameWithoutExtension(task.FilePath);
                        string tarPath = Path.Combine(GolemBuild.golemBuildTasksPath, fileName + ".tar.gz");

                        string hash = "SHA1:";
                        byte[] dataStream = File.ReadAllBytes(tarPath);
                        using (var cryptoProvider = new SHA1CryptoServiceProvider())
                        {
                            hash += BitConverter.ToString(cryptoProvider.ComputeHash(dataStream)).Replace("-", string.Empty).ToLower();
                        }

                        DeploymentSpecImage specImg = new DeploymentSpecImage(hash, "http://" + myIP + ":" + ServerPort + "/requestID/" + fileName);
                        //create deployment
                        DeploymentSpec spec = new DeploymentSpec(EnvType.Hd, specImg, "compiler", new List<string>() { });
                        string depId = await golemApi.CreateDeploymentAsync(peers[myPeer].NodeId, spec);

                        //TODO: based on our needs we can also create a session ID, and simply wait for peers to grab deployed tasks from there
                        //though I haven't checked that

                        var results = await golemApi.UpdateDeploymentAsync(peers[myPeer].NodeId, depId, new List<Command>() { new ExecCommand("golembuild.bat", new List<string>()) });

                    bool error = false;
                    string[] lines = results[0].Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                    foreach(string line in lines)
                    {
                        if (line.Contains(" error") || line.Contains("fatal error"))
                        {
                                Logger.LogError("[ERROR] " + fileName + ": " + line);error = true;
                        }
                        else if (line.Contains("warning"))
                        {
                                Logger.LogMessage("[WARNING] " + fileName + ": " + line);
                        }
                    }

                    if (!error)
                    {
                            Logger.LogMessage("[SUCCESS] " + fileName);

                            // Upload output.zip
                            results = await golemApi.UpdateDeploymentAsync(peers[myPeer].NodeId, depId, new List<Command>() { new UploadFileCommand("http://" + myIP + ":" + ServerPort + "/requestID/" + fileName, "output.zip") });
                    }
                    else
                    {
                            compilationSuccessful = false;
                    }

                        //TODO: when it is done either end deployment or add more tasks...
                        golemApi.DropDeployment(peers[myPeer].NodeId, depId);
                    }
                    catch(Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
                await Task.Delay(1000);
            }
        }
    }
}
