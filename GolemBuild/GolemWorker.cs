using GURestApi.Api;
using GURestApi.Model;
using ICSharpCode.SharpZipLib.Tar;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GolemBuild
{
    class CompilerArg
    {
        public string compiler;
        public string args;
        public List<string> files = new List<string>();
        public List<string> includeDirs = new List<string>();
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

        public void ClearTasks()
        {
            taskList.Clear();
        }

        public void Dispatch(PeerApi golemApi, DeploymentSpec spec, Action onSuccess)
        {
            TaskProc(golemApi, spec).ContinueWith((task) => { onSuccess(); });
        }

        private async Task TaskProc(PeerApi golemApi, DeploymentSpec spec)
        {
            try
            {
                if (deployment == null)
                {
                    deployment = spec;
                    deploymentID = await golemApi.CreateDeploymentAsync(Peer.NodeId, deployment);
                }

                //1. Take all input files and includes and package them into one TAR package + notify HttpServer about that file
                string packedFileName = PackFilesPreProcessed(taskList);

                //2. Create command to compile those source files -> cl.exe ....
                ExecCommand compileCmd = GenerateCompileCommand(packedFileName, taskList);

                var results = await golemApi.UpdateDeploymentAsync(Peer.NodeId, deploymentID, new List<Command>() {
                    new DownloadFileCommand(Service.GetHttpDownloadUri(packedFileName), packedFileName+".tar", FileFormat.Tar),
                    compileCmd});

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
                        new UploadFileCommand(Service.GetHttpUploadUri(packedFileName), packedFileName + ".tar/output.zip") });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private ExecCommand GenerateCompileCommand(string fileName, List<CompilationTask> taskList)
        {
            return new ExecCommand(fileName + ".tar\\golembuild.bat", new List<string>());
            //return new ExecCommand("cmd.exe", new List<string> { "/k mkdir test" });//cd " + fileName + ".tar && golembuild.bat" });
        }

        private void AddFileToTar(TarArchive archive, string filePath, string entry, List<string> addedEntries)
        {
            if (addedEntries.Contains(entry.ToLower()))
                return;

            string[] splitPath = entry.Split('/','\\');

            for(int i = 1; i < splitPath.Length; i++)
            {
                string path = splitPath[0];
                for(int j = 1; j < i; j++)
                {
                    path += Path.DirectorySeparatorChar+splitPath[j];
                }

                if (addedEntries.Contains(path.ToLower()))
                    continue;

                TarEntry pathEntry = TarEntry.CreateTarEntry(path);
                pathEntry.TarHeader.Mode = 1003;
                pathEntry.TarHeader.TypeFlag = TarHeader.LF_DIR;
                pathEntry.TarHeader.Size = 0;
                archive.WriteEntry(pathEntry, false);
                addedEntries.Add(path.ToLower());
            }

            TarEntry fileEntry = TarEntry.CreateEntryFromFile(filePath);
            fileEntry.Name = entry;
            archive.WriteEntry(fileEntry, false);
            addedEntries.Add(entry.ToLower());
        }

        private string PackFiles(List<CompilationTask> taskList)
        {
            byte[] package;
            using (var memoryStream = new MemoryStream())
            {
                using (var archive = TarArchive.CreateOutputTarArchive(memoryStream))
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
                        // not needed since this file is already in the task.Includes
                        // TODO: should it be like this?
                        {
                            TarEntry entry = TarEntry.CreateEntryFromFile(task.FilePath);
                            entry.Name = Path.GetFileName(task.FilePath);
                            archive.WriteEntry(entry, false);
                        }

                        // Package includes
                        string projectPath = task.ProjectPath;
                        string dstLibIncludePath = "includes";
                        string dstProjectIncludePath = "";

                        foreach (string include in task.Includes)
                        {
                            string dstFilePath = null;
                            if (include.StartsWith(projectPath))
                            {
                                string relative = include.Replace(projectPath, "").TrimStart('\\','/');
                                dstFilePath = Path.Combine(dstProjectIncludePath, relative);
                            }
                            else
                            {
                                for (int i=0;i<task.IncludeDirs.Count;++i)
                                {
                                    string srcIncludePath = task.IncludeDirs[i];
                                    if (include.StartsWith(srcIncludePath))
                                    {
                                        string relative = include.Replace(srcIncludePath, "").TrimStart('\\', '/');
                                        dstFilePath = Path.Combine(dstLibIncludePath+i.ToString(), relative);
                                        break;
                                    }
                                }
                            }

                            AddFileToTar(archive, include, dstFilePath, addedEntries);
                        }
                    }

                    // Package build batch
                    TextWriter batch = new StreamWriter("golembuild.bat", false);

                    // CD to the directory the batch file is in
                    batch.WriteLine("cd %~DP0");
                    // Create output folder
                    batch.WriteLine("mkdir output");

                    int numberOfIncludeDirs = 0;
                    List<CompilerArg> compilerArgs = new List<CompilerArg>();
                    foreach (CompilationTask task in taskList)
                    {
                        bool found = false;
                        foreach(CompilerArg compilerArg in compilerArgs)
                        {
                            if (compilerArg.compiler == task.Compiler && compilerArg.args == task.CompilerArgs)
                            {
                                compilerArg.files.Add(Path.GetFileName(task.FilePath));
                                found = true;
                                break;
                            }
                        }

                        if (found)
                            continue;

                        numberOfIncludeDirs = Math.Max(numberOfIncludeDirs, task.IncludeDirs.Count);

                        CompilerArg newCompilerArg = new CompilerArg();
                        newCompilerArg.compiler = task.Compiler;
                        newCompilerArg.args = task.CompilerArgs;
                        newCompilerArg.files.Add(Path.GetFileName(task.FilePath));
                        compilerArgs.Add(newCompilerArg);
                    }

                    // Add compilation commands, once per CompilerArg
                    foreach(CompilerArg compilerArg in compilerArgs)
                    {
                        for(int i=0;i< numberOfIncludeDirs;++i)
                            compilerArg.args += " /I\"includes"+i.ToString()+"\" /FS";
                        compilerArg.args += " /Fo\"output/\"";
                        compilerArg.args += " /Fd\"output/" + Path.GetFileNameWithoutExtension(compilerArg.files[0]) + ".pdb\"";
                        compilerArg.args += " /MP" + TaskCapacity;

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

                string hash = GolemCache.RegisterTasksPackage(package);

                FileStream debug = new FileStream(hash + ".tar", FileMode.Create);
                debug.Write(package, 0, package.Length);
                debug.Close();

                return hash;
            }
        }

        private string PackFilesPreProcessed(List<CompilationTask> taskList)
        {
            byte[] package;
            using (var memoryStream = new MemoryStream())
            {
                using (var archive = new TarOutputStream(memoryStream))
                {
                    List<string> addedEntries = new List<string>();

                    //precompiled headers are not used in preprocessed build

                    // Package build batch
                    TextWriter batch = new StreamWriter("golembuild.bat", false);

                    // CD to the directory the batch file is in
                    batch.WriteLine("cd %~DP0");
                    // Create output folder
                    batch.WriteLine("mkdir output");

                    List<CompilerArg> compilerArgs = new List<CompilerArg>();
                    foreach (CompilationTask task in taskList)
                    {
                        bool found = false;

                        string args = task.CompilerArgs;

                        foreach (CompilerArg compilerArg in compilerArgs)
                        {
                            bool includesMatch = task.IncludeDirs.Count == compilerArg.includeDirs.Count;
                            if (includesMatch)
                            {
                                for (int i = 0; i < task.IncludeDirs.Count; ++i)
                                {
                                    if (!compilerArg.includeDirs[i].Equals(task.IncludeDirs[i]))
                                    {
                                        includesMatch = false;
                                        break;
                                    }
                                }
                            }
                            if (compilerArg.compiler == task.Compiler && compilerArg.args == args && includesMatch)
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
                        newCompilerArg.args = args;
                        newCompilerArg.files.Add(task.FilePath);
                        foreach (string e in task.IncludeDirs)
                            newCompilerArg.includeDirs.Add(e);

                        compilerArgs.Add(newCompilerArg);
                    }

                    string tempFolder = "iGolemBuild"+Peer.NodeId;
                    Directory.CreateDirectory(tempFolder);

                    //foreach compilation task, preprocess the cpp file into a temporary folder
                    foreach (CompilerArg compilerArg in compilerArgs)
                    {
                        //preprocess file, grab output, write the file as file to compile on external machine
                        Process proc = new Process();
                        string args = compilerArg.args;
                        //add includes
                        foreach (string inc in compilerArg.includeDirs)
                            args += " /I\"" + inc + "\" ";
                        //add preprocessing flag
                        args += "/P /Fi" + tempFolder + "\\ ";
                        args += "/MP" + TaskCapacity;
                        //add source files
                        foreach (string srcFile in compilerArg.files)
                            args += " " + srcFile;
                        proc.StartInfo.Arguments = args;
                        proc.StartInfo.FileName = compilerArg.compiler;
                        proc.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                        proc.StartInfo.UseShellExecute = false;
                        proc.StartInfo.RedirectStandardInput = false;
                        proc.StartInfo.RedirectStandardOutput = true;
                        proc.StartInfo.RedirectStandardError = true;
                        proc.StartInfo.CreateNoWindow = true;

                        proc.Start();

                        System.Text.StringBuilder outputStr = new System.Text.StringBuilder();

                        proc.OutputDataReceived += (sender, e) =>
                        {
                            if (e.Data != null)
                            {
                                string output = e.Data;
                                Logger.LogMessage(output);
                            }
                        };

                        proc.ErrorDataReceived += (sender, e) =>
                        {
                            if (e.Data != null)
                            {
                                outputStr.AppendLine(e.Data);
                            }
                        };
                        proc.BeginOutputReadLine();
                        proc.BeginErrorReadLine();

                        proc.WaitForExit();

                        Logger.LogMessage(outputStr.ToString());

                        if (proc.ExitCode == 0)
                        {
                            //now read back the files and add them to tar
                            foreach (string srcFile in compilerArg.files)
                            {
                                //TODO: this might be inside a folder
                                string precompiledFile = tempFolder+"\\" + Path.GetFileNameWithoutExtension(srcFile) + ".i";
                                TarEntry entry = TarEntry.CreateEntryFromFile(precompiledFile);
                                entry.Name = Path.GetFileName(srcFile);
                                archive.PutNextEntry(entry);
                                using (Stream inputStream = File.OpenRead(precompiledFile))
                                {
                                    writeStreamToTar(archive, inputStream);
                                    archive.CloseEntry();
                                }
                            }
                        }
                        else
                        {
                            Logger.LogError($"Preprocessing of file package failed");
                        }
                    }

                    Directory.Delete(tempFolder, true);

                    // Add compilation commands, once per CompilerArg
                    foreach (CompilerArg compilerArg in compilerArgs)
                    {
                        //remove precompiled header args /Yu /Fp
                        Match match = Regex.Match(compilerArg.args, "/Yu\".+?\"");
                        if (match.Success)
                            compilerArg.args = compilerArg.args.Remove(match.Index, match.Length);
                        match = Regex.Match(compilerArg.args, "/Fp\".+?\"");
                        if (match.Success)
                            compilerArg.args = compilerArg.args.Remove(match.Index, match.Length);
                        compilerArg.args += " /FS";
                        compilerArg.args += " /Fo\"output/\"";
                        compilerArg.args += " /Fd\"output/" + Path.GetFileNameWithoutExtension(compilerArg.files[0]) + ".pdb\"";
                        compilerArg.args += " /MP" + TaskCapacity;

                        batch.Write("\"../" + Path.GetFileName(compilerArg.compiler) + "\" " + compilerArg.args);

                        foreach (string file in compilerArg.files)
                        {
                            batch.Write(" " + Path.GetFileName(file));
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
                    using (Stream inputStream = File.OpenRead("golembuild.bat"))
                    {
                        batchEntry.Size = inputStream.Length;
                        archive.PutNextEntry(batchEntry);
                        writeStreamToTar(archive, inputStream);
                        archive.CloseEntry();
                    }
                }
                package = memoryStream.ToArray();

                string hash = GolemCache.RegisterTasksPackage(package);

                FileStream debug = new FileStream(hash + ".tar", FileMode.Create);
                debug.Write(package, 0, package.Length);
                debug.Close();

                return hash;
            }
        }

        private void writeStreamToTar(TarOutputStream tarOutputStream, Stream inputStream)
        {
            // this is copied from TarArchive.WriteEntryCore
            byte[] localBuffer = new byte[32 * 1024];
            while (true)
            {
                int numRead = inputStream.Read(localBuffer, 0, localBuffer.Length);
                if (numRead <= 0)
                    break;

                tarOutputStream.Write(localBuffer, 0, numRead);
            }
            tarOutputStream.Flush();
        }
    }
}
