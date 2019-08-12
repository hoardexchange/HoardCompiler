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
                        GolemServerPort == input.GolemServerPort ||
                        (GolemServerPort.Equals(input.GolemServerPort))
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

        public event Action<string> OnMessage;

        public bool IsRunning
        {
            get { return mainLoop != null; }
        }

        private Task hubInfoLoop = null;
        private Task mainLoop = null;
        private Task requestLoop = null;
        private Task hubQuery = null;
        private System.Threading.CancellationTokenSource cancellationSource = null;
        private ConcurrentQueue<CompilationTask> taskQueue = null;
        private int ServerPort = 6000;
        private string myIP = null;

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

        public void LogMessage(string msg)
        {
            OnMessage?.Invoke(msg);
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
            hubInfoLoop = Task.Run(GolemHubQueryTask, cancellationSource.Token);

            //run main task loop
            mainLoop = Task.Run(TaskDispatcher, cancellationSource.Token);

            //run the http server
            requestLoop = Task.Run(RequestServer, cancellationSource.Token);

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
                    requestLoop.Wait();
                }
                catch(TaskCanceledException)
                {
                }
            }
            cancellationSource = null;

            hubInfoLoop = null;
            mainLoop = null;
            requestLoop = null;

            return true;
        }

        private async Task GolemHubQueryTask()
        {
            //first try to connect to the hub
            HubInfo = await golemApi.GetHubInfoAsync();
            LogMessage($"Connected to Golem Hub\n{HubInfo.ToJson()}");

            while (true)
            {
                //get all peers
                var peers = await golemApi.ListPeersAsync();

                //synchronize peers with the knownPeers
                foreach(var p in peers)
                {
                    if (!knownPeers.Contains(p))
                    {
                        //add new workers to workerPool                        
                        workerPool.Enqueue(new GolemWorker(this, p, await golemApi.GetPeerHardwareAsync(p.NodeId)));
                    }
                }
                //TODO: do we need to do sth with workers that are not in the hub anymore? or will they die automatically?

                knownPeers = peers;

                await Task.Delay(10 * 1000);//do this once per 10 seconds
            }
        }

        private async Task TaskDispatcher()
        {
            //we need to distribute work from the queue
            while (true)
            {
                while (taskQueue.Count > 0)
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
                        worker.Dispatch(golemApi, spec, ()=> { workerPool.Enqueue(worker); });
                    }
                }
                await Task.Delay(1000);
            }
        }

        private async Task TaskProcessor()
        {
            //first try to connect to the hub
            HubInfo = await golemApi.GetHubInfoAsync();
            LogMessage($"Connected to Golem Hub\n{HubInfo.ToJson()}");

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
                                LogMessage("[ERROR] " + fileName + ": " + line);error = true;
                        }
                        else if (line.Contains("warning"))
                        {
                                LogMessage("[WARNING] " + fileName + ": " + line);
                        }
                    }

                    if (!error)
                    {
                            LogMessage("[SUCCESS] " + fileName);

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

        /// <summary>
        /// All providers will ask this server for files and packages, for now the only supported format is tar.gz
        /// TODO: try if zip compression is also supported
        /// </summary>
        /// <returns></returns>
        private async Task RequestServer()
        {            
            HttpListener listener = new HttpListener();
            //this part is tricky:
            //either run this as administrator or
            //run: netsh http add urlacl url=http://+:ServerPort/requestID/ user=DOMAIN\username (as an administrator)
            listener.Prefixes.Add("http://+:" + ServerPort + "/requestID/");
            try
            {
                listener.Start();
                while (true)
                {
                    try
                    {
                        //get the request
                        HttpListenerContext context = await listener.GetContextAsync();
                        _ = Task.Run(() => ProcessRequest(context)); // Discard is used so we don't get warnings about not using await...
                    }
                    catch (TaskCanceledException)
                    {
                        listener.Stop();
                        //bail out
                        return;
                    }
                    catch (Exception ex)
                    {
                        //TODO: do sth with this exception
                        Console.WriteLine(ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage(ex.Message);
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            try
            {
                HttpListenerRequest request = context.Request;

                // Are they trying to upload a file?
                if (request.HttpMethod == "PUT")
                {
                    System.IO.Stream input = request.InputStream;
                    string zipName = Path.Combine(BuildPath, Path.ChangeExtension(Path.GetFileNameWithoutExtension(request.RawUrl), ".zip"));
                    FileStream fileStream = File.Create(zipName);
                    input.CopyTo(fileStream);
                    fileStream.Close();
                    input.Close();

                    // Send back OK
                    HttpListenerResponse response = context.Response;
                    response.Headers.Clear();                    
                    response.SendChunked = false;
                    response.StatusCode = 201;
                    response.AddHeader("Content-Location", zipName);
                    //response.AddHeader("Server", String.Empty);
                    //response.AddHeader("Date", String.Empty);
                    response.Close();

                    ZipFile.ExtractToDirectory(zipName, Path.GetDirectoryName(zipName));
                }
                else // They are trying to download a file
                {
                    // Obtain a response object.
                    HttpListenerResponse response = context.Response;

                    //let's check ranges
                    long offset = 0;
                    long size = -1;
                    foreach (string header in request.Headers.AllKeys)
                    {
                        if (header == "Range")
                        {
                            string[] values = request.Headers.GetValues(header);
                            string[] tokens = values[0].Split('=', '-');
                            offset = int.Parse(tokens[1]);
                            size = (int)(int.Parse(tokens[2]) - offset + 1);
                        }
                    }

                    // Are they requesting a CompilerPackage?
                    if (request.RawUrl.StartsWith("/requestID/compiler/"))
                    {
                        string compilerHash = request.RawUrl.Replace("/requestID/compiler/", "");
                        byte[] data;
                        if (GolemCache.GetCompilerPackageData(compilerHash, out data))
                        {
                            response.AddHeader("ETag", "SHA1:" + compilerHash);

                            if (size == -1)
                            {
                                size = data.Length;
                            }

                            response.ContentLength64 = size;

                            Stream output = response.OutputStream;
                            output.Write(data, (int)offset, (int)size);
                            output.Close();
                        }
                    }
                    // Or are they requesting a tasks package?
                    else if (request.RawUrl.StartsWith("/requestID/tasks/"))
                    {
                        string tasksPackageHash = request.RawUrl.Replace("/requestID/tasks/", "");
                        byte[] data;
                        if (GolemCache.GetTasksPackage(tasksPackageHash, out data))
                        {
                            response.AddHeader("ETag", "SHA1:" + tasksPackageHash);

                            if (size == -1)
                            {
                                size = data.Length;
                            }

                            response.ContentLength64 = size;

                            Stream output = response.OutputStream;
                            output.Write(data, (int)offset, (int)size);
                            output.Close();
                        }
                    }
                    /*{
                        //1. based on request.Url fetch the content data
                        DataPackage data = GetDataPackage(request.Url, offset, size);
                        //2. calculate some hash so provider nows if content has changed or not
                        response.AddHeader("ETag", data.DataHash);
                        response.ContentLength64 = data.DataStream.Length;
                        //response.ContentType = "application/x-gzip";// - not needed
                        //response.AddHeader("Accept-Ranges", "bytes");// - not needed?
                        System.IO.Stream output = response.OutputStream;
                        output.Write(data.DataStream, 0, data.DataStream.Length);
                        // close the output stream.
                        output.Close();
                    }*/
                }
            }
            catch (TaskCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                //TODO: do sth with this exception
                Console.WriteLine(ex.Message);
            }
        }

        private DataPackage GetDataPackage(Uri url, long offset, int size)
        {
            string fileName = Path.GetFileNameWithoutExtension(url.AbsolutePath);
            string tarPath = Path.Combine(GolemBuild.golemBuildTasksPath, fileName + ".tar.gz");

            if (!File.Exists(tarPath))
            {
                throw new FileNotFoundException("Could not find the requested " + fileName + "tar.gz");
            }

            DataPackage data = new DataPackage();

            if (size == -1)
            {
                data.DataStream = File.ReadAllBytes(tarPath);
                using (var cryptoProvider = new SHA1CryptoServiceProvider())
                {
                    data.DataHash = "SHA1:" + BitConverter
                            .ToString(cryptoProvider.ComputeHash(data.DataStream)).Replace("-", string.Empty).ToLower();
                }
            }
            else
            {
                data.DataStream = new byte[size];
                FileStream file = new FileStream(tarPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                file.Seek(offset, SeekOrigin.Begin);
                file.Read(data.DataStream, 0, size);
                file.Close();
                data.DataHash = "SHA1:abcdef";
            }

            return data;
        }
    }
}
