using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GolemBuild
{
    public enum BuildTaskStatus
    {
        None = 0,
        Queued,
        Started,
        Suceeded,
        Failed
    }

    public class BuildTaskStatusChangedArgs : EventArgs
    {
        public BuildTaskStatus Status { get; set; }
        public string Message { get; set; }
    }

    public interface IBuildService
    {
        event EventHandler<BuildTaskStatusChangedArgs> BuildTaskStatusChanged;

        void AddTask(CompilationTask task);

        bool Start();

        bool Stop();
    }
}
