using Microsoft.Build.Evaluation;
using Microsoft.Build.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Threading;

namespace GolemBuild
{
    public class GolemBuild
    {
        public event Action<string> OnError;
        public event Action<string> OnMessage;
        public event Action OnClear;

        private List<CompilationTask> tasks = new List<CompilationTask>();

        public bool BuildProject(string projPath, string configuration, string platform)
        {
            ProjectCollection projColl = new ProjectCollection();
            //load the project
            Project project = projColl.LoadProject(projPath);

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
                
                return BuildTasks(project);
            }

            OnError?.Invoke("Could not load project " + projPath);

            return false;
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
                processes[i].StandardInput.WriteLine("\"" + tasks[i].Compiler + "\" " + tasks[i].CompilerArgs + " " + tasks[i].FilePath); // Execute task
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

            if (!compilationSucceeded)
            {
                OnMessage.Invoke("- Compilation failed -");
                return false;
            }

            OnMessage.Invoke("- Compilation succeeded -");

            // Linking

            return true;
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
                    if (to != line.Length - 1)
                        line = line.Substring(to, line.Length - 1);
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

                        if (!includes.Contains(includeName) && !localIncludes.Contains(includeName))
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

                        if (!includes.Contains(includeName) && !localIncludes.Contains(includeName))
                        {
                            //WriteLineOutput("Include \"" + includeName + "\" found in " + fileName);
                            localIncludes.Add(includeName);
                            FindIncludes(project, includeName, includePaths, ref includes, ref localIncludes);
                        }
                        else
                        {
                            //WriteLineOutput("Ignored \"" + includeName + "\" found in " + fileName);
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
            string outputPath = "";

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
                        var CLtask = Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
                        CLtask.GetType().GetProperty("Sources").SetValue(CLtask, new TaskItem[] { new TaskItem() });
                        string args = GenerateTaskCommandLine(CLtask, new string[] { "PrecompiledHeaderOutputFile", "ObjectFileName", "AssemblerListingLocation" }, item.Metadata);//FS or MP?
                        tasks.Add(new CompilationTask(item.EvaluatedInclude, compilerPath, args, "", item.GetMetadataValue("PrecompiledHeaderOutputFile")));
                    }
                }
            }

            // Figure out include paths
            List<string> includePaths = new List<string>();
            string incPath = project.GetProperty("IncludePath").EvaluatedValue;
            string[] incPaths = incPath.Split(';');
            
            foreach(string path in incPaths)
            {
                includePaths.Add(path.Trim(';'));
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

                string buildPath = Path.Combine(Path.GetDirectoryName(project.FullPath), "GolemBuild");
                if (!Directory.Exists(buildPath))
                {
                    Directory.CreateDirectory(buildPath);
                }


                args += " /Fd\"GolemBuild\\" + Path.GetFileNameWithoutExtension(item.EvaluatedInclude) + "\"";

                string includePathString = "";
                foreach(string includePath in includePaths)
                {
                    if (includePath.Length > 0)
                        includePathString += " /I \"" + includePath + "\"";
                }
                args += includePathString;

                //TODO: guess which pch is going to be used (there can be probably only one)
                tasks.Add(new CompilationTask(item.EvaluatedInclude, compilerPath, args, "", item.GetMetadataValue("PrecompiledHeaderOutputFile")));
            }

            return;
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
