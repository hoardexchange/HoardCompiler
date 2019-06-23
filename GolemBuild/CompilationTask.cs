using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GolemBuild
{
    public class CompilationTask
    {
        public string FilePath { get; set; }
        public string Compiler { get; set; }
        public string CompilerArgs { get; set; }
        public string PrecompiledHeader { get; set; }
        public string OutputPath { get; set; }

        public CompilationTask(string filePath, string compiler, string compilerArgs, string pch, string outputPath)
        {
            FilePath = filePath;
            Compiler = compiler;
            CompilerArgs = compilerArgs;
            PrecompiledHeader = pch;
            OutputPath = outputPath;
        }
    }
}
