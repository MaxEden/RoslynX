using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynX
{
    public class Builder
    {
        public static BuildResult BuildAndAnalyze(string path)
        {
            var projectName = Path.GetFileNameWithoutExtension(path);
            var result = new BuildResult();
            result.ProjectName = projectName;
            result.ProjectPath = path;

            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = Helpers.GetArgsString(new string[]
                    {
                        "build",
                        path,
                        "--verbosity", "d",
                        "--configuration", "Debug",
                        "--no-dependencies",
                        "--no-incremental"
                    }),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            proc.ErrorDataReceived += (sender, args) =>
            {
                Console.WriteLine(args);
                result.Success = false;
            };

            proc.Start();

            string proj = null;
            bool nextLineIsCsc = false;
            while (true)
            {
                if (!proc.StandardOutput.EndOfStream)
                {
                    string line = proc.StandardOutput.ReadLine()?.Trim();
                    if(line == null) continue;
                    
                    result.Lines.Add(line);
                    //Console.WriteLine(line);

                    if (line.Contains("dotnet.exe exec", StringComparison.Ordinal))
                    {
                        nextLineIsCsc = false;

                        BuildResult res = null;
                        if (result.ProjectPath == proj)
                        {
                            res = result;
                        }
                        else
                        {
                            res = new BuildResult()
                            {
                                ProjectPath = proj,
                                ProjectName = Path.GetFileNameWithoutExtension(proj)
                            };
                            result.SubResults.Add(proj, res);
                        }

                        res.CscString = line;
                        //var args = SplitArgs(line);

                        var parts1 = line.Split(" /");

                        foreach (var part in parts1)
                        {
                            if (part.StartsWith("define:"))
                            {
                                res.Constants = part.Substring("define:".Length).Split(';');
                            }

                            if (part.StartsWith("reference:"))
                            {
                                res.References.Add(part.Substring("reference:".Length));
                            }

                            if (part.StartsWith("out:"))
                            {
                                res.OutputFilePath = part.Substring("out:".Length);
                            }
                        }

                        res.OutputStart = line.IndexOf(" /out:", StringComparison.Ordinal) + " /out:".Length;
                        res.OutputEnd = line.IndexOf(" /", res.OutputStart, StringComparison.Ordinal);

                        continue;
                    }

                    if (line.StartsWith(projectName))
                    {
                        result.OutputFilePath = line.Split("->", StringSplitOptions.RemoveEmptyEntries)[1].Trim();
                        result.Success = true;
                        Console.WriteLine(line);
                        continue;
                    }

                    if (line.StartsWith("Resolved file path is "))
                    {
                        var reference = line.Split("\"", StringSplitOptions.RemoveEmptyEntries)[1].Trim();
                        result.References.Add(reference);
                        //Console.WriteLine(line);
                        continue;
                    }

                    var parts = line.Split('>', '\"');
                    if (parts.Length > 2 && parts[2] == "CoreCompile" && parts.Length > 6)
                    {
                        proj = parts[6];
                        continue;
                    }

                    // if (parts.Length > 1 && parts[0] == "Task " && parts[1].Contains("Csc"))
                    // {
                    //     nextLineIsCsc = true;
                    //     continue;
                    // }
                }

                if (proc.HasExited)
                {
                    break;
                }
            }

            var args = SplitArgs(result.CscString);
            var dir = Path.GetDirectoryName(result.ProjectPath) + "\\";

            result.Sources = args
                .Where(p => p.EndsWith(".cs"))
                .Select(p => dir + p)
                .ToArray();

            foreach (var subResult in result.SubResults.Values)
            {
                var subArgs = SplitArgs(subResult.CscString);
                subResult.Sources = subArgs
                    .Where(p => p.EndsWith(".cs"))
                    .Select(p => dir + p)
                    .ToArray();
            }

            return result;
        }


        public static IEnumerable<MetadataReference> GetMetadataReferences(BuildResult result)
        {
            return result
                .References.Where(File.Exists)
                .Select(x => MetadataReference.CreateFromFile(x));
        }

        public static IEnumerable<DocumentInfo> GetDocuments(BuildResult buildResult, ProjectId projectId)
        {
            return buildResult
                .Sources.Where(File.Exists)
                .Select(x => DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId),
                    Path.GetFileName(x),
                    loader: TextLoader.From(
                        TextAndVersion.Create(
                            SourceText.From(File.ReadAllText(x), Encoding.UTF8),
                            VersionStamp.Create(),
                            x)),
                    filePath: x));
        }


        //Mikescher
        //https://stackoverflow.com/questions/298830/split-string-containing-command-line-parameters-into-string-in-c-sharp/298968#298968
        private static IEnumerable<string> SplitArgs(string commandLine)
        {
            var result = new StringBuilder();

            var quoted = false;
            var escaped = false;
            var started = false;
            var allowcaret = false;
            for (int i = 0; i < commandLine.Length; i++)
            {
                var chr = commandLine[i];

                if (chr == '^' && !quoted)
                {
                    if (allowcaret)
                    {
                        result.Append(chr);
                        started = true;
                        escaped = false;
                        allowcaret = false;
                    }
                    else if (i + 1 < commandLine.Length && commandLine[i + 1] == '^')
                    {
                        allowcaret = true;
                    }
                    else if (i + 1 == commandLine.Length)
                    {
                        result.Append(chr);
                        started = true;
                        escaped = false;
                    }
                }
                else if (escaped)
                {
                    result.Append(chr);
                    started = true;
                    escaped = false;
                }
                else if (chr == '"')
                {
                    quoted = !quoted;
                    started = true;
                }
                else if (chr == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    escaped = true;
                }
                else if (chr == ' ' && !quoted)
                {
                    if (started) yield return result.ToString();
                    result.Clear();
                    started = false;
                }
                else
                {
                    result.Append(chr);
                    started = true;
                }
            }

            if (started) yield return result.ToString();
            }
    }

    public class BuildResult
    {
        public string ProjectPath;
        public string ProjectName;

        public          string          CscString;
        public          bool            Success;
        public          string[]        Sources;
        public          string[]        Constants;
        public readonly HashSet<string> References = new();

        public string OutputFilePath;
        public int    OutputStart;
        public int    OutputEnd;

        public Dictionary<string, BuildResult> SubResults = new();

        public readonly List<string> Lines = new();

        public override string ToString()
        {
            if (ProjectPath == null) return base.ToString();
            return Path.GetFileName(ProjectPath);
        }
    }
}