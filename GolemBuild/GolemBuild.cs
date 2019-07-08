using Microsoft.Build.Evaluation;
using Microsoft.Build.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace GolemBuild
{
    public class GolemBuild
    {
        public event Action<string> OnError;
        public event Action<string> OnMessage;
        public event Action OnClear;

        private List<CompilationTask> pchTasks = new List<CompilationTask>();
        private List<CompilationTask> tasks = new List<CompilationTask>();

        public bool BuildProject(string projPath, string configuration, string platform)
        {
            ProjectCollection projColl = new ProjectCollection();
            //load the project
            Project project = projColl.LoadProject(projPath);


            string projectPath = Path.GetDirectoryName(project.FullPath);
            string golemBuildPath = Path.Combine(projectPath, "GolemBuild");

            // Clear GolemBuild directory
            System.IO.DirectoryInfo di = new DirectoryInfo(golemBuildPath);

            foreach (FileInfo file in di.GetFiles())
            {
                file.Delete();
            }
            foreach (DirectoryInfo dir in di.GetDirectories())
            {
                dir.Delete(true);
            }

            OnClear.Invoke();
            OnMessage.Invoke("--- Compiling " + Path.GetFileNameWithoutExtension(projPath) + " " + configuration + " " + platform + " with GolemBuild ---");
            

            if (project != null)
            {
                project.SetGlobalProperty("Configuration", configuration);
                project.SetGlobalProperty("Platform", platform);
                project.ReevaluateIfNecessary();
                                
                //var ProjectReferences = project.Items.Where(elem => elem.ItemType == "ProjectReference");
                /*foreach (var ProjRef in ProjectReferences)
                {
                    if (ProjRef.GetMetadataValue("ReferenceOutputAssembly") == "true" || ProjRef.GetMetadataValue("LinkLibraryDependencies") == "true")
                    {
                        //Console.WriteLine(string.Format("{0} referenced by {1}.", Path.GetFileNameWithoutExtension(ProjRef.EvaluatedInclude), Path.GetFileNameWithoutExtension(proj.FullPath)));
                        EvaluateProjectReferences(Path.GetDirectoryName(proj.FullPath) + Path.DirectorySeparatorChar + ProjRef.EvaluatedInclude, evaluatedProjects, newProj);
                    }
                }
                //Console.WriteLine("Adding " + Path.GetFileNameWithoutExtension(proj.FullPath));
                evaluatedProjects.Add(newProj);*/
                CreateCompilationTasks(project);

                CallPreBuildEvents(project);

                if (pchTasks.Count > 0)
                {
                    OnMessage.Invoke("Compiling Precompiled Headers...");
                    if (!BuildPCHTasks(project))
                    {
                        OnMessage.Invoke("- Compilation failed -");
                        return false;
                    }
                }

                const bool runDistributed = true; // TODO: Figure out if we are attached to a broker or not

                if (runDistributed)
                {
                    OnMessage.Invoke("Packaging tasks...");
                    if (!PackageTasks(project))
                    {
                        OnMessage.Invoke("- Packaging failed -");
                        return false;
                    }

                    OnMessage.Invoke("Compiling packaged tasks...");
                    if (!BuildPackagedTasks(project))
                    {
                        OnMessage.Invoke("- Compiling packaged tasks failed -");
                        return false;
                    }
                }
                else
                {
                    OnMessage.Invoke("Compiling...");
                    if (!BuildTasks(project))
                    {
                        OnMessage.Invoke("- Compilation failed -");
                        return false;
                    }
                }

                CallPreLinkEvents(project);

                OnMessage.Invoke("Linking...");
                string outputFile;
                if (!LinkProject(project, out outputFile))
                {
                    OnMessage.Invoke("- Linking failed -");
                    return false;
                }

                OnMessage.Invoke("-> " + outputFile);
                OnMessage.Invoke("- Compilation successful -");

                CallPostBuildEvents(project);

                return true;
            }

            OnError?.Invoke("Could not load project " + projPath);

            return false;
        }

        private bool PackageTasks(Project project)
        {
            string projectPath = Path.GetDirectoryName(project.FullPath);
            string golemBuildPath = Path.Combine(projectPath, "GolemBuildTasks");

            Directory.CreateDirectory(golemBuildPath);

            // Clear GolemBuildTasks directory
            System.IO.DirectoryInfo di = new DirectoryInfo(golemBuildPath);

            foreach (FileInfo file in di.GetFiles())
            {
                file.Delete();
            }
            foreach (DirectoryInfo dir in di.GetDirectories())
            {
                dir.Delete(true);
            }

            // Package tasks
            for (int i = 0; i < tasks.Count; i++)
            {
                string taskPath = Path.Combine(golemBuildPath, Path.GetFileNameWithoutExtension(tasks[i].FilePath));
                Directory.CreateDirectory(taskPath);

                // Package executables and necessary dlls
                string compilerDir = Path.GetDirectoryName(tasks[i].Compiler);
                foreach (string file in Directory.EnumerateFiles(compilerDir))
                {
                    if (Path.GetExtension(file) == ".exe" || Path.GetExtension(file) == ".dll")
                    {
                        File.Copy(file, Path.Combine(taskPath, Path.GetFileName(file)));
                    }
                }

                foreach (string dir in Directory.EnumerateDirectories(compilerDir))
                {
                    if (Directory.GetFiles(dir).Length > 0)
                    {
                        string taskSubDir = Path.Combine(taskPath, Path.GetFileName(dir));
                        Directory.CreateDirectory(taskSubDir);

                        foreach (string file in Directory.EnumerateFiles(dir))
                        {
                            File.Copy(file, Path.Combine(taskSubDir, Path.GetFileName(file)));
                        }
                    }
                }

                // Package precompiled header if used
                if (tasks[i].PrecompiledHeader.Length > 0)
                {
                    string dstPchPath = Path.Combine(taskPath, tasks[i].PrecompiledHeader);
                    Directory.CreateDirectory(Path.GetDirectoryName(dstPchPath));
                    File.Copy(Path.Combine(projectPath, tasks[i].PrecompiledHeader), dstPchPath);
                }

                // Package pdb and idb if used
                if (tasks[i].PDB.Length > 0)
                {
                    string dstPdbPath = Path.Combine(taskPath, tasks[i].PDB);
                    Directory.CreateDirectory(Path.GetDirectoryName(dstPdbPath));
                    File.Copy(Path.Combine(projectPath, tasks[i].PDB), dstPdbPath); // PDB
                    File.Copy(Path.Combine(projectPath, Path.ChangeExtension(tasks[i].PDB, ".idb")), Path.ChangeExtension(dstPdbPath, ".idb")); // IDB
                }

                // Package sourcefile
                File.Copy(Path.Combine(projectPath, tasks[i].FilePath), Path.Combine(taskPath, tasks[i].FilePath));

                // Package includes
                string dstIncludePath = Path.Combine(taskPath, "includes");
                Directory.CreateDirectory(dstIncludePath);

                foreach (string include in tasks[i].Includes)
                {
                    bool foundFile = false;
                    foreach (string srcIncludePath in tasks[i].IncludeDirs)
                    {
                        string srcFilePath = Path.Combine(srcIncludePath, include);

                        if (File.Exists(srcFilePath))
                        {
                            string dstFilePath = Path.Combine(dstIncludePath, include);
                            Directory.CreateDirectory(Path.GetDirectoryName(dstFilePath));

                            File.Copy(srcFilePath, dstFilePath, true);
                            foundFile = true;
                            break;
                        }
                    }

                    if (!foundFile)
                    {
                        OnMessage.Invoke("Warning: Could not find include file " + include);
                    }
                }

                foreach (string include in tasks[i].LocalIncludes)
                {
                    if (!File.Exists(Path.Combine(projectPath, include)))
                    {
                        bool foundFile = false;
                        foreach (string srcIncludePath in tasks[i].IncludeDirs)
                        {
                            string srcFilePath = Path.Combine(srcIncludePath, include);

                            if (File.Exists(srcFilePath))
                            {
                                string dstFilePath = Path.Combine(dstIncludePath, include);
                                Directory.CreateDirectory(Path.GetDirectoryName(dstFilePath));

                                File.Copy(srcFilePath, dstFilePath, true);
                                foundFile = true;
                                break;
                            }
                        }

                        if (!foundFile)
                        {
                            OnMessage.Invoke("Warning: Could not find local include file " + include);
                        }
                    }
                }

                // Package local includes
                foreach (string include in tasks[i].LocalIncludes)
                {
                    string srcFilePath = Path.Combine(projectPath, include);
                    if (File.Exists(srcFilePath))
                    {
                        string dstFilePath = Path.Combine(taskPath, include);

                        File.Copy(srcFilePath, dstFilePath, true);
                    }
                }

                // Create output directory
                Directory.CreateDirectory(Path.Combine(taskPath, "output"));

                // Create build batch
                string batchPath = Path.Combine(taskPath, "golembuild.bat");
                StreamWriter batch = File.CreateText(batchPath);

                string compilerArgs = tasks[i].CompilerArgs;
                compilerArgs += " /I\"includes\"";
                compilerArgs += " /Fo\"" + Path.Combine("output", Path.GetFileNameWithoutExtension(tasks[i].FilePath)) +".obj\"";

                batch.WriteLine("\"" + Path.GetFileName(tasks[i].Compiler) + "\" " + compilerArgs + " " + tasks[i].FilePath); // Execute task

                // Copy pdb to output if needed
                if (tasks[i].PDB.Length > 0)
                {
                    string srcPdb = tasks[i].PDB;
                    string dstPdb = Path.Combine("output", Path.ChangeExtension(Path.GetFileName(tasks[i].FilePath), ".pdb"));
                    batch.WriteLine("copy \"" + srcPdb + "\" \"" + dstPdb + "\""); 
                }
                
                batch.Close();
                //batch.WriteLine("exit");
            }
            return true;
        }

        private bool BuildPackagedTasks(Project project)
        {
            string projectPath = Path.GetDirectoryName(project.FullPath);
            string golemBuildPath = Path.Combine(projectPath, "GolemBuild");
            Directory.CreateDirectory(golemBuildPath);

            string golemBuildTasksPath = Path.Combine(projectPath, "GolemBuildTasks");

            Process[] processes = new Process[tasks.Count];
            StreamReader[] outputs = new StreamReader[tasks.Count];
            bool[] hasFinished = new bool[tasks.Count];

            // Start building packaged tasks
            for (int i = 0; i < tasks.Count; i++)
            {
                string taskPath = Path.Combine(golemBuildTasksPath, Path.GetFileNameWithoutExtension(tasks[i].FilePath));

                processes[i] = new Process();

                processes[i].StartInfo.FileName = "cmd.exe";
                processes[i].StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                processes[i].StartInfo.UseShellExecute = false;
                processes[i].StartInfo.RedirectStandardInput = true;
                processes[i].StartInfo.RedirectStandardOutput = true;
                processes[i].StartInfo.RedirectStandardError = true;
                processes[i].StartInfo.CreateNoWindow = true;

                processes[i].Start();
                outputs[i] = processes[i].StandardOutput;

                processes[i].StandardInput.WriteLine(taskPath[0] + ":"); // Change drive
                processes[i].StandardInput.WriteLine("cd \"" + taskPath + "\""); // CD
                processes[i].StandardInput.WriteLine("golembuild"); // Build
                processes[i].StandardInput.WriteLine("exit");
            }

            // Finish all compilation processes
            bool stillRunning = true;
            bool compilationSucceeded = true;
            while (stillRunning)
            {
                bool allFinished = true;
                for (int i = 0; i < tasks.Count; i++)
                {
                    if (processes[i].HasExited)
                    {
                        if (!hasFinished[i])
                        {
                            hasFinished[i] = true;

                            bool error = false;
                            while (outputs[i].Peek() >= 0)
                            {
                                string output = outputs[i].ReadLine();
                                if (output.Contains(" error") || output.Contains("fatal error"))
                                {
                                    OnMessage.Invoke("[ERROR] " + tasks[i].FilePath + ": " + output);
                                    error = true;
                                    compilationSucceeded = false;
                                }
                            }

                            if (!error)
                            {
                                OnMessage.Invoke("[SUCCESS] " + tasks[i].FilePath);

                                // Copy files from output folder to GolemBuild folder
                                string taskPath = Path.Combine(golemBuildTasksPath, Path.GetFileNameWithoutExtension(tasks[i].FilePath));
                                string outputPath = Path.Combine(taskPath, "output");

                                foreach (string file in Directory.EnumerateFiles(outputPath))
                                {
                                    File.Copy(file, Path.Combine(golemBuildPath, Path.GetFileName(file)));
                                }
                            }
                        }
                    }
                    else
                    {
                        allFinished = false;
                    }
                }

                if (allFinished)
                    stillRunning = false;
                else
                    Thread.Sleep(25);
            }

            return compilationSucceeded;
        }

        private bool BuildPCHTasks(Project project)
        {
            string projectPath = Path.GetDirectoryName(project.FullPath);

            // Print tasks
            for (int i = 0; i < pchTasks.Count; ++i)
            {
                var task = pchTasks[i];
                OnMessage?.Invoke(string.Format("PCH Task [#{0}]: {1} {2} {3} {4}", i, task.Compiler, task.CompilerArgs, task.FilePath, task.OutputPath));
            }

            Process[] processes = new Process[pchTasks.Count];
            StreamReader[] outputs = new StreamReader[pchTasks.Count];
            bool[] hasFinished = new bool[pchTasks.Count];
            
            // Start all compilation processes
            for (int i = 0; i < pchTasks.Count; i++)
            {
                processes[i] = new Process();

                processes[i].StartInfo.FileName = "cmd.exe";//task.Compiler;
                processes[i].StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                processes[i].StartInfo.UseShellExecute = false;
                processes[i].StartInfo.RedirectStandardInput = true;
                processes[i].StartInfo.RedirectStandardOutput = true;
                processes[i].StartInfo.RedirectStandardError = true;
                processes[i].StartInfo.CreateNoWindow = true;

                processes[i].Start();
                outputs[i] = processes[i].StandardOutput;

                processes[i].StandardInput.WriteLine(projectPath[0] + ":"); // Change drive
                processes[i].StandardInput.WriteLine("cd " + projectPath); // CD

                string compilerArgs = pchTasks[i].CompilerArgs;

                string includeDirString = "";
                foreach (string includeDir in pchTasks[i].IncludeDirs)
                {
                    if (includeDir.Length > 0)
                        includeDirString += " /I \"" + includeDir + "\"";
                }
                compilerArgs += includeDirString;

                compilerArgs += " /Fp\"" + pchTasks[i].OutputPath + "\" ";
                compilerArgs += " /Fo\"" + Path.Combine(projectPath, "GolemBuild", Path.ChangeExtension(pchTasks[i].FilePath, ".obj")) + "\" ";

                processes[i].StandardInput.WriteLine("\"" + pchTasks[i].Compiler + "\" " + compilerArgs + pchTasks[i].FilePath); // Execute task
                processes[i].StandardInput.WriteLine("exit");
            }

            // Finish all compilation processes
            bool stillRunning = true;
            bool compilationSucceeded = true;
            while (stillRunning)
            {
                bool allFinished = true;
                for (int i = 0; i < pchTasks.Count; i++)
                {
                    if (processes[i].HasExited)
                    {
                        if (!hasFinished[i])
                        {
                            hasFinished[i] = true;

                            bool error = false;
                            while (outputs[i].Peek() >= 0)
                            {
                                string output = outputs[i].ReadLine();
                                if (output.Contains(" error") || output.Contains("fatal error"))
                                {
                                    OnMessage.Invoke("[ERROR] " + pchTasks[i].FilePath + ": " + output);
                                    error = true;
                                    compilationSucceeded = false;
                                }
                            }

                            if (!error)
                            {
                                OnMessage.Invoke("[SUCCESS] " + pchTasks[i].FilePath);
                            }
                        }
                    }
                    else
                    {
                        allFinished = false;
                    }
                }

                if (allFinished)
                    stillRunning = false;
                else
                    Thread.Sleep(25);
            }

            return compilationSucceeded;
        }

        private bool BuildTasks(Project project)
        {
            string projectPath = Path.GetDirectoryName(project.FullPath);

            for (int i=0;i<tasks.Count;++i)
            {
                var task = tasks[i];
                OnMessage?.Invoke(string.Format("Task [#{0}]: {1} {2} {3} {4}", i, task.Compiler, task.CompilerArgs, task.FilePath, task.OutputPath));
            }
            Process[] processes = new Process[tasks.Count];
            StreamReader[] outputs = new StreamReader[tasks.Count];
            bool[] hasFinished = new bool[tasks.Count];

            // Start all compilation processes
            for(int i = 0; i < tasks.Count; i++)
            {
                processes[i] = new Process();

                processes[i].StartInfo.FileName = "cmd.exe";//task.Compiler;
                processes[i].StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                processes[i].StartInfo.UseShellExecute = false;
                processes[i].StartInfo.RedirectStandardInput = true;
                processes[i].StartInfo.RedirectStandardOutput = true;
                processes[i].StartInfo.RedirectStandardError = true;
                processes[i].StartInfo.CreateNoWindow = true;

                processes[i].Start();
                outputs[i] = processes[i].StandardOutput;

                processes[i].StandardInput.WriteLine(projectPath[0] + ":"); // Change drive
                processes[i].StandardInput.WriteLine("cd " + projectPath); // CD

                string compilerArgs = tasks[i].CompilerArgs;

                string includeDirString = "";
                foreach (string includeDir in tasks[i].IncludeDirs)
                {
                    if (includeDir.Length > 0)
                        includeDirString += " /I \"" + includeDir + "\"";
                }
                compilerArgs += includeDirString;
                compilerArgs += " /Fo\"" + Path.Combine(projectPath, "GolemBuild", Path.ChangeExtension(tasks[i].FilePath, ".obj")) + "\" ";

                processes[i].StandardInput.WriteLine("\"" + tasks[i].Compiler + "\" " + compilerArgs + " " + tasks[i].FilePath); // Execute task
                processes[i].StandardInput.WriteLine("exit");
            }

            // Finish all compilation processes
            bool stillRunning = true;
            bool compilationSucceeded = true;
            while(stillRunning)
            {
                bool allFinished = true;
                for (int i = 0; i < tasks.Count; i++)
                {
                    if (processes[i].HasExited)
                    {
                        if (!hasFinished[i])
                        {
                            hasFinished[i] = true;

                            bool error = false;
                            while (outputs[i].Peek() >= 0)
                            {
                                string output = outputs[i].ReadLine();
                                if (output.Contains(" error") || output.Contains("fatal error"))
                                {
                                    OnMessage.Invoke("[ERROR] " + tasks[i].FilePath + ": " + output);
                                    error = true;
                                    compilationSucceeded = false;
                                }
                            }

                            if (!error)
                            {
                                OnMessage.Invoke("[SUCCESS] " + tasks[i].FilePath);
                            }
                        }
                    }
                    else
                    {
                        allFinished = false;
                    }
                }

                if (allFinished)
                    stillRunning = false;
                else
                    Thread.Sleep(25);
            }

            return compilationSucceeded;
        }

        bool LinkProject(Project project, out string outputFile)
        {
            string projectPath = Path.GetDirectoryName(project.FullPath);

            // Linking
            string configurationType = project.GetPropertyValue("ConfigurationType");
            outputFile = "";

            string VCTargetsPath = project.GetPropertyValue("VCTargetsPathEffective");
            if (string.IsNullOrEmpty(VCTargetsPath))
            {
                OnMessage.Invoke("Failed to evaluate VCTargetsPath variable on " + System.IO.Path.GetFileName(project.FullPath) + ". Is this a supported version of Visual Studio?");
                return false;
            }
            string BuildDllPath = VCTargetsPath + (VCTargetsPath.Contains("v110") ? "Microsoft.Build.CPPTasks.Common.v110.dll" : "Microsoft.Build.CPPTasks.Common.dll");
            Assembly CPPTasksAssembly = Assembly.LoadFrom(BuildDllPath);

            string linkerPath = "";
            object linkTask = null;
            string linkerOptions = "";
            if (configurationType == "StaticLibrary")
            {
                var libDefinitions = project.ItemDefinitions["Lib"];
                linkTask = Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.LIB"));
                linkerOptions = GenerateTaskCommandLine(linkTask, new string[] { "OutputFile" }, libDefinitions.Metadata);
                outputFile = libDefinitions.GetMetadataValue("OutputFile").Replace('\\', '/');
                linkerPath = GetLibPath(project);
            }
            else // Exe or DLL
            {
                var linkDefinitions = project.ItemDefinitions["Link"];
                linkTask = Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.Link"));
                linkerOptions = GenerateTaskCommandLine(linkTask, new string[] { "OutputFile", "ProfileGuidedDatabase" }, linkDefinitions.Metadata);
                outputFile = linkDefinitions.GetMetadataValue("OutputFile").Replace('\\', '/');
                linkerPath = GetLinkerPath(project);
            }

            Process linkerProcess = new Process();

            linkerProcess.StartInfo.FileName = "cmd.exe";
            linkerProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            linkerProcess.StartInfo.UseShellExecute = false;
            linkerProcess.StartInfo.RedirectStandardInput = true;
            linkerProcess.StartInfo.RedirectStandardOutput = true;
            linkerProcess.StartInfo.RedirectStandardError = true;
            linkerProcess.StartInfo.CreateNoWindow = true;

            linkerProcess.Start();
            StreamReader linkOutput = linkerProcess.StandardOutput;
            linkerProcess.StandardInput.WriteLine("\"" + GetDevCmdPath(project) + "\"");

            string linkCommand = "\"" + linkerPath + "\" " + linkerOptions + " /OUT:\"" + outputFile + "\"";

            //all pch files
            foreach (var task in pchTasks)
            {
                linkCommand += " \"" + Path.Combine(projectPath, "GolemBuild", Path.ChangeExtension(task.FilePath, ".obj")) + "\"";
            }

            //all compiled obj files
            foreach (var task in tasks)
            {
                linkCommand += " \"" + Path.Combine(projectPath, "GolemBuild", Path.ChangeExtension(task.FilePath, ".obj")) + "\"";
            }

            linkerProcess.StandardInput.WriteLine(linkCommand); // Execute task
            linkerProcess.StandardInput.WriteLine("exit");
            linkerProcess.WaitForExit();

            bool linkSuccessful = true;
            while (linkOutput.Peek() >= 0)
            {
                string output = linkOutput.ReadLine();
                if (output.Contains(" error") || output.Contains("fatal error"))
                {
                    OnMessage.Invoke("[LINK ERROR]: " + output);
                    linkSuccessful = false;
                }
            }

            return linkSuccessful;
        }

        private void FindIncludes(Project project, string fileName, List<string> includePaths, ref List<string> includes, ref List<string> localIncludes)
        {
            //WriteLineOutput("Scanning: " + fileName);

            string path = Path.Combine(Path.GetDirectoryName(project.FullPath), fileName);
            if (!File.Exists(path))
            {
                bool foundFile = false;
                foreach (string includePath in includePaths)
                {
                    path = Path.Combine(includePath, fileName);

                    if (File.Exists(path))
                    {
                        foundFile = true;
                        break;
                    }
                }

                if (!foundFile)
                {
                    OnMessage.Invoke("Could not find include: " + fileName);
                    return;
                }
            }

            System.IO.StreamReader file = new System.IO.StreamReader(path);

            bool isInMultilineComment = false;
            string line;
            while ((line = file.ReadLine()) != null)
            {
                line = line.Trim();

                // Remove // comments
                if (line.Contains("//"))
                {
                    int to = line.IndexOf("//");
                    line = line.Substring(0, to);
                }

                // Start multiline comment
                if (line.Contains("/*"))
                {
                    // End multiline comment
                    if (line.Contains("*/"))
                    {
                        int startComment = line.IndexOf("/*");
                        int endComment = line.IndexOf("*/") + 1;

                        string newLine = "";
                        if (startComment != 0)
                            newLine += line.Substring(0, startComment);
                        if (endComment < line.Length - 1)
                            newLine += line.Substring(endComment);

                        line = newLine;
                    }
                    else
                    {
                        int to = line.IndexOf("/*") + 1;
                        line = line.Substring(0, to);
                        isInMultilineComment = true;
                    }
                }
                else if (isInMultilineComment && line.Contains("*/"))
                {
                    int to = line.IndexOf("*/") + 1;
                    if (to < line.Length - 1)
                        line = line.Substring(to);
                    isInMultilineComment = false;
                }
                else if (isInMultilineComment)
                {
                    continue;
                }

                if (line.Contains("#include"))
                {
                    if (line.Contains("<") && line.Contains(">"))
                    {
                        // Angle bracket include
                        int from = line.IndexOf("<") + 1;
                        int to = line.LastIndexOf(">");
                        string includeName = line.Substring(from, to - from);


                        bool found = includes.Contains(includeName) || localIncludes.Contains(includeName);
                        
                        if (!found)
                        {
                            //WriteLineOutput("Include <" + includeName + "> found in " + fileName);
                            includes.Add(includeName);
                            FindIncludes(project, includeName, includePaths, ref includes, ref localIncludes);
                        }
                        else
                        {
                            //WriteLineOutput("Ignored <" + includeName + "> found in " + fileName);
                        }
                    }
                    else if (line.Contains("\""))
                    {
                        // Quote include
                        int from = line.IndexOf("\"") + 1;
                        int to = line.LastIndexOf("\"");
                        string includeName = line.Substring(from, to - from);

                        bool found = includes.Contains(includeName) || localIncludes.Contains(includeName);

                        if (!found)
                        {
                            //WriteLineOutput("Include <" + includeName + "> found in " + fileName);
                            localIncludes.Add(includeName);
                            FindIncludes(project, includeName, includePaths, ref includes, ref localIncludes);
                        }
                        else
                        {
                            //WriteLineOutput("Ignored <" + includeName + "> found in " + fileName);
                        }
                    }
                }
            }
        }

        private string PrintIncludes(List<string> includes)
        {
            string str = "";
            for (int i = 0; i < includes.Count; i++)
            {
                if (i != 0)
                    str += ", ";

                str += includes[i];
            }
            return str;
        }

        private void CreateCompilationTasks(Project project)
        {
            string projectPath = Path.GetDirectoryName(project.FullPath);
            //in VS2017 this semms to be the proper one
            string VCTargetsPath = project.GetPropertyValue("VCTargetsPathEffective");
            if (string.IsNullOrEmpty(VCTargetsPath))
            {
                Console.WriteLine("Failed to evaluate VCTargetsPath variable on " + System.IO.Path.GetFileName(project.FullPath) + ". Is this a supported version of Visual Studio?");
                return;
            }
            string BuildDllPath = VCTargetsPath + (VCTargetsPath.Contains("v110") ? "Microsoft.Build.CPPTasks.Common.v110.dll" : "Microsoft.Build.CPPTasks.Common.dll");
            Assembly CPPTasksAssembly = Assembly.LoadFrom(BuildDllPath);

            string compilerPath = GetCompilerPath(project);

            // Figure out include paths
            List<string> includePaths = new List<string>();
            string incPath = project.GetProperty("IncludePath").EvaluatedValue;
            string[] incPaths = incPath.Split(';');

            foreach (string path in incPaths)
            {
                includePaths.Add(path.Trim(';'));
            }

            var cItems = project.GetItems("ClCompile");

            //list preacompiled headers
            foreach (var item in cItems)
            {
                if (item.DirectMetadata.Any())
                {
                    if (item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
                    {
                        //skip
                        continue;
                    }
                    if (item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "Create").Any())
                    {
                        List<string> includes = new List<string>();
                        List<string> localIncludes = new List<string>();

                        FindIncludes(project, item.EvaluatedInclude, includePaths, ref includes, ref localIncludes);

                        OnMessage.Invoke(">> " + item.EvaluatedInclude);
                        OnMessage.Invoke("   Found " + includes.Count + " includes: " + PrintIncludes(includes));
                        OnMessage.Invoke("   Found " + localIncludes.Count + " includes: " + PrintIncludes(localIncludes));

                        var CLtask = Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
                        CLtask.GetType().GetProperty("Sources").SetValue(CLtask, new TaskItem[] { new TaskItem() });
                        string args = GenerateTaskCommandLine(CLtask, new string[] { "PrecompiledHeaderOutputFile", "ObjectFileName", "AssemblerListingLocation" }, item.Metadata);//FS or MP?

                        pchTasks.Add(new CompilationTask(item.EvaluatedInclude, compilerPath, args, "", item.GetMetadataValue("ProgramDataBaseFileName"), item.GetMetadataValue("PrecompiledHeaderOutputFile"), includePaths, includes, localIncludes));
                    }
                }
            }

            //list files to compile
            foreach (var item in cItems)
            {
                List<string> includes = new List<string>();
                List<string> localIncludes = new List<string>();

                bool ExcludePrecompiledHeader = false;
                if (item.DirectMetadata.Any())
                {
                    if (item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
                        continue;
                    if (item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "Create").Any())
                        continue;
                    if (item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "NotUsing").Any())
                        ExcludePrecompiledHeader = true;
                }

                FindIncludes(project, item.EvaluatedInclude, includePaths, ref includes, ref localIncludes);

                OnMessage.Invoke(">> " + item.EvaluatedInclude);
                OnMessage.Invoke("   Found " + includes.Count + " includes: " + PrintIncludes(includes));
                OnMessage.Invoke("   Found " + localIncludes.Count + " includes: " + PrintIncludes(localIncludes));

                var Task = Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
                Task.GetType().GetProperty("Sources").SetValue(Task, new TaskItem[] { new TaskItem() });
                string args = GenerateTaskCommandLine(Task, new string[] { "ObjectFileName", "AssemblerListingLocation" }, item.Metadata);//FS or MP?
                if (Path.GetExtension(item.EvaluatedInclude) == ".c")
                    args += " /TC";
                else
                    args += " /TP";

                //args += " /FS"; // Force synchronous PDB writes // If we ever want one single pdb file per distributed node, this is how to do it

                /*string buildPath = Path.Combine(Path.GetDirectoryName(project.FullPath), "GolemBuild");
                if (!Directory.Exists(buildPath))
                {
                    Directory.CreateDirectory(buildPath);
                }


                args += " /Fd\"GolemBuild\\" + Path.GetFileNameWithoutExtension(item.EvaluatedInclude) + "\"";*/ // Use this for having one pdb per object file, this has issues with pch

                string pch = "";
                if (pchTasks.Count > 0)
                {
                    pch = item.GetMetadataValue("PrecompiledHeaderOutputFile");
                }

                tasks.Add(new CompilationTask(item.EvaluatedInclude, compilerPath, args, pch, item.GetMetadataValue("ProgramDataBaseFileName"), "", includePaths, includes, localIncludes));
            }

            return;
        }

        private bool CallPreBuildEvents(Project project)
        {
            string projectPath = Path.GetDirectoryName(project.FullPath);
            var buildEvents = project.GetItems("PreBuildEvent");

            foreach(var buildEvent in buildEvents)
            {
                string command = buildEvent.GetMetadataValue("Command");
                if (command.Length == 0)
                    continue;

                Process eventProcess = new Process();

                eventProcess.StartInfo.FileName = "cmd.exe";
                eventProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                eventProcess.StartInfo.UseShellExecute = false;
                eventProcess.StartInfo.RedirectStandardInput = true;
                eventProcess.StartInfo.RedirectStandardOutput = true;
                eventProcess.StartInfo.RedirectStandardError = true;
                eventProcess.StartInfo.CreateNoWindow = true;

                eventProcess.Start();
                StreamReader eventOutput = eventProcess.StandardOutput;

                eventProcess.StandardInput.WriteLine("@echo off"); // Echo off
                eventProcess.StandardInput.WriteLine(projectPath[0] + ":"); // Drive
                eventProcess.StandardInput.WriteLine("cd \"" + projectPath + "\""); // CD
                eventProcess.StandardInput.WriteLine("Command:");
                eventProcess.StandardInput.WriteLine(command); // Call event
                eventProcess.StandardInput.WriteLine("exit"); // Exit

                eventProcess.WaitForExit();

                // Print results of command
                bool startCommand = false;
                while (eventOutput.Peek() >= 0)
                {
                    string line = eventOutput.ReadLine();
                    if (line.EndsWith("Command:"))
                    {
                        startCommand = true;
                        continue;
                    }

                    if (startCommand && line != "exit")
                    {
                        OnMessage.Invoke(line);
                    }
                }
            }

            return true;
        }

        private bool CallPreLinkEvents(Project project)
        {
            string projectPath = Path.GetDirectoryName(project.FullPath);
            var buildEvents = project.GetItems("PreLinkEvent");

            foreach (var buildEvent in buildEvents)
            {
                string command = buildEvent.GetMetadataValue("Command");
                if (command.Length == 0)
                    continue;

                Process eventProcess = new Process();

                eventProcess.StartInfo.FileName = "cmd.exe";
                eventProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                eventProcess.StartInfo.UseShellExecute = false;
                eventProcess.StartInfo.RedirectStandardInput = true;
                eventProcess.StartInfo.RedirectStandardOutput = true;
                eventProcess.StartInfo.RedirectStandardError = true;
                eventProcess.StartInfo.CreateNoWindow = true;

                eventProcess.Start();
                StreamReader eventOutput = eventProcess.StandardOutput;

                eventProcess.StandardInput.WriteLine("@echo off"); // Echo off
                eventProcess.StandardInput.WriteLine(projectPath[0] + ":"); // Drive
                eventProcess.StandardInput.WriteLine("cd \"" + projectPath + "\""); // CD
                eventProcess.StandardInput.WriteLine("Command:");
                eventProcess.StandardInput.WriteLine(command); // Call event
                eventProcess.StandardInput.WriteLine("exit"); // Exit

                eventProcess.WaitForExit();

                // Print results of command
                bool startCommand = false;
                while (eventOutput.Peek() >= 0)
                {
                    string line = eventOutput.ReadLine();
                    if (line.EndsWith("Command:"))
                    {
                        startCommand = true;
                        continue;
                    }

                    if (startCommand && line != "exit")
                    {
                        OnMessage.Invoke(line);
                    }
                }
            }

            return true;
        }

        private bool CallPostBuildEvents(Project project)
        {
            string projectPath = Path.GetDirectoryName(project.FullPath);
            var buildEvents = project.GetItems("PostBuildEvent");

            foreach (var buildEvent in buildEvents)
            {
                string command = buildEvent.GetMetadataValue("Command");
                if (command.Length == 0)
                    continue;

                Process eventProcess = new Process();

                eventProcess.StartInfo.FileName = "cmd.exe";
                eventProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                eventProcess.StartInfo.UseShellExecute = false;
                eventProcess.StartInfo.RedirectStandardInput = true;
                eventProcess.StartInfo.RedirectStandardOutput = true;
                eventProcess.StartInfo.RedirectStandardError = true;
                eventProcess.StartInfo.CreateNoWindow = true;

                eventProcess.Start();
                StreamReader eventOutput = eventProcess.StandardOutput;

                eventProcess.StandardInput.WriteLine("@echo off"); // Echo off
                eventProcess.StandardInput.WriteLine(projectPath[0] + ":"); // Drive
                eventProcess.StandardInput.WriteLine("cd \"" + projectPath + "\""); // CD
                eventProcess.StandardInput.WriteLine("Command:");
                eventProcess.StandardInput.WriteLine(command); // Call event
                eventProcess.StandardInput.WriteLine("exit"); // Exit

                eventProcess.WaitForExit();

                // Print results of command
                bool startCommand = false;
                while (eventOutput.Peek() >= 0)
                {
                    string line = eventOutput.ReadLine();
                    if (line.EndsWith("Command:"))
                    {
                        startCommand = true;
                        continue;
                    }

                    if (startCommand && line != "exit")
                    {
                        OnMessage.Invoke(line);
                    }
                }
            }

            return true;
        }

        private string GetCompilerPath(Project project)
        {
            var PlatformToolsetVersion = project.GetProperty("PlatformToolsetVersion").EvaluatedValue;

            string OutDir = project.GetProperty("OutDir").EvaluatedValue;
            string IntDir = project.GetProperty("IntDir").EvaluatedValue;

            var vsDir = project.GetProperty("VSInstallDir").EvaluatedValue;

            var WindowsSDKTarget = project.GetProperty("WindowsTargetPlatformVersion") != null ? project.GetProperty("WindowsTargetPlatformVersion").EvaluatedValue : "8.1";

            var sdkDir = project.GetProperty("WindowsSdkDir").EvaluatedValue;
            
            var incPath = project.GetProperty("IncludePath").EvaluatedValue;
            var libPath = project.GetProperty("LibraryPath").EvaluatedValue;
            var refPath = project.GetProperty("ReferencePath").EvaluatedValue;
            var path = project.GetProperty("Path").EvaluatedValue;
            var temp = project.GetProperty("Temp").EvaluatedValue;
            var sysRoot = project.GetProperty("SystemRoot").EvaluatedValue;

            //name depends on comilation platform and source platform
            string clPath = Path.Combine(project.GetProperty("VC_ExecutablePath_x86").EvaluatedValue, "cl.exe");
            return clPath;
        }

        private string GetLinkerPath(Project project)
        {
            //name depends on comilation platform and source platform
            string clPath = Path.Combine(project.GetProperty("VC_ExecutablePath_x86").EvaluatedValue, "link.exe");
            return clPath;
        }

        private string GetLibPath(Project project)
        {
            //name depends on comilation platform and source platform
            string clPath = Path.Combine(project.GetProperty("VC_ExecutablePath_x86").EvaluatedValue, "lib.exe");
            return clPath;
        }

        private string GetDevCmdPath(Project project)
        {
            var vsDir = project.GetProperty("VSInstallDir").EvaluatedValue;
            return Path.Combine(vsDir, "Common7", "Tools", "VsDevCmd.bat");
        }

        public string GetProjectInformation(string projectFile)
        {
            ProjectCollection pc = new ProjectCollection();
            var proj = pc.LoadProject(projectFile);
            var cItems = proj.GetItems("ClCompile");
            int excludedCount = 0;
            int precompiledHeadersCount = 0;
            int filesToCompileCount = 0;

            foreach (var item in cItems)
            {
                if (item.DirectMetadata.Any())
                {
                    if (item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
                    {
                        ++excludedCount;
                        continue;
                    }
                    if (item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "Create").Any())
                    {
                        ++precompiledHeadersCount;
                        continue;
                    }
                }
                ++filesToCompileCount;
            }
            return string.Format(CultureInfo.CurrentCulture, "Found:\n\t{0} excluded files,\n\t{1} precompiled header,\n\t{2} files to compile", excludedCount, precompiledHeadersCount, filesToCompileCount);
        }

        private string GenerateTaskCommandLine(object Task, string[] PropertiesToSkip, IEnumerable<ProjectMetadata> MetaDataList)
        {
            foreach (ProjectMetadata MetaData in MetaDataList)
            {
                if (PropertiesToSkip.Contains(MetaData.Name))
                    continue;

                var MatchingProps = Task.GetType().GetProperties().Where(prop => prop.Name == MetaData.Name);
                if (MatchingProps.Any() && !string.IsNullOrEmpty(MetaData.EvaluatedValue))
                {
                    string EvaluatedValue = MetaData.EvaluatedValue.Trim();
                    if (MetaData.Name == "AdditionalIncludeDirectories")
                    {
                        EvaluatedValue = EvaluatedValue.Replace("\\\\", "\\");
                        EvaluatedValue = EvaluatedValue.Replace(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                    }

                    PropertyInfo propInfo = MatchingProps.First();
                    if (propInfo.PropertyType.IsArray && propInfo.PropertyType.GetElementType() == typeof(string))
                    {
                        propInfo.SetValue(Task, Convert.ChangeType(EvaluatedValue.Split(';'), propInfo.PropertyType));
                    }
                    else
                    {
                        propInfo.SetValue(Task, Convert.ChangeType(EvaluatedValue, propInfo.PropertyType));
                    }
                }
            }

            var GenCmdLineMethod = Task.GetType().GetRuntimeMethods().Where(meth => meth.Name == "GenerateCommandLine").First();
            return GenCmdLineMethod.Invoke(Task, new object[] { Type.Missing, Type.Missing}) as string;
        }

    }
}
