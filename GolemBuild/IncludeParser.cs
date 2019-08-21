using System;
using System.Collections.Generic;
using System.IO;

namespace GolemBuild
{
    internal class IncludeParser
    {
        private static void parseIncludes(string path, Action<bool, string> visitorCB)
        {
            StreamReader file = new StreamReader(path);

            bool isInMultilineComment = false;
            string line;
            while ((line = file.ReadLine()) != null)
            {
                line = line.Trim();

                // Start multiline comment
                if (line.Contains("/*"))
                {
                    // End multiline comment
                    if (line.Contains("*/"))
                    {
                        int startComment = line.IndexOf("/*");
                        int endComment = line.IndexOf("*/") + 1;

                        string newLine = "";
                        if (startComment != 0)
                            newLine += line.Substring(0, startComment);
                        if (endComment < line.Length - 1)
                            newLine += line.Substring(endComment);

                        line = newLine;
                    }
                    else
                    {
                        int to = line.IndexOf("/*") + 1;
                        line = line.Substring(0, to);
                        isInMultilineComment = true;
                    }
                }
                else if (isInMultilineComment && line.Contains("*/"))
                {
                    int to = line.IndexOf("*/") + 1;
                    if (to < line.Length - 1)
                        line = line.Substring(to);
                    isInMultilineComment = false;
                }
                else if (isInMultilineComment)
                {
                    continue;
                }

                // Remove // comments
                if (line.Contains("//"))
                {
                    int to = line.IndexOf("//");
                    line = line.Substring(0, to);
                }

                var match = System.Text.RegularExpressions.Regex.Match(line, @"#\s*include");

                if (match.Success)//line.Contains("#include"))
                {
                    if (line.Contains("<") && line.Contains(">"))
                    {
                        // Angle bracket include
                        int from = line.IndexOf("<") + 1;
                        int to = line.LastIndexOf(">");
                        string includeName = line.Substring(from, to - from);

                        visitorCB(false, includeName);
                    }
                    else if (line.Contains("\""))
                    {
                        // Quote include
                        int from = line.IndexOf("\"") + 1;
                        int to = line.LastIndexOf("\"");
                        string includeName = line.Substring(from, to - from);

                        visitorCB(true, includeName);
                    }
                }
            }
        }

        public static void FindIncludes(bool isLocal, string curFolder, string filePath, IEnumerable<string> includePaths, List<string> includes)
        {
            bool fileExists = false;
            string fullPath = null;
            //if filePath is absolute change cur folder and split path
            if (Path.IsPathRooted(filePath))
            {
                throw new NotSupportedException($"Could not add an absolute include: {filePath}!\nGU currently does not support absolute file paths! Please change this include to relative one!");
                /*if (File.Exists(filePath))
                {
                    fullPath = filePath;
                    fileExists = true;                    
                }*/
            }
            else
            {
                //1. if this is local file, first try to find it relative to the current folder
                if (isLocal)
                {
                    fullPath = Path.Combine(curFolder, filePath);
                    if (File.Exists(fullPath))
                        fileExists = true;
                }
                //2. if not found check includes
                if (!fileExists)
                {
                    foreach (string includePath in includePaths)
                    {
                        fullPath = Path.Combine(includePath, filePath);
                        if (File.Exists(fullPath))
                        {
                            curFolder = includePath;
                            fileExists = true;
                            break;
                        }
                    }
                }
            }
            if (!fileExists)
            {
                Logger.LogError("Could not find include: " + filePath);
                return;
            }
            //now check if this file has been already processed
            if (includes.Contains(fullPath))
                return;

            includes.Add(fullPath);
            //get current folder
            curFolder = Path.GetDirectoryName(fullPath);
            //we have found the file, load and parse it, to recursively find all other includes
            parseIncludes(fullPath, (local, includeName) =>
            {
                FindIncludes(local, curFolder, includeName, includePaths, includes);
            });
            //--------------------            
        }
    }
}
