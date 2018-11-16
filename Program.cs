﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static System.Console;

namespace containersec
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var vulnTypes = new List<string>()
            {
                "Critical",
                "High",
                "Medium",
                "Low",
                "Negligible",
                "Unknown"
            };

            var dir = "/Users/rich/git/containersec/repos";

            foreach(var file in Directory.GetFiles(dir))
            {
                var lines = File.ReadAllLines(file);
                var repo = lines[0];
                var tag = lines[1];

                for (int i = 20;i < lines.Length;i++)
                {
                    var digest = lines[i];
                    var image = $"{repo}@{digest}";
                    var pullReturnCode = DockerPullDigest(image);

                    if (pullReturnCode != 0)
                    {
                        return;
                    }

                    var (inspectReturnCode, timestamp) = GetTimestampForDigest(image);
                    var token = GetTokenForImage(repo, tag, digest, timestamp);
                    await RegisterImage(token);
                }
            }

            var vBuffer = new StringBuilder();
            foreach(var vType in vulnTypes)
            {
                vBuffer.Append($",{vType}");
            }

            WriteLine($"Repo,Tag,Timestamp{vBuffer.ToString()},Digest");
            
            foreach(var file in Directory.GetFiles(dir))
            {

                var lines = File.ReadAllLines(file);
                var repo = lines[0];
                var tag = lines[1];

                for (int i = 2;i < lines.Length;i++)
                {
                    var digest = lines[i];
                    var image = $"{repo}@{digest}";
                    var (inspectReturnCode, timestamp) = GetTimestampForDigest(image);
                    var token = GetTokenForImage(repo, tag, digest, timestamp);
                    var analyzed = false;
                    while (!analyzed)
                    {
                        analyzed = await RegisterImage(token);
                        await Task.Delay(500);
                    }

                    var vulnerabilities = await GetVulnerabilitiesForToken(token);

                    foreach(var v in vulnerabilities)
                    {
                        var vulns = token.Vulnerabilities;
                        if (!vulns.TryGetValue(v.severity, out var sevList))
                        {   
                            sevList = new List<Vulnerability>();
                            vulns.Add(v.severity,sevList);
                        }

                        sevList.Add(v);
                    }

                    var vulnBuffer = new StringBuilder();
                    
                    foreach(var vType in vulnTypes)
                    {
                        var count = 0;
                        if (token.Vulnerabilities.TryGetValue(vType, out var vulns))
                        {
                            count = vulns.Count;
                        }
                        vulnBuffer.Append($",{count}");
                    }

                    var shortTime = timestamp.Substring(0,timestamp.IndexOf("T"));
                    WriteLine($"{repo},{tag},{shortTime}{vulnBuffer.ToString()},{image}");
                    
                }

            }

        }

        private static async Task<IList<Vulnerability>> GetVulnerabilitiesForToken(DigestInfo token)
        {
            // curl -XGET -u admin:foobar 
            // http://localhost:8228/v1/images/sha256:bbb3345ed2e7548dc7a53385b724374ecfb166489a1066cc31b345d0d767df78/vuln/all

            var request = GetRequestMessage();
            request.Method = HttpMethod.Get;
            request.RequestUri = new Uri($"{request.RequestUri}/{token.digest}/vuln/all");
            var client = new HttpClient();
            var result = await client.SendAsync(request);
            var resultJson = await result.Content.ReadAsStringAsync();
            var resultObj = JsonConvert.DeserializeObject<JToken>(resultJson);
            var vulnArray = resultObj.Value<JArray>("vulnerabilities");
            var vulnerabilities = vulnArray.ToObject<List<Vulnerability>>();
            return vulnerabilities;
        }

        private static HttpRequestMessage GetRequestMessage()
        {
            var request = new HttpRequestMessage();
            request.RequestUri = new Uri("http://localhost:8228/v1/images"); // this endpoint expects basic auth with "username" and "password" as the credential
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:foobar")));
            return request;
        }

        private static async Task<bool> RegisterImage(DigestInfo token)
        {
            var request = GetRequestMessage();
            request.Method = HttpMethod.Post;
            var json = JsonConvert.SerializeObject(token);
            var content = new StringContent(json);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content = content;
            var client = new HttpClient();
            var result = await client.SendAsync(request);
            var resultJson = await result.Content.ReadAsStringAsync();
            var resultArray = JsonConvert.DeserializeObject<object[]>(resultJson);
            var resultObj = (JToken)resultArray[0];
            var analyzed = resultObj.Value<string>("analysis_status");

            return (analyzed == "analyzed");
        }

        private static DigestInfo GetTokenForImage(string repo, string tag, string digest, string timestamp)
        {
            return new DigestInfo(){
                tag = $"{repo}:{tag}",
                digest = digest,
                created_at = timestamp
            };
        }

        private static (int,string) GetTimestampForDigest(string image)
        {
            var inspect = "inspect --format={{.Created}}" + $" {image}";
            var start = new ProcessStartInfo("docker");
            start.Arguments = inspect;
            start.RedirectStandardOutput = true;
            var process = Process.Start(start);
            var date = string.Empty;
            using (var stream = process.StandardOutput)
            {
                string output = null;
                while ((output = stream.ReadLine()) != null)
                {
                    date = output;
                }
            }

            process.WaitForExit();

            var index = date.IndexOf('.');
            var shortDate = date.Substring(0,index) + "Z";

            return (process.ExitCode,shortDate);
        }

        private static int DockerPullDigest(string image)
        {
            var pull = $"pull {image}";
            var process = Process.Start("docker",pull);
            process.WaitForExit();
            return process.ExitCode;
        }
    }
}