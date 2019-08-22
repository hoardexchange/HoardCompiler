using ICSharpCode.SharpZipLib.Tar;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

namespace GolemBuild
{
    public class GolemBuild
    {
        const bool runDistributed = true; // TODO: Figure out if we are attached to a broker or not
        const bool runVerbose = false; // TODO: Add this as a checkbox somewhere

        private List<CompilationTask> pchTasks = new List<CompilationTask>();
        private List<CompilationTask> tasks = new List<CompilationTask>();
        private List<CustomBuildTask> customBuildTasks = new List<CustomBuildTask>();
        private List<CustomBuildTask> customBuildTasksParallel = new List<CustomBuildTask>();

        public static string golemBuildTasksPath = "";

        public bool BuildProject(string projPath, string configuration, string platform)
        {
            ProjectCollection projColl = new ProjectCollection();
            //load the project
            Project project = projColl.LoadProject(projPath);

            string projectPath = Path.GetDirectoryName(project.FullPath);
            string golemBuildPath = Path.Combine(projectPath, "GolemBuild");
            Directory.CreateDirectory(golemBuildPath);

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

            Logger.LogMessage("--- Compiling " + Path.GetFileNameWithoutExtension(projPath) + " " + configuration + " " + platform + " with GolemBuild ---");
            

            if (project != null)
            {
                project.SetGlobalProperty("Configuration", configuration);
                project.SetGlobalProperty("Platform", platform);
                project.ReevaluateIfNecessary();
                                
                CreateCompilationTasks(project, platform);

                CallPreBuildEvents(project);

                if (customBuildTasks.Count > 0)
                {
                    Logger.LogMessage("Running Custom Build Tasks...");
                    if (!CustomBuildTasks(project))
                    {
                        Logger.LogError("- Custom Build Tasks failed -");
                        return false;
                    }
                }

                if (pchTasks.Count > 0)
                {
                    Logger.LogMessage("Compiling Precompiled Headers...");
                    if (!BuildPCHTasks(project))
                    {
                        Logger.LogError("- Compilation failed -");
                        return false;
                    }
                }

                if (runDistributed)
                {
                    /*Logger.LogMessage("Packaging tasks...");
                    if (!PackageTasks(project))
                    {
                        Logger.LogError("- Packaging failed -");
                        return false;
                    }*/

                    if (tasks.Count > 0)
                    {
                        Logger.LogMessage("Queueing tasks...");
                        if (!QueuePackagedTasks(project))
                        {
                            Logger.LogError("- Queueing failed -");
                            return false;
                        }

                        Logger.LogMessage("Waiting for external build...");
                        if (!GolemBuildService.Instance.WaitTasks())
                        {
                            Logger.LogError("- External Compilation failed -");
                            return false;
                        }
                    }
                }
                else
                {
                    Logger.LogMessage("Compiling...");
                    if (!BuildTasks(project))
                    {
                        Logger.LogError("- Compilation failed -");
                        return false;
                    }
                }

                CallPreLinkEvents(project);

                if (tasks.Count > 0)
                {
                    Logger.LogMessage("Linking...");
                    string outputFile;
                    if (!LinkProject(project, platform, out outputFile))
                    {
                        Logger.LogError("- Linking failed -");
                        return false;
                    }

                    Logger.LogMessage("-> " + outputFile);
                }
                Logger.LogMessage("- Compilation successful -");

                CallPostBuildEvents(project);

                return true;
            }

            Logger.LogError("Could not load project " + projPath);

            return false;
        }
        
        private void AddDirectoryFilesToTar(TarArchive tarArchive, string sourceDirectory, bool recurse)
        {
            // Optionally, write an entry for the directory itself.
            // Specify false for recursion here if we will add the directory's files individually.
            TarEntry tarEntry = TarEntry.CreateEntryFromFile(sourceDirectory);
            tarArchive.WriteEntry(tarEntry, false);

            // Write each file to the tar.
            string[] filenames = Directory.GetFiles(sourceDirectory);
            foreach (string filename in filenames)
            {
                tarEntry = TarEntry.CreateEntryFromFile(filename);
                tarArchive.WriteEntry(tarEntry, true);
            }

            if (recurse)
            {
                string[] directories = Directory.GetDirectories(sourceDirectory);
                foreach (string directory in directories)
                    AddDirectoryFilesToTar(tarArchive, directory, recurse);
            }
        }

        private bool QueuePackagedTasks(Project project)
        {
            string projectPath = Path.GetDirectoryName(project.FullPath);
            string golemBuildPath = Path.Combine(projectPath, "GolemBuild");
            GolemBuildService.Instance.BuildPath = golemBuildPath;

            GolemBuildService.Instance.compilationSuccessful = true;

            // Start building packaged tasks
            for (int i = 0; i < tasks.Count; i++)
            {
                GolemBuildService.Instance.AddTask(tasks[i]);
            }
            return true;
        }

        private bool BuildPackagedTasks(Project project)
        {
            string projectPath = Path.GetDirectoryName(project.FullPath);
            string golemBuildPath = Path.Combine(projectPath, "GolemBuild");

            string golemBuildTasksPath = Path.Combine(projectPath, "GolemBuildTasks");

            Process[] processes = new Process[tasks.Count];
            bool[] hasFinished = new bool[tasks.Count];
            bool[] hasErrored = new bool[tasks.Count];
            bool compilationSucceeded = true;

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
                
                int index = i; // Make copy of i, else the lambda captures are wrong...
                processes[i].OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        string output = e.Data;
                        if (output.Contains(" error") || output.Contains("fatal error"))
                        {
                            Logger.LogError("[ERROR] " + tasks[index].FilePath + ": " + output);
                            hasErrored[index] = true;
                            compilationSucceeded = false;
                        }
                        else if (output.Contains("warning"))
                        {
                            Logger.LogMessage("[WARNING] " + tasks[index].FilePath + ": " + output);
                        }
                    }
                };

                processes[i].ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        string output = e.Data;
                        Logger.LogError("[ERROR] " + tasks[index].FilePath + ": " + output);
                        hasErrored[index] = true;
                        compilationSucceeded = false;
                    }
                };
                processes[i].BeginOutputReadLine();
                processes[i].BeginErrorReadLine();

                processes[i].StandardInput.WriteLine("@echo off");
                processes[i].StandardInput.WriteLine(taskPath[0] + ":"); // Change drive
                processes[i].StandardInput.WriteLine("cd \"" + taskPath + "\""); // CD
                processes[i].StandardInput.WriteLine("golembuild"); // Build
                processes[i].StandardInput.WriteLine("exit");
            }

            // Finish all compilation processes
            bool stillRunning = true;
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
                            if (!hasErrored[i])
                            {
                                Logger.LogMessage("[SUCCESS] " + tasks[i].FilePath);

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
                Logger.LogMessage(string.Format("PCH Task [#{0}]: {1} {2} {3} {4}", i, task.Compiler, task.CompilerArgs, task.FilePath, task.OutputPath));
            }

            Process[] processes = new Process[pchTasks.Count];
            bool[] hasFinished = new bool[pchTasks.Count];
            bool[] hasErrored = new bool[pchTasks.Count];
            bool compilationSucceeded = true;

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

                int index = i; // Make copy of i, else the lambda captures are wrong...
                processes[i].OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        string output = e.Data;
                        if (output.Contains(" error") || output.Contains("fatal error"))
                        {
                            Logger.LogError("[ERROR] " + pchTasks[index].FilePath + ": " + output);
                            hasErrored[index] = true;
                            compilationSucceeded = false;
                        }
                        else if (output.Contains("warning"))
                        {
                            Logger.LogMessage("[WARNING] " + pchTasks[index].FilePath + ": " + output);
                        }
                    }
                };

                processes[i].ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        string output = e.Data;
                        Logger.LogError("[ERROR] " + pchTasks[index].FilePath + ": " + output);
                        hasErrored[index] = true;
                        compilationSucceeded = false;
                    }
                };
                processes[i].BeginOutputReadLine();
                processes[i].BeginErrorReadLine();

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

                Directory.CreateDirectory(Path.Combine(projectPath, Path.GetDirectoryName(pchTasks[i].OutputPath)));
                compilerArgs += " /Fp\"" + pchTasks[i].OutputPath + "\" ";
                compilerArgs += " /Fo\"" + Path.Combine(projectPath, "GolemBuild", Path.ChangeExtension(pchTasks[i].FilePath, ".obj")) + "\" ";

                processes[i].StandardInput.WriteLine("\"" + pchTasks[i].Compiler + "\" " + compilerArgs + pchTasks[i].FilePath); // Execute task
                processes[i].StandardInput.WriteLine("exit");
            }

            // Finish all compilation processes
            bool stillRunning = true;
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

                            if (!hasErrored[i])
                            {
                                Logger.LogMessage("[SUCCESS] " + pchTasks[i].FilePath);
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

        private bool CustomBuildTasks(Project project)
        {
            string projectPath = Path.GetDirectoryName(project.FullPath);
            string golemBuildPath = Path.Combine(projectPath, "GolemBuild");

            // Sequential custom build tasks
            for (int i = 0; i < customBuildTasks.Count; i++)
            {
                bool shouldDebugLog = false;

                // Create batch file
                string batchPath = Path.Combine(golemBuildPath, "customBuildTask" + i + ".bat");
                StreamWriter batch = File.CreateText(batchPath);
                batch.WriteLine("@echo off");
                batch.WriteLine(customBuildTasks[i].Command);
                batch.Close();

                Process process = new Process();

                process.StartInfo.FileName = "cmd.exe";
                process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardInput = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;

                process.Start();

                if (shouldDebugLog)
                {
                    Logger.LogError("[DEBUG] " + batchPath);

                    int index = i; // Make copy of i, else the lambda captures are wrong...
                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            string output = e.Data;
                            if (output.Contains(" error") || output.Contains("fatal error"))
                            {
                                Logger.LogError("[ERROR] " + customBuildTasks[index].FilePath + ": " + output);
                            }
                            else if (output.Contains("warning"))
                            {
                                Logger.LogMessage("[WARNING] " + customBuildTasks[index].FilePath + ": " + output);
                            }
                        }
                    };

                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            string output = e.Data;
                            Logger.LogError("[ERROR] " + customBuildTasks[index].FilePath + ": " + output);
                        }
                    };
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (customBuildTasks[i].Message.Length > 0)
                    Logger.LogMessage(customBuildTasks[i].Message);
                else
                    Logger.LogMessage("CustomBuildTask" + i);

                process.StandardInput.WriteLine("@echo off"); // Echo off
                process.StandardInput.WriteLine(projectPath[0] + ":"); // Change drive
                process.StandardInput.WriteLine("cd \"" + projectPath + "\""); // CD
                process.StandardInput.WriteLine("\"" + batchPath + "\""); // Execute task
                process.StandardInput.WriteLine("exit");

                process.WaitForExit();
            }

            // Parallel custom build tasks
            Process[] processes = new Process[customBuildTasksParallel.Count];
            bool[] hasFinished = new bool[customBuildTasksParallel.Count];

            // Start all custom build processes
            for (int i = 0; i < customBuildTasksParallel.Count; i++)
            {
                bool shouldDebugLog = false;

                // Create batch file
                string batchPath = Path.Combine(golemBuildPath, "customBuildTaskParallel" + i + ".bat");
                StreamWriter batch = File.CreateText(batchPath);
                batch.WriteLine("@echo off");
                batch.WriteLine(customBuildTasksParallel[i].Command);
                batch.Close();

                processes[i] = new Process();

                processes[i].StartInfo.FileName = "cmd.exe";
                processes[i].StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                processes[i].StartInfo.UseShellExecute = false;
                processes[i].StartInfo.RedirectStandardInput = true;
                processes[i].StartInfo.RedirectStandardOutput = true;
                processes[i].StartInfo.RedirectStandardError = true;
                processes[i].StartInfo.CreateNoWindow = true;

                processes[i].Start();

                if (shouldDebugLog)
                {
                    Logger.LogError("[DEBUG] " + batchPath);

                    int index = i; // Make copy of i, else the lambda captures are wrong...
                    processes[i].OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            string output = e.Data;
                            if (output.Contains(" error") || output.Contains("fatal error"))
                            {
                                Logger.LogError("[ERROR] " + tasks[index].FilePath + ": " + output);
                            }
                            else if (output.Contains("warning"))
                            {
                                Logger.LogMessage("[WARNING] " + tasks[index].FilePath + ": " + output);
                            }
                        }
                    };

                    processes[i].ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            string output = e.Data;
                            Logger.LogError("[ERROR] " + tasks[index].FilePath + ": " + output);
                        }
                    };
                }

                processes[i].BeginOutputReadLine();
                processes[i].BeginErrorReadLine();

                if (customBuildTasksParallel[i].Message.Length > 0)
                    Logger.LogMessage("[STARTING] " + customBuildTasksParallel[i].Message);
                else
                    Logger.LogMessage("[STARTING] CustomBuildTaskParallel" + i);

                processes[i].StandardInput.WriteLine("@echo off"); // Echo off
                processes[i].StandardInput.WriteLine(projectPath[0] + ":"); // Change drive
                processes[i].StandardInput.WriteLine("cd \"" + projectPath + "\""); // CD
                processes[i].StandardInput.WriteLine("\"" + batchPath + "\""); // Execute task
                processes[i].StandardInput.WriteLine("exit");
            }

            // Finish all compilation processes
            bool stillRunning = true;
            while (stillRunning)
            {
                bool allFinished = true;
                for (int i = 0; i < customBuildTasksParallel.Count; i++)
                {
                    if (processes[i].HasExited)
                    {
                        if (!hasFinished[i])
                        {
                            hasFinished[i] = true;

                            if (customBuildTasksParallel[i].Message.Length > 0)
                                Logger.LogMessage("[SUCCESS] " + customBuildTasksParallel[i].Message);
                            else
                                Logger.LogMessage("[SUCCESS] CustomBuildTaskParallel" + i);
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

            return true;
        }

        private bool BuildTasks(Project project)
        {
            string projectPath = Path.GetDirectoryName(project.FullPath);

            for (int i=0;i<tasks.Count;++i)
            {
                var task = tasks[i];
                Logger.LogMessage(string.Format("Task [#{0}]: {1} {2} {3} {4}", i, task.Compiler, task.CompilerArgs, task.FilePath, task.OutputPath));
            }
            Process[] processes = new Process[tasks.Count];
            bool[] hasFinished = new bool[tasks.Count];
            bool[] hasErrored = new bool[tasks.Count];
            bool compilationSucceeded = true;

            // Start all compilation processes
            for (int i = 0; i < tasks.Count; i++)
            {
                processes[i] = new Process();

                processes[i].StartInfo.FileName = "cmd.exe";
                processes[i].StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                processes[i].StartInfo.UseShellExecute = false;
                processes[i].StartInfo.RedirectStandardInput = true;
                processes[i].StartInfo.RedirectStandardOutput = true;
                processes[i].StartInfo.RedirectStandardError = true;
                processes[i].StartInfo.CreateNoWindow = true;

                processes[i].Start();
                int index = i; // Make copy of i, else the lambda captures are wrong...
                processes[i].OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        string output = e.Data;
                        if (output.Contains(" error") || output.Contains("fatal error"))
                        {
                            Logger.LogError("[ERROR] " + tasks[index].FilePath + ": " + output);
                            hasErrored[index] = true;
                            compilationSucceeded = false;
                        }
                        else if (output.Contains("warning"))
                        {
                            Logger.LogMessage("[WARNING] " + tasks[index].FilePath + ": " + output);
                        }
                    }
                };

                processes[i].ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        string output = e.Data;
                        Logger.LogError("[ERROR] " + tasks[index].FilePath + ": " + output);
                        hasErrored[index] = true;
                        compilationSucceeded = false;
                    }
                };
                processes[i].BeginOutputReadLine();
                processes[i].BeginErrorReadLine();

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

                            if (!hasErrored[i])
                            {
                                Logger.LogMessage("[SUCCESS] " + tasks[i].FilePath);
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

        bool LinkProject(Project project, string platform, out string outputFile)
        {
            string projectPath = Path.GetDirectoryName(project.FullPath);

            // Linking
            string configurationType = project.GetPropertyValue("ConfigurationType");
            outputFile = "";

            string VCTargetsPath = project.GetPropertyValue("VCTargetsPathEffective");
            if (string.IsNullOrEmpty(VCTargetsPath))
            {
                Logger.LogError("Failed to evaluate VCTargetsPath variable on " + System.IO.Path.GetFileName(project.FullPath) + ". Is this a supported version of Visual Studio?");
                return false;
            }
            string BuildDllPath = VCTargetsPath + (VCTargetsPath.Contains("v110") ? "Microsoft.Build.CPPTasks.Common.v110.dll" : "Microsoft.Build.CPPTasks.Common.dll");
            Assembly CPPTasksAssembly = Assembly.LoadFrom(BuildDllPath);

            string linkerPath = "";
            object linkTask = null;
            string linkerOptions = "";
            string importLibrary = "";
            if (configurationType == "StaticLibrary")
            {
                var libDefinitions = project.ItemDefinitions["Lib"];
                linkTask = Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.LIB"));
                linkerOptions = GenerateTaskCommandLine(linkTask, new string[] { "OutputFile" }, libDefinitions.Metadata);
                outputFile = libDefinitions.GetMetadataValue("OutputFile").Replace('\\', '/');
                linkerPath = GetLibPath(project, platform);
            }
            else // Exe or DLL
            {
                var linkDefinitions = project.ItemDefinitions["Link"];
                linkTask = Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.Link"));
                linkerOptions = GenerateTaskCommandLine(linkTask, new string[] { "OutputFile", "ProfileGuidedDatabase" }, linkDefinitions.Metadata);
                outputFile = linkDefinitions.GetMetadataValue("OutputFile").Replace('\\', '/');
                linkerPath = GetLinkerPath(project, platform);
                
                if (configurationType == "DynamicLibrary")
                {
                    importLibrary = linkDefinitions.GetMetadataValue("ImportLibrary").Replace('\\', '/');
                }
            }

            Directory.CreateDirectory(Path.Combine(projectPath, Path.GetDirectoryName(outputFile)));

            bool linkSuccessful = true;

            Process linkerProcess = new Process();

            linkerProcess.StartInfo.FileName = "cmd.exe";
            linkerProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            linkerProcess.StartInfo.UseShellExecute = false;
            linkerProcess.StartInfo.RedirectStandardInput = true;
            linkerProcess.StartInfo.RedirectStandardOutput = true;
            linkerProcess.StartInfo.RedirectStandardError = true;
            linkerProcess.StartInfo.CreateNoWindow = true;

            linkerProcess.Start();

            linkerProcess.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    string output = e.Data;
                    if (output.Contains(" error") || output.Contains("fatal error"))
                    {
                        Logger.LogError("[LINK ERROR] " + output);
                        linkSuccessful = false;
                    }
                }
            };

            linkerProcess.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    string output = e.Data;
                    Logger.LogError("[LINK ERROR] " + output);
                    linkSuccessful = false;
                }
            };
            linkerProcess.BeginOutputReadLine();
            linkerProcess.BeginErrorReadLine();

            linkerProcess.StandardInput.WriteLine("@echo off");
            linkerProcess.StandardInput.WriteLine("\"" + GetDevCmdPath(project, platform) + "\"");
            linkerProcess.StandardInput.WriteLine(projectPath[0] + ":"); // Change drive
            linkerProcess.StandardInput.WriteLine("cd " + projectPath); // CD

            string linkCommand = "\"" + linkerPath + "\" " + linkerOptions + " /OUT:\"" + outputFile + "\"";

            //all pch files
            foreach (var task in pchTasks)
            {
                linkCommand += " \"" + Path.Combine("GolemBuild", Path.ChangeExtension(Path.GetFileName(task.FilePath), ".obj")) + "\"";
            }

            //all compiled obj files
            foreach (var task in tasks)
            {
                linkCommand += " \"" + Path.Combine("GolemBuild", Path.ChangeExtension(Path.GetFileName(task.FilePath), ".obj")) + "\"";
            }

            Logger.LogMessage(string.Format("Linking Task: {0}", linkCommand));

            linkerProcess.StandardInput.WriteLine(linkCommand); // Execute task
            linkerProcess.StandardInput.WriteLine("exit");
            bool exited = linkerProcess.WaitForExit(30000);

            if (!exited)
            {
                Logger.LogError("[LINK ERROR]: Linker timed out after 30 seconds");
            }

            if (linkSuccessful && importLibrary.Length > 0)
            {
                Logger.LogMessage("Import library: " + importLibrary);
            }

            return linkSuccessful;
        }

        private void CreateCompilationTasks(Project project, string platform)
        {
            string projectPath = project.DirectoryPath;
            //in VS2017 this seems to be the proper one
            string VCTargetsPath = project.GetPropertyValue("VCTargetsPathEffective");
            if (string.IsNullOrEmpty(VCTargetsPath))
            {
                Console.WriteLine("Failed to evaluate VCTargetsPath variable on " + System.IO.Path.GetFileName(project.FullPath) + ". Is this a supported version of Visual Studio?");
                return;
            }
            string BuildDllPath = VCTargetsPath + (VCTargetsPath.Contains("v110") ? "Microsoft.Build.CPPTasks.Common.v110.dll" : "Microsoft.Build.CPPTasks.Common.dll");
            Assembly CPPTasksAssembly = Assembly.LoadFrom(BuildDllPath);

            string compilerPath = GetCompilerPath(project, platform);

            // Figure out include paths
            List<string> includePaths = new List<string>();
            {
                string incPath = project.GetProperty("IncludePath").EvaluatedValue;
                string[] incPaths = incPath.Split(';');

                foreach (string path in incPaths)
                {
                    if (path.Length > 0 && !includePaths.Contains(path))
                        includePaths.Add(path.Trim('\\'));
                }
            }

            var customBuildItems = project.GetItems("CustomBuild");
            foreach (var item in customBuildItems)
            {
                string command = item.GetMetadata("Command").EvaluatedValue;
                string message = item.GetMetadata("Message").EvaluatedValue;
                bool buildParallel = item.HasMetadata("BuildInParallel") && item.GetMetadata("BuildInParallel").EvaluatedValue == "true";

                if (buildParallel)
                {
                    customBuildTasksParallel.Add(new CustomBuildTask(item.EvaluatedInclude, command, message, buildParallel));
                }
                else
                {
                    customBuildTasks.Add(new CustomBuildTask(item.EvaluatedInclude, command, message, buildParallel));
                }
            }

            var cItems = project.GetItems("ClCompile");

            //list precompiled headers
            Logger.LogMessage("Disabling pch tasks as it makes no sense when precompiling cpp files...");
            if (false)
            {
                foreach (var item in cItems)
                {
                    if (item.DirectMetadata.Where(dmd => dmd.Name == "AdditionalIncludeDirectories").Any())
                    {
                        string incPath = item.GetMetadata("AdditionalIncludeDirectories").EvaluatedValue;
                        string[] incPaths = incPath.Split(';');

                        foreach (string path in incPaths)
                        {
                            if (!string.IsNullOrEmpty(path))
                            {
                                string tPath = path;
                                if (!Path.IsPathRooted(path))
                                {
                                    tPath = Path.GetFullPath(Path.Combine(project.DirectoryPath, path));
                                }
                                if (tPath.Length > 0 && !includePaths.Contains(tPath))
                                {
                                    includePaths.Add(tPath.Trim('\\'));
                                }
                            }
                        }
                    }

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

                            //disabled as we are now preprocessing files
                            //IncludeParser.FindIncludes(true, project.DirectoryPath, item.EvaluatedInclude, includePaths, includes);

                            Logger.LogMessage(">> " + item.EvaluatedInclude);

                            var CLtask = Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
                            CLtask.GetType().GetProperty("Sources").SetValue(CLtask, new TaskItem[] { new TaskItem() });
                            string args = GenerateTaskCommandLine(CLtask, new string[] { "PrecompiledHeaderOutputFile", "ProgramDataBaseFileName", "ObjectFileName", "AssemblerListingLocation" }, item.Metadata);//FS or MP?

                            string pchOutputFile = MakeAbsolutePath(projectPath, item.GetMetadataValue("PrecompiledHeaderOutputFile"));
                            string pdbOutputFile = MakeAbsolutePath(projectPath, item.GetMetadataValue("ProgramDataBaseFileName"));

                            pchTasks.Add(new CompilationTask(item.EvaluatedInclude, compilerPath, args, "", pdbOutputFile, pchOutputFile, projectPath, includePaths, includes));
                        }
                    }
                }
            }

            //list files to compile
            foreach (var item in cItems)
            {
                if (item.HasMetadata("AdditionalIncludeDirectories"))
                {
                    string incPath = item.GetMetadata("AdditionalIncludeDirectories").EvaluatedValue;
                    string[] incPaths = incPath.Split(';');

                    foreach (string path in incPaths)
                    {                        
                        if (!string.IsNullOrEmpty(path))
                        {
                            string tPath = MakeAbsolutePath(projectPath,path);
                            if (tPath.Length > 0 && !includePaths.Contains(tPath))
                            {
                                includePaths.Add(tPath.Trim('\\'));
                            }
                        }
                    }
                }

                List<string> includes = new List<string>();

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

                //IncludeParser.FindIncludes(true, project.DirectoryPath, item.EvaluatedInclude, includePaths, includes);

                Logger.LogMessage(">> " + item.EvaluatedInclude);

                var Task = Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
                Task.GetType().GetProperty("Sources").SetValue(Task, new TaskItem[] { new TaskItem() });

                string args = "";

                if (runDistributed)
                {
                    args = GenerateTaskCommandLine(Task, new string[] { "ObjectFileName", "AssemblerListingLocation", "ProgramDataBaseFileName","AdditionalIncludeDirectories" }, item.Metadata);//FS or MP?
                }
                else
                {
                    args = GenerateTaskCommandLine(Task, new string[] { "ObjectFileName", "AssemblerListingLocation" }, item.Metadata);//FS or MP?
                }

                if (Path.GetExtension(item.EvaluatedInclude) == ".c")
                    args += " /TC";
                else
                    args += " /TP";

                if (!runDistributed)
                {
                    args += " /FS"; // Force synchronous PDB writes // If we ever want one single pdb file per distributed node, this is how to do it
                }
                    
                /*string buildPath = Path.Combine(Path.GetDirectoryName(project.FullPath), "GolemBuild");
                if (!Directory.Exists(buildPath))
                {
                    Directory.CreateDirectory(buildPath);
                }


                args += " /Fd\"GolemBuild\\" + Path.GetFileNameWithoutExtension(item.EvaluatedInclude) + "\"";*/ // Use this for having one pdb per object file, this has issues with pch

                string pch = "";
                if (pchTasks.Count > 0)
                {
                    pch = MakeAbsolutePath(projectPath, item.GetMetadataValue("PrecompiledHeaderOutputFile"));
                }
                string pdb = MakeAbsolutePath(projectPath, item.GetMetadataValue("ProgramDataBaseFileName"));

                //replace precompiled header file location to absolute
                if (!string.IsNullOrEmpty(pch))
                {
                    Match match = Regex.Match(args, "/Fp\".+?\"");
                    if (match.Success)
                        args = args.Replace(match.Value,$"/Fp\"{pch}\"");
                }

                tasks.Add(new CompilationTask(Path.GetFullPath(Path.Combine(project.DirectoryPath, item.EvaluatedInclude)), compilerPath, args, pch, pdb, "", projectPath, includePaths, includes));
            }

            return;
        }

        private string MakeAbsolutePath(string rootPath, string path)
        {
            if (Path.IsPathRooted(path))
                return path;
            return Path.GetFullPath(Path.Combine(rootPath, path));
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
                        Logger.LogMessage(line);
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
                        Logger.LogMessage(line);
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
                        Logger.LogMessage(line);
                    }
                }
            }

            return true;
        }

        private string GetCompilerPath(Project project, string platform)
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
            var temp = project.GetProperty("Temp").EvaluatedValue;
            var sysRoot = project.GetProperty("SystemRoot").EvaluatedValue;

            //name depends on comilation platform and source platform

            string clPath = "";
            if (platform == "x64")
            {
                clPath = project.GetProperty("VC_ExecutablePath_x64_x64").EvaluatedValue;
            }
            else
            {
                clPath = project.GetProperty("VC_ExecutablePath_x86_x86").EvaluatedValue;
            }
            
            if (clPath.Contains(";"))
            {
                bool foundCl = false;
                string[] clPaths = clPath.Split(';');
                foreach(string path in clPaths)
                {
                    if (File.Exists(Path.Combine(path, "cl.exe")))
                    {
                        clPath = path;
                        foundCl = true;
                        break;
                    }
                }

                if (!foundCl)
                {
                    Logger.LogError("Could not find CL.exe!");
                }
            }

            clPath = Path.Combine(clPath, "cl.exe");

            return clPath;
        }

        private string GetLinkerPath(Project project, string platform)
        {
            string linkPath = "";
            if (platform == "x64")
            {
                linkPath = project.GetProperty("VC_ExecutablePath_x64_x64").EvaluatedValue;
            }
            else
            {
                linkPath = project.GetProperty("VC_ExecutablePath_x86_x86").EvaluatedValue;
            }

            if (linkPath.Contains(";"))
            {
                bool foundLink = false;
                string[] linkPaths = linkPath.Split(';');
                foreach (string path in linkPaths)
                {
                    if (File.Exists(Path.Combine(path, "link.exe")))
                    {
                        linkPath = path;
                        foundLink = true;
                        break;
                    }
                }

                if (!foundLink)
                {
                    Logger.LogError("Could not find Link.exe!");
                }
            }

            linkPath = Path.Combine(linkPath, "link.exe");

            return linkPath;
        }

        private string GetLibPath(Project project, string platform)
        {
            string libPath = "";
            if (platform == "x64")
            {
                libPath = project.GetProperty("VC_ExecutablePath_x64_x64").EvaluatedValue;
            }
            else
            {
                libPath = project.GetProperty("VC_ExecutablePath_x86_x86").EvaluatedValue;
            }

            if (libPath.Contains(";"))
            {
                bool foundLib = false;
                string[] libPaths = libPath.Split(';');
                foreach (string path in libPaths)
                {
                    if (File.Exists(Path.Combine(path, "lib.exe")))
                    {
                        libPath = path;
                        foundLib = true;
                        break;
                    }
                }

                if (!foundLib)
                {
                    Logger.LogError("Could not find lib.exe!");
                }
            }

            libPath = Path.Combine(libPath, "lib.exe");

            return libPath;
        }

        private string GetDevCmdPath(Project project, string platform)
        {
            string vsDir = "";
            if (platform == "x64")
            {
                vsDir = project.GetProperty("VsInstallRoot").EvaluatedValue;
                vsDir = Path.Combine(vsDir, "VC", "Auxiliary", "Build", "vcvars64.bat");
            }
            else
            {
                vsDir = project.GetProperty("VSInstallDir").EvaluatedValue;
                vsDir = Path.Combine(vsDir, "Common7", "Tools", "VsDevCmd.bat");
            }

            
            return vsDir;
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

        public void ClearTasks()
        {
            tasks.Clear();
            pchTasks.Clear();
        }

    }
}
