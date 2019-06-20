using System;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Win32;

namespace GolemCompilerVSIX
{
	public class OptionPageGrid : DialogPage
	{
        [Category("GolemCompiler")]
        [DisplayName("url to Golem Hub")]
        [Description("Can be used to specify the url to Golem Hub")]
        public string OptionGolemHubUrl
        {
            get;
            set;
        } = "localhost:8080";

        [Category("GolemCompiler")]
        [DisplayName("Use unity files")]
        [Description("Whether to attempt to use 'unity' files to speed up compilation. May require modifying some headers.")]
        public bool OptionUseUnity
        {
            get;
            set;
        } = false;
	}

    [PackageRegistration(UseManagedResourcesOnly = true)]
    [InstalledProductRegistration("#110", "#112", "0.1", IconResourceID = 400)] // Info on this package for Help/About
    [Guid(GolemCompilerPackage.PackageGuidString)]
    [ProvideOptionPage(typeof(OptionPageGrid), "Golem Compiler", "Options", 0, 0, true)]
    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1650:ElementDocumentationMustBeSpelledCorrectly", Justification = "pkgdef, VS and vsixmanifest are valid VS terms")]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    public sealed class GolemCompilerPackage : Package
    {
        /// <summary>
        /// GolemCompilerPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "58caa410-0099-4620-a7b4-8cce182d193b";

		public IVsOutputWindowPane m_outputPane;
        /// <summary>
        /// Initializes a new instance of the <see cref="GolemCompilerPackage"/> class.
        /// </summary>
        public GolemCompilerPackage()
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
        protected override void Initialize()
        {
            base.Initialize();

            BuildCommand.Initialize(this);

            var outputWindow = GetService(typeof(SVsOutputWindow)) as IVsOutputWindow;
            if (outputWindow == null) return;

            var outputPaneId = VSConstants.OutputWindowPaneGuid.GeneralPane_guid;
            var hresult = outputWindow.CreatePane(outputPaneId, "GolemCompiler", 1, 0);
            ErrorHandler.ThrowOnFailure(hresult);

            hresult = outputWindow.GetPane(outputPaneId, out m_outputPane);
            ErrorHandler.ThrowOnFailure(hresult);

            m_outputPane.Activate();
            m_outputPane.OutputString("Golem Compiler extension initialized and ready to run!");
        }

        #endregion
    }
}
