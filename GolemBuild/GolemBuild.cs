using Microsoft.Build.Evaluation;
using Microsoft.Build.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace GolemBuild
{
    public class GolemBuild
    {
        public bool BuildProject(string projPath, string configuration, string platform)
        {
            ProjectCollection projColl = new ProjectCollection();
            Project project = projColl.LoadProject(projPath);

            if (project != null)
            {
                project.SetGlobalProperty("Configuration", configuration);
                project.SetGlobalProperty("Platform", platform);
                project.ReevaluateIfNecessary();

                var ProjectReferences = project.Items.Where(elem => elem.ItemType == "ProjectReference");
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
            }

            List<CompilationTask> tasks = CreateCompilationTasks(project);

            return true;
        }

        private List<CompilationTask> CreateCompilationTasks(Project project)
        {
            List<CompilationTask> tasks = new List<CompilationTask>();
            //in VS2017 this semms to be the proper one
            string VCTargetsPath = project.GetPropertyValue("VCTargetsPathEffective");
            if (string.IsNullOrEmpty(VCTargetsPath))
            {
                Console.WriteLine("Failed to evaluate VCTargetsPath variable on " + System.IO.Path.GetFileName(project.FullPath) + ". Is this a supported version of Visual Studio?");
                return tasks;
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

            //list files to compile
            foreach (var item in cItems)
            {
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

                var Task = Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
                Task.GetType().GetProperty("Sources").SetValue(Task, new TaskItem[] { new TaskItem() });
                string args = GenerateTaskCommandLine(Task, new string[] { "ObjectFileName", "AssemblerListingLocation" }, item.Metadata);//FS or MP?
                if (Path.GetExtension(item.EvaluatedInclude) == ".c")
                    args += " /TC";
                else
                    args += " /TP";
                //TODO: guess which pch is going to be used (there can be probably only one)
                tasks.Add(new CompilationTask(item.EvaluatedInclude, compilerPath, args, "", item.GetMetadataValue("PrecompiledHeaderOutputFile")));
            }

            return tasks;
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
            string clPath = Path.Combine(project.GetProperty("VC_ExecutablePath_x64_x86").EvaluatedValue, "cl.exe");
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
            return GenCmdLineMethod.Invoke(Task, new object[] { Type.Missing, Type.Missing }) as string;
        }

    }
}
