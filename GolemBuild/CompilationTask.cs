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
        public string PDB { get; set; }
        public string OutputPath { get; set; }
        public List<string> IncludeDirs { get; set; }
        public List<string> Includes { get; set; }
        public List<string> LocalIncludes { get; set; }

        public CompilationTask(string filePath, string compiler, string compilerArgs, string pch, string pdb, string outputPath, List<string> includeDirs, List<string> includes, List<string> localIncludes)
        {
            FilePath = filePath;
            Compiler = compiler;
            CompilerArgs = compilerArgs;
            PrecompiledHeader = pch;
            PDB = pdb;
            OutputPath = outputPath;
            IncludeDirs = includeDirs;
            Includes = includes;
            LocalIncludes = localIncludes;
        }
    }
}
