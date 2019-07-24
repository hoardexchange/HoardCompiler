using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using GURestApi.Api;
using GURestApi.Client;
using GURestApi.Model;

namespace GolemBuild
{
    public class GolemBuildService : IBuildService
    {
        public event EventHandler<BuildTaskStatusChangedArgs> BuildTaskStatusChanged;

        private Task mainLoop = null;
        private Task requestLoop = null;
        private System.Threading.CancellationTokenSource cancellationSource = null;
        private ConcurrentQueue<CompilationTask> taskQueue = null;
        private int ServerPort = 6000;
        private string myIP = null;

        public HubInfo HubInfo { get; private set; }

        private PeerApi golemApi = null;
        
        public GolemBuildService(string hubUrl = "http://10.30.10.121:6162", int serverPort = 6000)//current default
        {
            // TODO: Configure API key authorization: serviceToken
            //Configuration.Default.AddApiKey("X-GU-APIKEY", "YOUR_API_KEY");
            // Uncomment below to setup prefix (e.g. Bearer) for API key, if needed
            // Configuration.Default.AddApiKeyPrefix("X-GU-APIKEY", "Bearer");
            // TODO: Configure API key authorization: systemName
            //Configuration.Default.AddApiKey("X-GU-APPNAME", "GolemCompiler");
            // Uncomment below to setup prefix (e.g. Bearer) for API key, if needed
            // Configuration.Default.AddApiKeyPrefix("X-GU-APPNAME", "Bearer");

            ServerPort = serverPort;

            myIP = Dns.GetHostAddresses("localhost")[0].ToString();

            golemApi = new PeerApi(hubUrl);
        }

        public void AddTask(CompilationTask task)
        {
            taskQueue.Enqueue(task);
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


                    //1. Create deployment
                    string fileName = "213fad4e020ded42e6a949f61cb660cb69bc9844";//for now it can be anything - provider uses it as a file name
                    string hash = "SHA1:"+fileName;//should be the hash of name or file - note that SHA1 or SHA3 prefix is a must
                    DeploymentSpecImage specImg = new DeploymentSpecImage(hash, "http://"+myIP+":"+ServerPort+ "/requestID/"+ fileName);
                    //create deployment
                    DeploymentSpec spec = new DeploymentSpec(EnvType.Hd, specImg, "compiler", new List<string>() {});
                    string depId = await golemApi.CreateDeploymentAsync(peers[0].NodeId, spec);
                    depId = depId.Replace("\"", "");//TODO: remove this! current bug fix
                    //TODO: based on our needs we can also create a session ID, and simply wait for peers to grab deployed tasks from there
                    //though I haven't checked that
                    //create tasks based on the CompilationTask
                    //TODO: example:
                    var results = await golemApi.UpdateDeploymentAsync(peers[0].NodeId, depId, new List<Command>() { new ExecCommand("dir", new List<string>()) });
                    //TODO: one of the tasks should be "upload_file" to send a file to our server (this needs to be implemented on the RequestServer side?)

                    //TODO: when it is done either end deployment or add more tasks...
                    golemApi.DropDeployment(peers[0].NodeId, depId);
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
            listener.Prefixes.Add("http://+:" + ServerPort + "+/requestID/");
            listener.Start();
            while (true)
            {
                try
                {
                    //get the request
                    HttpListenerContext context = await listener.GetContextAsync();
                    HttpListenerRequest request = context.Request;
                    //TODO: provider consumes data in chunks (first time it asks only for length)
                    //sencod time it asks for next chunk of data (need to parse the request to get to know this)
                    // Obtain a response object.
                    HttpListenerResponse response = context.Response;
                    //TODO:
                    //1. based on request.Url fetch the content data
                    DataPackage data = GetDataPackage(request.Url);
                    //2. calculate some hash so provider nows if content has changed or not
                    response.AddHeader("ETag", data.DataHash);
                    response.ContentLength64 = data.DataStream.Length;
                    //response.ContentType = "application/x-gzip";// - not needed
                    //response.AddHeader("Accept-Ranges", "bytes");// - not needed?
                    System.IO.Stream output = response.OutputStream;                                        
                    await output.WriteAsync(data.DataStream, 0, data.DataStream.Length);
                    // close the output stream.
                    output.Close();
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

        private DataPackage GetDataPackage(Uri url)
        {
            throw new NotImplementedException();
        }
    }
}
