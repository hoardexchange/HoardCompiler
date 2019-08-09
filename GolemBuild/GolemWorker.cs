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

        private PeerHardware Hardware { get; set; }

        private List<CompilationTask> taskList = new List<CompilationTask>();
        private DeploymentSpec deployment = null;
        private string deploymentID = null;
        private GolemBuildService Service = null;

        public int TaskCapacity
        {
            get
            {
                return Hardware.CoreNumber;
            }
        }

        public GolemWorker(GolemBuildService service, PeerInfo peer, PeerHardware hardware)
        {
            Service = service;
            Peer = peer;
            Hardware = hardware;
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

                //1. Take all input files and includes and package them into one TAR package + notify HttpServer about that file
                string packedFileName = PackFiles(taskList);

                //2. Create command to compile those source files -> cl.exe ....
                ExecCommand ecmd = GenerateCompileCommand(taskList);

                var results = await golemApi.UpdateDeploymentAsync(Peer.NodeId, deploymentID, new List<Command>() {
                    new DownloadFileCommand(Service.GetHttpDownloadUri(packedFileName), packedFileName+".tar", FileFormat.Tar),
                    ecmd });

                bool error = false;
                string[] lines = results[0].Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                foreach (string line in lines)
                {
                    if (line.Contains(" error") || line.Contains("fatal error"))
                    {
                        Service.LogMessage("[ERROR] " + packedFileName + ": " + line);
                        error = true;
                    }
                    else if (line.Contains("warning"))
                    {
                        Service.LogMessage("[WARNING] " + packedFileName + ": " + line);
                    }
                }

                if (!error)
                {
                    Service.LogMessage("[SUCCESS] " + packedFileName);

                    // Upload output.zip
                    results = await golemApi.UpdateDeploymentAsync(Peer.NodeId, deploymentID, new List<Command>() {
                        new UploadFileCommand(Service.GetHttpUploadUri(packedFileName), packedFileName+".zip") });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private ExecCommand GenerateCompileCommand(List<CompilationTask> taskList)
        {
            throw new NotImplementedException();
        }

        private string PackFiles(List<CompilationTask> taskList)
        {
            throw new NotImplementedException();
        }
    }
}
