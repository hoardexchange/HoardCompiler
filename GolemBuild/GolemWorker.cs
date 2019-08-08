using GURestApi.Api;
using GURestApi.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GolemBuild
{
    /// <summary>
    /// Worker for a particular peer.
    /// </summary>
    class GolemWorker
    {
        public PeerInfo Peer { get; set; }

        private List<CompilationTask> taskList = new List<CompilationTask>();
        private DeploymentSpec deployment = null;
        private string deploymentID = null;
        private GolemBuildService Service = null;

        public int TaskCapacity
        {
            get
            {
                return 4;//TODO: use value from PeerInfo
            }
        }

        public GolemWorker(GolemBuildService service, PeerInfo peer)
        {
            Service = service;
            Peer = peer;
        }

        public void AddTask(CompilationTask task)
        {
            taskList.Add(task);
        }

        public void Dispatch(PeerApi golemApi, DeploymentSpec spec, Action onSuccess)
        {
            TaskProc(golemApi, spec).ContinueWith((task) => { onSuccess(); });
        }

        private async Task TaskProc(PeerApi golemApi, DeploymentSpec spec)
        {
            try
            {
                if (deployment != spec)
                {
                    //either there is no deployment, or a change is requested
                    if (deployment!=null)
                    {
                        golemApi.DropDeployment(Peer.NodeId, deploymentID);
                        deployment = null;
                        deploymentID = null;
                    }
                    deployment = spec;
                    deploymentID = await golemApi.CreateDeploymentAsync(Peer.NodeId, deployment);
                }

                //TODO: gather all tasks into one task with more than one file
                //TODO: this can be done automatically during AddTask()
                string fileName = CombineTasks();

                var results = await golemApi.UpdateDeploymentAsync(Peer.NodeId, deploymentID, new List<Command>() {
                    new DownloadFileCommand(Service.GetHttpDownloadUri(fileName), "output.zip"),
                    new ExecCommand("golembuild.bat", new List<string>()) });

                bool error = false;
                string[] lines = results[0].Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                foreach (string line in lines)
                {
                    if (line.Contains(" error") || line.Contains("fatal error"))
                    {
                        Service.LogMessage("[ERROR] " + fileName + ": " + line);
                        error = true;
                    }
                    else if (line.Contains("warning"))
                    {
                        Service.LogMessage("[WARNING] " + fileName + ": " + line);
                    }
                }

                if (!error)
                {
                    Service.LogMessage("[SUCCESS] " + fileName);

                    // Upload output.zip
                    results = await golemApi.UpdateDeploymentAsync(Peer.NodeId, deploymentID, new List<Command>() { new UploadFileCommand(Service.GetHttpUploadUri(fileName), "output.zip") });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private string CombineTasks()
        {
            throw new NotImplementedException();
        }
    }
}
