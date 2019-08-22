using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GolemBuild
{
    class CustomBuildTask
    {
        public string FilePath { get; set; }
        public string Command { get; set; }
        public string Message { get; set; }
        public bool BuildParallel { get; set; }

        public CustomBuildTask(string filePath, string command, string message, bool buildParallel)
        {
            FilePath = filePath;
            Command = command;
            Message = message;
            BuildParallel = buildParallel;
        }
    }
}
