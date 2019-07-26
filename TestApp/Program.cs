using GURestApi.Api;
using GURestApi.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TestApp
{
    class Program
    {
        static Thread serverThread = null;
        static CancellationTokenSource serverToken = null;
        static void Main(string[] args)
        {
            PeerApi peerApi = new PeerApi("http://10.30.10.121:6162");
            var info = peerApi.GetHubInfo();
            System.Console.WriteLine(info.ToString());

            runServer();

            var peers = peerApi.ListPeers();

            {
                string hash = "SHA1:213fad4e020ded42e6a949f61cb660cb69bc9844";
                DeploymentSpecImage specImg = new DeploymentSpecImage(hash, "http://10.30.8.234:6000/generatedID/test1.hdi");
                DeploymentSpec spec = new DeploymentSpec(EnvType.Hd, specImg, "compiler",new List<string>() { "dupa"});
                string depId = peerApi.CreateDeployment(peers[0].NodeId, spec);
                depId = depId.Replace("\"", "");
                var results = peerApi.UpdateDeployment(peers[0].NodeId, depId, new List<Command>() { new ExecCommand("test.bat",new List<string>())});
                peerApi.DropDeployment(peers[0].NodeId, depId);
                System.Console.WriteLine(depId);                
                stopServer();
                return;
            }

            //session
            {
                SessionApi sessionApi = new SessionApi("http://10.30.10.121:6162");

                //create session
                var body = new HubSession();
                long? sessionId = sessionApi.CreateSession(body);
                body = sessionApi.GetSession(sessionId);
                //add peer
                var res1 = sessionApi.AddSessionPeers(sessionId, new List<string>() { peers[0].NodeId });
                var sPeers = sessionApi.ListSessionPeers(sessionId);
                body = sessionApi.GetSession(sessionId);
                var deploymentSpec = new DeploymentInfo();
                deploymentSpec.Name = "dupa";
                string result = sessionApi.CreateDeployment(sessionId, peers[0].NodeId, deploymentSpec);
                System.Console.WriteLine(result);
            }
        }

        private static void stopServer()
        {
            serverToken.Cancel();
            serverThread.Join();

        }

        private static void runServer()
        {
            serverThread = new Thread(httpServer);
            serverToken = new CancellationTokenSource();
            serverThread.Start();
        }

        private static void httpServer()
        {
            //load file targ.gz for testing
            var fileBytes = System.IO.File.ReadAllBytes("Debug.tgz");
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add("http://+:6000/generatedID/");
            listener.Start();
            while(true)
            {
                try
                {
                    // Note: The GetContext method blocks while waiting for a request. 
                    Task<HttpListenerContext> context = listener.GetContextAsync();
                    context.Wait(serverToken.Token);
                    HttpListenerRequest request = context.Result.Request;
                    // Obtain a response object.
                    HttpListenerResponse response = context.Result.Response;
                    response.AddHeader("ETag", "675af34563dc-tr34");
                    // Construct a response.
                    // Get a response stream and write the response to it.
                    response.ContentLength64 = fileBytes.Length;
                    //response.ContentType = "application/x-gzip";
                    //response.AddHeader("Accept-Ranges", "bytes");
                    System.IO.Stream output = response.OutputStream;
                    output.Write(fileBytes, 0, fileBytes.Length);
                    // You must close the output stream.
                    output.Close();
                }
                catch(OperationCanceledException ex)
                {
                    listener.Stop();
                    return;
                }
                catch(Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }
        }
    }
}
