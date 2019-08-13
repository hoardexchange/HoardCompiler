using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Globalization;
using EnvDTE;
using GolemBuild;
//using Microsoft.VisualStudio.ProjectSystem;
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
        private readonly AsyncPackage package;
        private readonly DTE Dte;

        /// <summary>
        /// Initializes a new instance of the <see cref="BuildCommand"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        private BuildCommand(AsyncPackage package, DTE dte, OleMenuCommandService commandService)
        {
            if (package == null)
            {
                throw new ArgumentNullException("package");
            }

            this.package = package;
            Dte = dte;

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
        public static async System.Threading.Tasks.Task InitializeAsync(AsyncPackage package)
        {
            // Switch to the main thread - the call to AddCommand in Command1's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var dte = await package.GetServiceAsync(typeof(DTE)) as DTE;
            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new BuildCommand(package, dte, commandService);
        }

        private void ProjItemCallback(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            List<VCProject> projects = new List<VCProject>();

            Dte.ExecuteCommand("File.SaveAll");

            foreach (var item in Dte.SelectedItems)
            {
                EnvDTE.Project proj = (item as SelectedItem).Project;
                if (proj != null)
                {
                    VCProject vcp = proj.Object as VCProject;
                    AddProject(vcp, projects);
                }
            }

            var config = Dte.Solution.SolutionBuild.ActiveConfiguration as EnvDTE80.SolutionConfiguration2;

            RequestBuildProjects(config, projects);
        }
        
        private void SolutionItemCallback(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (null == Dte.Solution)
                return;

            if (Dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
            {
                Logger.Log("Build not launched due to active debugger.\r");
                return;
            }

            Dte.ExecuteCommand("File.SaveAll");
            
            //list projects to be build
            Solution sln = Dte.Solution;

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

            var config = sln.SolutionBuild.ActiveConfiguration as EnvDTE80.SolutionConfiguration2;

            RequestBuildProjects(config, projects);
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
        private void RequestBuildProjects(EnvDTE80.SolutionConfiguration2 config, List<VCProject> projects)
        {
            OptionPageGrid page = (OptionPageGrid)package.GetDialogPage(typeof(OptionPageGrid));
            GolemBuildService.Configuration options = new GolemBuildService.Configuration() { GolemHubUrl = page.OptionGolemHubUrl, GolemServerPort = page.OptionGolemServerPort };

            var task = System.Threading.Tasks.Task.Run(async () =>
            {
                if (!GolemBuildService.Instance.Options.Equals(options) || !GolemBuildService.Instance.IsRunning)
                {
                    GolemBuildService.Instance.Stop();
                    GolemBuildService.Instance.Options = options;
                    GolemBuildService.Instance.Start();

                }

                GolemBuild.GolemBuild builder = new GolemBuild.GolemBuild();

                GolemBuild.Logger.OnMessage += (str) =>
                {
                    Logger.Log(str + "\n");
                };
                GolemBuild.Logger.OnError += (str) =>
                {
                    Logger.Log(str + "\n");
                };

                int projectsSucceeded = 0;
                int projectsFailed = 0;

                bool first = true;
                foreach (VCProject p in projects)
                {
                    if (first)
                        first = false;
                    else
                        builder.ClearTasks();

                    bool succeeded = builder.BuildProject(p.ProjectFile, config.Name, config.PlatformName);
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

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

                VsShellUtilities.ShowMessageBox(
                    ServiceProvider,
                    message,
                    title,
                    OLEMSGICON.OLEMSGICON_INFO,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            });            
        }
    }
}
