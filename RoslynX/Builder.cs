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
    public class Builder: IBuilder
    {
        public BuildResult BuildAndAnalyze(string path)
        {
            var projectName = Path.GetFileNameWithoutExtension(path);
            var result = new BuildResult();
            result.ProjectName = projectName;
            result.ProjectPath = path;

            var outArgs = Helpers.GetArgsString(new string[]
            {
                "build",
                path,
                "--verbosity", "d",
                "--configuration", "Debug",
                //"--no-dependencies",
                //"--no-restore",
                "--nologo",
                //"--tl:off",
                "--no-incremental"
            });

            Console.WriteLine("dotnet " + outArgs);

            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = outArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            proc.ErrorDataReceived += (sender, args) =>
            {
                result.Errors ??= new List<string>();
                Console.WriteLine(args);
                result.Errors.Add(args.Data);
                result.Success = false;
            };

            proc.Start();
            var text = proc.StandardOutput.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text)) return null;

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);



            string proj = null;
            foreach (var line1 in lines)
            {
                var line = line1;
                if (string.IsNullOrWhiteSpace(line)) continue;
                line = line.Trim();

                result.Lines.Add(line);

                if (line.Contains(" error ", StringComparison.Ordinal))
                {
                    result.Errors ??= new();
                    result.Errors.Add(line);
                    continue;
                }

                if (line.Contains("dotnet.exe exec", StringComparison.Ordinal))
                {
                    BuildResult res = null;
                    if (result.ProjectPath == proj)
                    {
                        res = result;
                    }
                    else
                    {
                        if (!result.SubResults.TryGetValue(proj, out res))
                        {
                            res = new BuildResult()
                            {
                                ProjectPath = proj,
                                ProjectName = Path.GetFileNameWithoutExtension(proj)
                            };
                            result.SubResults.Add(proj, res);
                        }
                    }

                    res.CscString = line;

                    var parts1 = line.Split(" /");

                    foreach (var part in parts1)
                    {
                        if (part.StartsWith("define:", StringComparison.Ordinal))
                        {
                            res.Constants = part.Substring("define:".Length).Split(';');
                        }

                        if (part.StartsWith("reference:", StringComparison.Ordinal))
                        {
                            res.AddReference(part.Substring("reference:".Length));
                        }

                        if (part.StartsWith("out:", StringComparison.Ordinal))
                        {
                            res.SubResultOutFilePath = part.Substring("out:".Length);
                        }
                    }

                    continue;
                }

                if (line.Contains("->", StringComparison.Ordinal))
                {

                    var splitArrow = line.Split("->", StringSplitOptions.RemoveEmptyEntries);
                    if (splitArrow.Length == 2)
                    {
                        var projName = splitArrow[0].Trim();
                        var dllPath = splitArrow[1].Trim();

                        foreach (var subResult in result.SubResults.Values)
                        {
                            if (projName.Equals(subResult.ProjectName, StringComparison.Ordinal))
                            {
                                subResult.OutputFilePath = dllPath;
                                subResult.Success = true;
                                Console.WriteLine(line);
                                break;
                            }
                        }

                        if (projName.Equals(projectName, StringComparison.Ordinal))
                        {
                            result.OutputFilePath = dllPath;
                            result.Success = true;
                            Console.WriteLine(line);
                            continue;
                        }
                    }

                }

                if (line.StartsWith("Resolved file path is ", StringComparison.Ordinal))
                {
                    var reference = line.Split("\"", StringSplitOptions.RemoveEmptyEntries)[1].Trim();
                    result.AddReference(reference);
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



            if (result.Success)
            {
                var args = SplitArgs(result.CscString);
                var dir = Path.GetDirectoryName(result.ProjectPath) + "\\";
                result.Sources = GetSources(args, dir);

                foreach (var subResult in result.SubResults.Values)
                {
                    var subArgs = SplitArgs(subResult.CscString);
                    subResult.Sources = GetSources(subArgs, dir);
                }
            }

            return result;
        }

        private string[] GetSources(IEnumerable<string> args, string dir)
        {
            var files = args.Where(p => p.EndsWith(".cs")).ToArray();

            for (int i = 0; i < files.Length; i++)
            {
                if (!Path.IsPathFullyQualified(files[i])) files[i] = dir + files[i];
            }

            return files;
        }
        
        public List<MetadataReference> GetMetadataReferences(BuildResult result)
        {
            List<MetadataReference> refs = new();
            foreach (var reference in result.References)
            {
                if (File.Exists(reference))
                {
                    refs.Add(MetadataReference.CreateFromFile(reference));
                }
            }

            return refs;
        }

        public IEnumerable<DocumentInfo> GetDocuments(BuildResult buildResult, ProjectId projectId,
            Func<string, bool> excludeSource)
        {
            var filtered = new List<string>();

            foreach (var source in buildResult.Sources)
            {
                if (excludeSource != null && excludeSource(source))
                {
                    Console.WriteLine($"Excluded: {Path.GetFileName(source)} Filtered");
                    continue;
                }

                if (!File.Exists(source))
                {
                    Console.WriteLine($"Excluded: {source} File doesn't exist");
                    continue;
                }

                filtered.Add(source);
            }

            return filtered
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
        private static List<string> SplitArgs(string commandLine)
        {
            List<string> argsList = new();

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
                    if (started)
                    {
                        argsList.Add(result.ToString());
                    }
                    result.Clear();
                    started = false;
                }
                else
                {
                    result.Append(chr);
                    started = true;
                }
            }

            if (started)
            {
                argsList.Add(result.ToString());
            }

            return argsList;
        }
    }

    public interface IBuilder
    {
        BuildResult BuildAndAnalyze(string path);
        IEnumerable<DocumentInfo> GetDocuments(BuildResult result, ProjectId projectId, Func<string, bool> excludeSource);
        List<MetadataReference> GetMetadataReferences(BuildResult result);
    }

    public class BuildResult
    {
        public string ProjectPath;
        public string ProjectName;

        public string CscString;
        public bool Success;
        public string[] Sources;
        public string[] Constants;
        public readonly HashSet<string> References = new();

        public string OutputFilePath;

        public Dictionary<string, BuildResult> SubResults = new();

        public readonly List<string> Lines = new();
        public string SubResultOutFilePath;
        public List<string> Errors { get; set; }

        public override string ToString()
        {
            if (ProjectPath == null) return base.ToString();
            return Path.GetFileName(ProjectPath);
        }

        private static readonly string[] _stdLibs = new string[]
        {
            "System", "Microsoft", "Windows", "netstandard", "mscorlib"
        };
        public void AddReference(string reference)
        {
            reference = reference.Trim('"');
            var name = Path.GetFileName(reference);
            //if (_stdLibs.Any(p => name.StartsWith(p))) return;
            References.Add(reference);
        }
    }
}