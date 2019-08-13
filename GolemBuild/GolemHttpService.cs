using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GolemBuild
{
    class GolemHttpService
    {
        private int ServerPort = 6000;
        private Task requestLoop = null;
        private CancellationTokenSource cancellationSource = null;

        /// <summary>
        /// Path for built data. TODO: this should be interfaced so it is not strictly data on the filesystem, it might be in memory
        /// </summary>
        public string BuildPath { get; set; }

        public void Start()
        {
            cancellationSource = new CancellationTokenSource();
            requestLoop = RequestServer(cancellationSource.Token);
        }

        public void Stop()
        {
            cancellationSource.Cancel();
            requestLoop.Wait();

            cancellationSource = null;
            requestLoop = null;
        }

        /// <summary>
        /// All providers will ask this server for files and packages, for now the only supported format is tar.gz
        /// TODO: try if zip compression is also supported
        /// </summary>
        /// <returns></returns>
        private async Task RequestServer(System.Threading.CancellationToken token)
        {
            HttpListener listener = new HttpListener();
            //this part is tricky:
            //either run this as administrator or
            //run: netsh http add urlacl url=http://+:ServerPort/requestID/ user=DOMAIN\username (as an administrator)
            listener.Prefixes.Add("http://+:" + ServerPort + "/requestID/");
            try
            {
                var taskCompletionSource = new TaskCompletionSource<HttpListenerContext>();
                token.Register(() =>
                {
                    taskCompletionSource.TrySetCanceled();//this will throw
                });

                listener.Start();
                while (true)
                {
                    try
                    {
                        //get the request
                        HttpListenerContext context = await await Task.WhenAny(listener.GetContextAsync(), taskCompletionSource.Task);
                        _ = Task.Run(() => ProcessRequest(context)); // Discard is used so we don't get warnings about not using await...
                    }
                    catch (TaskCanceledException)
                    {
                        //bail out
                        break;
                    }
                    catch (Exception ex)
                    {
                        //TODO: do sth with this exception
                        Console.WriteLine(ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex.Message);
            }
            finally
            {
                listener.Stop();
                listener.Close();
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;

            // Are they trying to upload a file?
            if (request.HttpMethod == "PUT")
            {
                System.IO.Stream input = request.InputStream;
                string zipName = Path.Combine(BuildPath, Path.ChangeExtension(Path.GetFileNameWithoutExtension(request.RawUrl), ".zip"));
                FileStream fileStream = File.Create(zipName);
                input.CopyTo(fileStream);
                fileStream.Close();
                input.Close();

                // Send back OK
                HttpListenerResponse response = context.Response;
                response.Headers.Clear();
                response.SendChunked = false;
                response.StatusCode = 201;
                response.AddHeader("Content-Location", zipName);
                //response.AddHeader("Server", String.Empty);
                //response.AddHeader("Date", String.Empty);
                response.Close();

                ZipFile.ExtractToDirectory(zipName, Path.GetDirectoryName(zipName));
            }
            else // They are trying to download a file
            {
                // Obtain a response object.
                HttpListenerResponse response = context.Response;

                //let's check ranges
                long offset = 0;
                long size = -1;
                foreach (string header in request.Headers.AllKeys)
                {
                    if (header == "Range")
                    {
                        string[] values = request.Headers.GetValues(header);
                        string[] tokens = values[0].Split('=', '-');
                        offset = int.Parse(tokens[1]);
                        size = (int)(int.Parse(tokens[2]) - offset + 1);
                    }
                }

                // Are they requesting a CompilerPackage?
                if (request.RawUrl.StartsWith("/requestID/compiler/"))
                {
                    string compilerHash = request.RawUrl.Replace("/requestID/compiler/", "");
                    byte[] data;
                    if (GolemCache.GetCompilerPackageData(compilerHash, out data))
                    {
                        response.AddHeader("ETag", "SHA1:" + compilerHash);

                        if (size == -1)
                        {
                            size = data.Length;
                        }

                        response.ContentLength64 = size;

                        Stream output = response.OutputStream;
                        output.Write(data, (int)offset, (int)size);
                        output.Close();
                    }
                }
                // Or are they requesting a tasks package?
                else if (request.RawUrl.StartsWith("/requestID/tasks/"))
                {
                    string tasksPackageHash = request.RawUrl.Replace("/requestID/tasks/", "");
                    byte[] data;
                    if (GolemCache.GetTasksPackage(tasksPackageHash, out data))
                    {
                        response.AddHeader("ETag", "SHA1:" + tasksPackageHash);

                        if (size == -1)
                        {
                            size = data.Length;
                        }

                        response.ContentLength64 = size;

                        Stream output = response.OutputStream;
                        output.Write(data, (int)offset, (int)size);
                        output.Close();
                    }
                }
                /*{
                    //1. based on request.Url fetch the content data
                    DataPackage data = GetDataPackage(request.Url, offset, size);
                    //2. calculate some hash so provider nows if content has changed or not
                    response.AddHeader("ETag", data.DataHash);
                    response.ContentLength64 = data.DataStream.Length;
                    //response.ContentType = "application/x-gzip";// - not needed
                    //response.AddHeader("Accept-Ranges", "bytes");// - not needed?
                    System.IO.Stream output = response.OutputStream;
                    output.Write(data.DataStream, 0, data.DataStream.Length);
                    // close the output stream.
                    output.Close();
                }*/
            }
        }

        private DataPackage GetDataPackage(Uri url, long offset, int size)
        {
            string fileName = Path.GetFileNameWithoutExtension(url.AbsolutePath);
            string tarPath = Path.Combine(GolemBuild.golemBuildTasksPath, fileName + ".tar.gz");

            if (!File.Exists(tarPath))
            {
                throw new FileNotFoundException("Could not find the requested " + fileName + "tar.gz");
            }

            DataPackage data = new DataPackage();

            if (size == -1)
            {
                data.DataStream = File.ReadAllBytes(tarPath);
                using (var cryptoProvider = new SHA1CryptoServiceProvider())
                {
                    data.DataHash = "SHA1:" + BitConverter
                            .ToString(cryptoProvider.ComputeHash(data.DataStream)).Replace("-", string.Empty).ToLower();
                }
            }
            else
            {
                data.DataStream = new byte[size];
                FileStream file = new FileStream(tarPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                file.Seek(offset, SeekOrigin.Begin);
                file.Read(data.DataStream, 0, size);
                file.Close();
                data.DataHash = "SHA1:abcdef";
            }

            return data;
        }
    }
}
