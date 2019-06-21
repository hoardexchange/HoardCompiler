using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Globalization;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.VCProjectEngine;

namespace GolemCompilerVSIX
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class BuildCommand
    {
        /// <summary>
        /// Command ID.
        /// </summary>
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
                var menuCommandID = new CommandID(CommandSet, CommandId);
                var menuItem = new MenuCommand(SolutionItemCallback, menuCommandID);
                commandService.AddCommand(menuItem);

                menuCommandID = new CommandID(CommandSet, SlnCommandId);
                menuItem = new MenuCommand(SolutionItemCallback, menuCommandID);
                commandService.AddCommand(menuItem);

                menuCommandID = new CommandID(CommandSet, ProjCommandId);
                menuItem = new MenuCommand(ProjItemCallback, menuCommandID);
                commandService.AddCommand(menuItem);

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

            package.m_dte.ExecuteCommand("File.SaveAll");

            foreach (var item in package.m_dte.SelectedItems)
            {
                Project proj = (item as SelectedItem).Project;
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
            if (null == package.m_dte.Solution)
                return;

            package.m_outputPane.Activate();
            package.m_outputPane.Clear();

            if (package.m_dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
            {
                package.m_outputPane.OutputString("Build not launched due to active debugger.\r");
                return;
            }

            package.m_dte.ExecuteCommand("File.SaveAll");
            
            //list projects to be build
            Solution sln = package.m_dte.Solution;

            List<VCProject> projects = new List<VCProject>();

            foreach (var item in sln.Projects)
            {
                Project proj = (item as Project);
                if (proj != null)
                {
                    VCProject vcp = proj.Object as VCProject;
                    AddProject(vcp, projects);
                }
            }

            RequestBuildProjects(projects);
        }

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

        private void RequestBuildProjects(List<VCProject> projects)
        {
            string message = string.Format(CultureInfo.CurrentCulture, "Found {0} projects to build", projects.Count);
            string title = "BuildCommand";
            
            VsShellUtilities.ShowMessageBox(
                ServiceProvider,
                message,
                title,
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }
}
