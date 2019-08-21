using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace GolemBuild
{
    class GolemCache
    {
        class CompilerPackage
        {
            public List<string> compilers;
            public string hash;
            public byte[] data;
        }

        static List<CompilerPackage> compilerCache = new List<CompilerPackage>();
        static Dictionary<string, byte[]> tasksCache = new Dictionary<string, byte[]>();

        static public void Reset()
        {
            //compilerCache.Clear();
            tasksCache.Clear();
        }

        static private void AddDirectoryFilesToTar(TarArchive tarArchive, string sourceDirectory, bool recurse)
        {
            // Optionally, write an entry for the directory itself.
            // Specify false for recursion here if we will add the directory's files individually.
            TarEntry tarEntry = TarEntry.CreateEntryFromFile(sourceDirectory);
            tarArchive.WriteEntry(tarEntry, false);

            // Write each file to the tar.
            string[] filenames = Directory.GetFiles(sourceDirectory);
            foreach (string filename in filenames)
            {
                if (Path.GetExtension(filename) == ".exe" || Path.GetExtension(filename) == ".dll")
                {
                    tarEntry = TarEntry.CreateEntryFromFile(filename);
                    tarArchive.WriteEntry(tarEntry, true);
                }
            }

            if (recurse)
            {
                string[] directories = Directory.GetDirectories(sourceDirectory);
                foreach (string directory in directories)
                    AddDirectoryFilesToTar(tarArchive, directory, recurse);
            }
        }

        // This function gathers and caches a CompilerPackage in memory, this CompilerPackage is a tar.gz of (hopefully) all files needed for the compilers passed as parameters.
        // Please note that everything is kept in memory and we don't save anything to filesystem.
        static public string GetCompilerPackageHash(List<string> compilers)
        {
            // See if we have this list of CompilerPackage cached already
            foreach(CompilerPackage compilerPackage in compilerCache)
            {
                if (compilerPackage.compilers.Count != compilers.Count)
                    continue;

                bool perfectMatch = true;
                foreach(string cacheCompiler in compilerPackage.compilers)
                {
                    bool foundCompiler = false;
                    foreach(string compiler in compilers)
                    {
                        if (cacheCompiler == compiler)
                        {
                            foundCompiler = true;
                            break;
                        }
                    }

                    if (!foundCompiler)
                    {
                        perfectMatch = false;
                        break;
                    }
                }

                if (perfectMatch)
                    return compilerPackage.hash;
            }

            // Create a new CompilerPackage
            CompilerPackage newCompilerPackage = new CompilerPackage();
            newCompilerPackage.compilers = compilers;

            // Lets get all the .exe and .dll files in the same directory (including sub directories) as the compiler and tar.gz them.
            //step 1. tar file
            using (MemoryStream stream = new MemoryStream())
            {
                //using (GZipOutputStream gzoStream = new GZipOutputStream(stream))
                using (TarArchive tarArchive = TarArchive.CreateOutputTarArchive(stream))// gzoStream))
                {
                    foreach (string compiler in compilers)
                    {
                        // Package executables and necessary dlls
                        string compilerDir = Path.GetDirectoryName(compiler);
                        tarArchive.RootPath = compilerDir;
                        AddDirectoryFilesToTar(tarArchive, compilerDir, true);
                    }
                }
                newCompilerPackage.data = stream.ToArray();
            }
            //step 2. calculate SHA1 hash of tar file
            using (var cryptoProvider = new SHA1CryptoServiceProvider())
            {
                newCompilerPackage.hash = BitConverter.ToString(cryptoProvider.ComputeHash(newCompilerPackage.data)).Replace("-", string.Empty).ToLower();
            }
            //step 3. zip file (we cannot calculate SHA1 from zip since zip contains timestamps and metadata and each compression process creates different
            //header for zip file
            using (MemoryStream stream = new MemoryStream())
            {
                using (GZipOutputStream gzoStream = new GZipOutputStream(stream))
                {
                    gzoStream.Write(newCompilerPackage.data,0, newCompilerPackage.data.Length);
                }
                newCompilerPackage.data = stream.ToArray();
            }

            // Lets cache this for later so we don't need to redo this every time
            compilerCache.Add(newCompilerPackage);
            return newCompilerPackage.hash;
        }

        static public bool GetCompilerPackageData(string hash, out byte[] data)
        {
            data = null;
            foreach(CompilerPackage compilerPackage in compilerCache)
            {
                if (compilerPackage.hash == hash)
                {
                    data = compilerPackage.data;
                    return true;
                }
            }

            return false;
        }

        static public string RegisterTasksPackage(byte[] data)
        {
            string hash = "";
            // Calculate the hash of the data
            using (var cryptoProvider = new SHA1CryptoServiceProvider())
            {
                hash = BitConverter.ToString(cryptoProvider.ComputeHash(data)).Replace("-", string.Empty).ToLower();
            }
            tasksCache[hash] = data;
            return hash;
        }

        static public bool GetTasksPackage(string hash, out byte[] data)
        {
            data = null;
            if (!tasksCache.ContainsKey(hash))
                return false;

            data = tasksCache[hash];
            return true;
        }

    }
}
