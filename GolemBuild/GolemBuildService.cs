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
using GURestApi.Client;
using GURestApi.Model;

namespace GolemBuild
{
    public class GolemBuildService : IBuildService
    {
        public static GolemBuildService buildService = null;
        public event EventHandler<BuildTaskStatusChangedArgs> BuildTaskStatusChanged;

        public string BuildPath = "";

        private Task mainLoop = null;
        private Task requestLoop = null;
        private System.Threading.CancellationTokenSource cancellationSource = null;
        private ConcurrentQueue<CompilationTask> taskQueue = null;
        private int ServerPort = 6000;
        private string myIP = null;
        private int taskCount = 0;

        public HubInfo HubInfo { get; private set; }

        private PeerApi golemApi = null;

        public GolemBuildService(string hubUrl = "http://10.30.10.121:6162", int serverPort = 6000)//current default
        {
            // TODO: Configure API key authorization: serviceToken
            //Configuration.Default.AddApiKey("X-GU-APIKEY", "YOUR_API_KEY");
            // Uncomment below to setup prefix (e.g. Bearer) for API key, if needed
            // Configuration.Default.AddApiKeyPrefix("X-GU-APIKEY", "Bearer");
            // TODO: Configure API key authorization: systemName
            Configuration.Default.AddApiKey("X-GU-APPNAME", "GolemCompiler");
            // Uncomment below to setup prefix (e.g. Bearer) for API key, if needed
            Configuration.Default.AddApiKeyPrefix("X-GU-APPNAME", "Bearer");

            ServerPort = serverPort;

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

            golemApi = new PeerApi(hubUrl);
            buildService = this;
        }

        public void AddTask(CompilationTask task)
        {
            System.Threading.Interlocked.Increment(ref taskCount);
            taskQueue.Enqueue(task);
        }

        public int GetTaskCount()
        {
            return taskCount;
        }

        public void WaitTasks()
        {
            while(GetTaskCount() > 0)
            {
                System.Threading.Thread.Sleep(100);
            }
        }

        public bool Start()
        {
            if (mainLoop != null)
                throw new Exception("Service is already running!");

            taskQueue = new ConcurrentQueue<CompilationTask>();

            cancellationSource = new System.Threading.CancellationTokenSource();

            //run main task loop
            mainLoop = Task.Run(TaskProcessor, cancellationSource.Token);

            //run the http server
            requestLoop = Task.Run(RequestServer, cancellationSource.Token);

            return mainLoop!=null;
        }

        public bool Stop()
        {
            if (cancellationSource!=null)
            {
                cancellationSource.Cancel();//this will throw exception
                try
                {
                    mainLoop.Wait();
                }
                catch(TaskCanceledException)
                {
                }
            }
            cancellationSource = null;
            mainLoop = null;
            
            return true;
        }

        private async Task TaskProcessor()
        {
            //first try to connect to the hub
            HubInfo = await golemApi.GetHubInfoAsync();
            //TODO: would be nice to write this info to some output stream

            while (true)
            {
                CompilationTask task = null;
                if (taskQueue.TryDequeue(out task))
                {
                    var peers = await golemApi.ListPeersAsync();

                    //TODO: we want each task to be distributed to a different peer
                    //not that number of peers might change in time
                    //we might first grab all tasks and then grab all peers and than distribute tasks for those that are not occupied

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
                    
                    //1. Create deployment
                    string fileName = Path.GetFileNameWithoutExtension(task.FilePath);
                    string hash = "SHA1:"+fileName;//should be the hash of name or file - note that SHA1 or SHA3 prefix is a must
                    DeploymentSpecImage specImg = new DeploymentSpecImage(hash, "http://"+myIP+":"+ServerPort+ "/requestID/"+ fileName);
                    //create deployment
                    DeploymentSpec spec = new DeploymentSpec(EnvType.Hd, specImg, "compiler", new List<string>() {});
                    string depId = await golemApi.CreateDeploymentAsync(peers[myPeer].NodeId, spec);
                    depId = depId.Replace("\"", "");//TODO: remove this! current bug fix
                    //TODO: based on our needs we can also create a session ID, and simply wait for peers to grab deployed tasks from there
                    //though I haven't checked that

                    var results = await golemApi.UpdateDeploymentAsync(peers[myPeer].NodeId, depId, new List<Command>() { new ExecCommand("golembuild.bat", new List<string>())});

                    // Upload output.zip
                    results = await golemApi.UpdateDeploymentAsync(peers[myPeer].NodeId, depId, new List<Command>() { new UploadFileCommand("http://"+myIP+":"+ServerPort+ "/requestID/" + fileName, "output.zip") });

                    //TODO: when it is done either end deployment or add more tasks...
                    golemApi.DropDeployment(peers[myPeer].NodeId, depId);

                    System.Threading.Interlocked.Decrement(ref taskCount);
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
            }
            catch (Exception e)
            {

            }
            while (true)
            {
                try
                {
                    //get the request
                    HttpListenerContext context = await listener.GetContextAsync();
                    _ = Task.Run(() => ProcessRequest(context)); // Discard is used so we don't get warnings about not using await...
                }
                catch (TaskCanceledException ex)
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

        private void ProcessRequest(HttpListenerContext context)
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
                response.StatusCode = 200;
                response.Headers.Add("Server", String.Empty);
                response.Headers.Add("Date", String.Empty);
                response.Close();

                ZipFile.ExtractToDirectory(zipName, Path.GetDirectoryName(zipName));
            }
            else // They are trying to download a file
            {
                // Obtain a response object.
                HttpListenerResponse response = context.Response;

                //let's check ranges
                long offset = 0;
                int size = -1;
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
            }
        }

        private DataPackage GetDataPackage(Uri url, long offset, int size)
        {
            string fileName = Path.GetFileNameWithoutExtension(url.AbsolutePath);//for now it can be anything - provider uses it as a file name
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
                FileStream file = new FileStream(tarPath, FileMode.Open);
                file.Seek(offset, SeekOrigin.Begin);
                file.Read(data.DataStream, 0, size);
                file.Close();
                data.DataHash = "SHA1:abcdef";
            }

            return data;
        }
    }
}
