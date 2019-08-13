using GURestApi.Api;
using GURestApi.Model;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GolemBuild
{
    class CompilerArg
    {
        public string compiler;
        public string args;
        public List<string> files = new List<string>();
    }

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
                return Hardware.CoreCount;
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
                ExecCommand compileCmd = GenerateCompileCommand(packedFileName, taskList);

                var results = await golemApi.UpdateDeploymentAsync(Peer.NodeId, deploymentID, new List<Command>() {
                    new DownloadFileCommand(Service.GetHttpDownloadUri(packedFileName), packedFileName+".tar", FileFormat.Tar)});

                bool error = false;
                string[] lines = results[0].Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                foreach (string line in lines)
                {
                    if (line.Contains(" error") || line.Contains("fatal error"))
                    {
                        Logger.LogError("[ERROR] " + packedFileName + ": " + line);
                        error = true;
                    }
                    else if (line.Contains("warning"))
                    {
                        Logger.LogMessage("[WARNING] " + packedFileName + ": " + line);
                    }
                }

                if (!error)
                {
                    Logger.LogMessage("[SUCCESS] " + packedFileName);

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

        private ExecCommand GenerateDecompressCommand(string fileName, List<CompilationTask> taskList)
        {
            return new ExecCommand("powershell.exe -nologo -noprofile -command \" & { Add - Type - A 'System.IO.Compression.FileSystem'; [IO.Compression.ZipFile]::ExtractToDirectory('" + fileName + ".zip', '" + fileName + "'); }", new List<string>());
        }

        private ExecCommand GenerateCompileCommand(string fileName, List<CompilationTask> taskList)
        {
            return new ExecCommand("cd " + fileName + " && golembuild.bat", new List<string>());
        }

        private string PackFiles(List<CompilationTask> taskList)
        {
            byte[] package;
            using (var memoryStream = new MemoryStream())
            {
                using (var gzoStream = new GZipOutputStream(memoryStream))
                using (var archive = TarArchive.CreateOutputTarArchive(gzoStream))
                {
                    List<string> addedEntries = new List<string>();

                    // Package precompiled header if used
                    foreach (CompilationTask task in taskList)
                    {
                        if (task.PrecompiledHeader.Length > 0)
                        {
                            TarEntry entry = TarEntry.CreateEntryFromFile(task.PrecompiledHeader);
                            entry.Name = Path.GetFileName(task.PrecompiledHeader);
                            archive.WriteEntry(entry, false);
                            break;
                        }
                    }

                    foreach (CompilationTask task in taskList)
                    {
                        // Package sourcefiles
                        {
                            TarEntry entry = TarEntry.CreateEntryFromFile(task.FilePath);
                            entry.Name = Path.GetFileName(task.FilePath);
                            archive.WriteEntry(entry, false);
                        }
                        
                        // Package includes
                        foreach (string include in task.Includes)
                        {
                            foreach (string srcIncludePath in task.IncludeDirs)
                            {
                                string srcFilePath = Path.Combine(srcIncludePath, include);
                                if (File.Exists(srcFilePath))
                                {
                                    string entry = Path.Combine("includes", include);
                                    if (!addedEntries.Contains(entry.ToLower()))
                                    {
                                        TarEntry includeEntry = TarEntry.CreateEntryFromFile(srcFilePath);
                                        includeEntry.Name = "includes/" + srcFilePath.Replace(srcIncludePath, "");
                                        archive.WriteEntry(includeEntry, false);
                                        addedEntries.Add(entry.ToLower());
                                    }
                                }
                            }
                        }

                        // Package local includes from include folders
                        foreach (string include in task.LocalIncludes)
                        {
                            foreach (string srcIncludePath in task.IncludeDirs)
                            {
                                string srcFilePath = Path.Combine(srcIncludePath, include);
                                if (File.Exists(srcFilePath))
                                {
                                    string entry = Path.Combine("includes", include);
                                    if (!addedEntries.Contains(entry.ToLower()))
                                    {
                                        TarEntry includeEntry = TarEntry.CreateEntryFromFile(srcFilePath);
                                        includeEntry.Name = "includes/" + srcFilePath.Replace(srcIncludePath, "");
                                        archive.WriteEntry(includeEntry, false);
                                        addedEntries.Add(entry.ToLower());
                                    }
                                }
                            }
                        }

                        // Package local includes from project folder
                        foreach (string include in task.LocalIncludes)
                        {
                            string includePath = Path.Combine(task.ProjectPath, include);
                            if (File.Exists(includePath))
                            {
                                string entry = Path.Combine("includes", include);
                                if (!addedEntries.Contains(entry.ToLower()))
                                {
                                    TarEntry includeEntry = TarEntry.CreateEntryFromFile(includePath);
                                    includeEntry.Name = "includes/" + includePath.Replace(includePath, "");
                                    archive.WriteEntry(includeEntry, false);
                                    addedEntries.Add(entry.ToLower());
                                }
                            }
                        }
                    }

                    // Package build batch
                    TextWriter batch = new StreamWriter("golembuild.bat", false);

                    // Create output folder
                    batch.WriteLine("mkdir output"); 

                    List<CompilerArg> compilerArgs = new List<CompilerArg>();
                    foreach (CompilationTask task in taskList)
                    {
                        bool found = false;
                        foreach(CompilerArg compilerArg in compilerArgs)
                        {
                            if (compilerArg.compiler == task.Compiler && compilerArg.args == task.CompilerArgs)
                            {
                                compilerArg.files.Add(task.FilePath);
                                found = true;
                                break;
                            }
                        }

                        if (found)
                            continue;

                        CompilerArg newCompilerArg = new CompilerArg();
                        newCompilerArg.compiler = task.Compiler;
                        newCompilerArg.args = task.CompilerArgs;
                        newCompilerArg.files.Add(Path.GetFileName(task.FilePath));
                        compilerArgs.Add(newCompilerArg);
                    }

                    // Add compilation commands, once per CompilerArg
                    foreach(CompilerArg compilerArg in compilerArgs)
                    {
                        compilerArg.args += " /I\"includes\" /FS";
                        compilerArg.args += " /Fo\"output/\"";
                        compilerArg.args += " /Fd\"output/\"" + Path.GetFileNameWithoutExtension(compilerArg.files[0]) + ".pdb";

                        batch.Write("\"../" + Path.GetFileName(compilerArg.compiler) + "\" " + compilerArg.args);

                        foreach(string file in compilerArg.files)
                        {
                            batch.Write(" " + file);
                        }
                        batch.WriteLine();
                    }

                    // Zip output folder
                    batch.WriteLine("powershell.exe -nologo -noprofile -command \"& { Add-Type -A 'System.IO.Compression.FileSystem'; [IO.Compression.ZipFile]::CreateFromDirectory('output', 'output.zip'); }\"");

                    // stop the service
                    batch.WriteLine("\"../mspdbsrv.exe\" -stop");
                    batch.WriteLine("exit 0");//assume no error

                    batch.Close();

                    TarEntry batchEntry = TarEntry.CreateEntryFromFile("golembuild.bat");
                    batchEntry.Name = "golembuild.bat";
                    archive.WriteEntry(batchEntry, false);
                }
                package = memoryStream.ToArray();

                //FileStream debug = new FileStream("Debug.tar.gz", FileMode.Create);
                //debug.Write(package, 0, package.Length);
                //debug.Close();

                return GolemCache.RegisterTasksPackage(package);
            }
        }
    }
}
