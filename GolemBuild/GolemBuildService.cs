using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using GURestApi.Api;
using GURestApi.Client;

namespace GolemBuild
{
    public class GolemBuildService : IBuildService
    {
        public event EventHandler<BuildTaskStatusChangedArgs> BuildTaskStatusChanged;

        private Task mainLoop = null;
        private System.Threading.CancellationTokenSource cancellationSource = null;
        private ConcurrentQueue<CompilationTask> taskQueue = null;

        private PeerApi golemApi = null;
        
        public GolemBuildService(string hubUrl)
        {
            // Configure API key authorization: serviceToken
            Configuration.Default.AddApiKey("X-GU-APIKEY", "YOUR_API_KEY");
            // Uncomment below to setup prefix (e.g. Bearer) for API key, if needed
            // Configuration.Default.AddApiKeyPrefix("X-GU-APIKEY", "Bearer");
            // Configure API key authorization: systemName
            Configuration.Default.AddApiKey("X-GU-APPNAME", "GolemCompiler");
            // Uncomment below to setup prefix (e.g. Bearer) for API key, if needed
            // Configuration.Default.AddApiKeyPrefix("X-GU-APPNAME", "Bearer");

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

            mainLoop = Task.Run(TaskProcessor, cancellationSource.Token);

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
            var hubInfo = await golemApi.GetHubInfoAsync();

            while (true)
            {
                CompilationTask task = null;
                if (taskQueue.TryDequeue(out task))
                {
                    var peers = await golemApi.ListPeersAsync();

                    //we want each task to be distributed to a different peer
                    //not that number of peers might change in time
                    //we might first grab all tasks and then grab all peers and than distribute tasks for those that are not occupied

                    //1. Create deployment
                    //2. session?
                    //3. send task
                    //4. grab result

                    //golemApi.CreateDeploymentAsync(hubInfo.)
                    //golemClient.CallApiAsync()
                    //TODO: do sth with this task
                }
                await Task.Delay(1000);
            }
        }
    }
}
