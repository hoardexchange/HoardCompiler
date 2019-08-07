using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Globalization;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.VCProjectEngine;

namespace GolemCompiler
{
    /// <summary>
    /// Build command handler
    /// </summary>
    internal sealed class BuildCommand
    {
        public const int CommandId = 0x0100;
        public const int SlnCommandId = 0x0101;
        public const int ProjCommandId = 0x0102;
        public const int XProjCommandId = 0x0103;


        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("4a0e73c2-8f07-44b9-837a-3f7db6209609");

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly GolemCompilerPackage package;

        /// <summary>
        /// Initializes a new instance of the <see cref="BuildCommand"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        private BuildCommand(Package package)
        {
            if (package == null)
            {
                throw new ArgumentNullException("package");
            }

            this.package = (GolemCompilerPackage)package;

            OleMenuCommandService commandService = ServiceProvider.GetService(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
            {
                //build command in the build menu
                var menuCommandID = new CommandID(CommandSet, CommandId);
                var menuItem = new MenuCommand(SolutionItemCallback, menuCommandID);
                commandService.AddCommand(menuItem);

                //build command in the solution context menu
                menuCommandID = new CommandID(CommandSet, SlnCommandId);
                menuItem = new MenuCommand(SolutionItemCallback, menuCommandID);
                commandService.AddCommand(menuItem);

                //build command in the project context menu
                menuCommandID = new CommandID(CommandSet, ProjCommandId);
                menuItem = new MenuCommand(ProjItemCallback, menuCommandID);
                commandService.AddCommand(menuItem);

                //build command when there more than one project is selected
                menuCommandID = new CommandID(CommandSet, XProjCommandId);
                menuItem = new MenuCommand(ProjItemCallback, menuCommandID);
                commandService.AddCommand(menuItem);
            }
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static BuildCommand Instance
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        private IServiceProvider ServiceProvider
        {
            get
            {
                return this.package;
            }
        }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static void Initialize(Package package)
        {
            Instance = new BuildCommand(package);
        }

        private void ProjItemCallback(object sender, EventArgs e)
        {            
            List<VCProject> projects = new List<VCProject>();

            package.Dte.ExecuteCommand("File.SaveAll");

            foreach (var item in package.Dte.SelectedItems)
            {
                EnvDTE.Project proj = (item as SelectedItem).Project;
                if (proj != null)
                {
                    VCProject vcp = proj.Object as VCProject;
                    AddProject(vcp, projects);
                }
            }

            RequestBuildProjects(projects);
        }
        
        private void SolutionItemCallback(object sender, EventArgs e)
        {
            if (null == package.Dte.Solution)
                return;

            if (package.Dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
            {
                Logger.Log("Build not launched due to active debugger.\r");
                return;
            }

            package.Dte.ExecuteCommand("File.SaveAll");
            
            //list projects to be build
            Solution sln = package.Dte.Solution;

            List<VCProject> projects = new List<VCProject>();

            foreach (var item in sln.Projects)
            {
                EnvDTE.Project proj = (item as EnvDTE.Project);
                if (proj != null)
                {
                    VCProject vcp = proj.Object as VCProject;
                    AddProject(vcp, projects);
                }
            }

            RequestBuildProjects(projects);
        }

        /// <summary>
        /// This recursive function will add project and all its dependencies in a sorted way
        /// </summary>
        /// <param name="vcp"></param>
        /// <param name="projects"></param>
        private void AddProject(VCProject vcp, List<VCProject> projects)
        {
            if (vcp != null && !projects.Contains(vcp))
            {
                //check references
                foreach (VCReference r in vcp.VCReferences)
                {
                    VCProjectReference vcref = r as VCProjectReference;
                    if (vcref != null && vcref.ReferencedProject != null)
                        AddProject(vcref.ReferencedProject.Object, projects);
                }
                projects.Add(vcp);
            }
        }

        /// <summary>
        /// Start projects building
        /// </summary>
        /// <param name="projects"></param>
        static bool hasInitedServiceEvents = false;
        private void RequestBuildProjects(List<VCProject> projects)
        {
            var task = ThreadHelper.JoinableTaskFactory.StartOnIdle(
                async () =>
                {
                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        Solution sln = package.Dte.Solution;
                        var sc = sln.SolutionBuild.ActiveConfiguration as EnvDTE80.SolutionConfiguration2;

                        GolemBuild.GolemBuild builder = new GolemBuild.GolemBuild();

                        builder.OnMessage += (str) =>
                        {
                            Logger.Log(str + "\n");
                        };

                        if (!hasInitedServiceEvents)
                        {
                            hasInitedServiceEvents = true;
                            GolemBuild.GolemBuildService.buildService.OnMessage += (str) =>
                            {
                                Logger.Log(str + "\n");
                            };
                        }

                        int projectsSucceeded = 0;
                        int projectsFailed = 0;

                        bool first = true;
                        foreach (VCProject p in projects)
                        {
                            if (first)
                                first = false;
                            else
                                builder.ClearTasks();

                            bool succeeded = builder.BuildProject(p.ProjectFile, sc.Name, sc.PlatformName);
                            if (succeeded)
                                projectsSucceeded++;
                            else
                                projectsFailed++;
                        }

                        Logger.Log("Succeeded: " + projectsSucceeded.ToString() + " Failed: " + projectsFailed.ToString() + "\n");

                        string message = string.Format(CultureInfo.CurrentCulture, "Found {0} projects to build", projects.Count);
                        foreach (VCProject p in projects)
                        {
                            message += "\nPROJECT:" + p.ProjectFile;
                            message += builder.GetProjectInformation(p.ProjectFile);
                        }
                        string title = "BuildCommand";

                        VsShellUtilities.ShowMessageBox(
                            ServiceProvider,
                            message,
                            title,
                            OLEMSGICON.OLEMSGICON_INFO,
                            OLEMSGBUTTON.OLEMSGBUTTON_OK,
                            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                    });
                }
                );
            
        }
    }
}
