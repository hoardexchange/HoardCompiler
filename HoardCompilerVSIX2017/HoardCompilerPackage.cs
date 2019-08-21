using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace HoardCompiler
{
    /// <summary>
    /// Page in Options menu for customization
    /// </summary>
	public class OptionPageGrid : DialogPage
	{
        [Category("HoardCompiler")]
        [DisplayName("Golem Hub Url")]
        [Description("Address of the Golem Hub")]
        public string OptionGolemHubUrl
        {
            get;
            set;
        } = "http://10.30.10.121:6162";

        [Category("HoardCompiler")]
        [DisplayName("Local Server port")]
        [Description("Port of local http server that communicates with Golem Hub")]
        public int OptionGolemServerPort
        {
            get;
            set;
        } = 6000;

        [Category("HoardCompiler")]
        [DisplayName("Create unity files")]
        [Description("Whether to attempt to use 'unity' files to speed up compilation. May require modification of some headers.")]
        public bool OptionUseUnity
        {
            get;
            set;
        } = false;
	}

    /// <summary>
    /// Main package of HoardCompiler project
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("#110", "#112", "0.3", IconResourceID = 400)] // Info on this package for Help/About
    [Guid(HoardCompilerPackage.PackageGuidString)]
    [ProvideOptionPage(typeof(OptionPageGrid), "Hoard Compiler", "Options", 0, 0, true)]
    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1650:ElementDocumentationMustBeSpelledCorrectly", Justification = "pkgdef, VS and vsixmanifest are valid VS terms")]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    public sealed class HoardCompilerPackage : AsyncPackage
    {
        /// <summary>
        /// HoardCompilerPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "58caa410-0099-4620-a7b4-8cce182d193b";

        /// <summary>
        /// Initializes a new instance of the <see cref="HoardCompilerPackage"/> class.
        /// </summary>
        public HoardCompilerPackage()
        {
            // Inside this method you can place any initialization code that does not require
            // any Visual Studio service because at this point the package object is created but
            // not sited yet inside Visual Studio environment. The place to do all the other
            // initialization is the Initialize method.
        }

        #region Package Members

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        protected override async System.Threading.Tasks.Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            var buildService = new GolemBuild.GolemBuildService();
            GolemBuild.Logger.OnMessage += (str) =>
            {
                Logger.Log(str + "\n");
            };
            GolemBuild.Logger.OnError += (str) =>
            {
                Logger.Log(str + "\n");
            };

            //Add build commands
            await BuildCommand.InitializeAsync(this);

            await Logger.InitializeAsync(this, "HoardCompiler");

            Logger.Log("Hoard Compiler extension initialized and ready to run!");
        }

        #endregion
    }
}
