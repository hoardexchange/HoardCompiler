using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using System;

namespace HoardCompiler
{
    internal static class Logger
    {
        private static string _name;
        private static IVsOutputWindowPane _pane;
        private static IVsOutputWindow _output;

        public static async System.Threading.Tasks.Task InitializeAsync(AsyncPackage package, string name)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();

            _output = await package.GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
            _name = name;
            if (_output != null)
            {
                Guid guid = Guid.NewGuid();
                _output.CreatePane(ref guid, _name, 1, 1);
                _output.GetPane(ref guid, out _pane);
            }            
        }

        public static void Log(object message)
        {
            try
            {
                if (_pane!=null)
                {
                    ThreadHelper.JoinableTaskFactory.StartOnIdle(() =>
                    {
                        _pane.OutputString(DateTime.Now.ToString() + ": " + message + Environment.NewLine);
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.Write(ex);
            }
        }
    }
}
