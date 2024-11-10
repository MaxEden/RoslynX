using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace RoslynX
{
    public class Compiler
    {
        private readonly Dictionary<string, CompilerProjectInfo> _projectInfos = new();
        private readonly Dictionary<string, CompilerDocumentInfo> _docInfos = new();
        private readonly Dictionary<string, CompilerReferenceInfo> _refInfos = new();

        public IReadOnlyDictionary<string, CompilerProjectInfo> ProjectInfos => _projectInfos;
        public IReadOnlyDictionary<string, CompilerDocumentInfo> DocInfos => _docInfos;
        public IReadOnlyDictionary<string, CompilerReferenceInfo> RefInfos => _refInfos;

        public Compiler()
        {
            var _1 = typeof(Microsoft.CodeAnalysis.CSharp.Formatting.CSharpFormattingOptions);
            //var _2 = typeof(Microsoft.CodeAnalysis.MSBuild.ProjectMap);
            var _3 = typeof(Microsoft.CodeAnalysis.CSharp.Formatting.LabelPositionOptions);
        }

        public CompilerProjectInfo BuildProject(string path, string targetFile = null)
        {
            path = Path.GetFullPath(path);

            if (!_projectInfos.TryGetValue(path, out var pi))
            {
                var result = Builder.BuildAndAnalyze(path);

                if (!result.Success)
                {
                    Console.WriteLine($"RoslynX: {Path.GetFileName(path)} has failed!");
                    WriteDebugInfo(result);
                    _projectInfos.Remove(path);
                    return null;
                }

                AddResult(result);
                foreach (var subResult in result.SubResults.Values)
                {
                    AddResult(subResult);
                }
            }

            pi = _projectInfos[path];
            Prepare(pi);
            if (pi.TargetFile == null) pi.TargetFile = targetFile;
            if (pi.TargetFile == null) pi.TargetFile = pi.Proj.OutputFilePath;

            var compilation = pi.Proj.GetCompilationAsync().Result;

            using (var dll = File.Open(pi.TargetFile!, FileMode.OpenOrCreate, FileAccess.Write))
            using (var pdb = File.Open(pi.SymbolFile, FileMode.OpenOrCreate, FileAccess.Write))
            {
                var result = compilation.Emit(dll, pdb);
                bool built = true;
                foreach (var diagnostic in result.Diagnostics)
                {
                    if (diagnostic.Severity == DiagnosticSeverity.Error)
                    {
                        built = false;
                        Console.WriteLine(diagnostic.GetMessage());
                    }
                }

                if (built)
                {
                    Console.WriteLine("RoslynX ->" + pi.TargetFile);
                    //Console.WriteLine("RoslynX ->" + pi.SymbolFile);
                }
            }

            foreach (var reference in pi.Proj.AllProjectReferences)
            {
                var refPath = reference.ProjectId.ToString().Replace(")", "")
                    .Split(" - ", StringSplitOptions.RemoveEmptyEntries)[1];

                if (!_projectInfos.TryGetValue(refPath, out var refInfo)) continue;

                var outputDllPath = Path.Join(pi.OutputDirectory, Path.GetFileName(refInfo.TargetFile));
                var outputPdbPath = Path.Join(pi.OutputDirectory, Path.GetFileName(refInfo.SymbolFile));

                File.Copy(refInfo.TargetFile!, outputDllPath, true);
                File.Copy(refInfo.SymbolFile!, outputPdbPath, true);

                Console.WriteLine("RoslynX copy:" + outputDllPath);
                //Console.WriteLine("RoslynX:" + outputPdbPath);
            }

            ReferenceChanged(pi.TargetFile);

            return pi;
        }

        private static void WriteDebugInfo(BuildResult result)
        {
            Console.WriteLine();
            foreach (var line in result.Lines)
            {
                if (line.Contains(" error ", StringComparison.InvariantCultureIgnoreCase)
                   || line.Contains(" failed", StringComparison.InvariantCultureIgnoreCase))
                {
                    Console.WriteLine(line);
                }
            }

            Console.WriteLine();
        }

        private void AddResult(BuildResult result)
        {
            var pi = new CompilerProjectInfo()
            {
                Path = result.ProjectPath,
                Directory = Path.GetDirectoryName(result.ProjectPath),
                BuildResult = result
            };

            //Console.WriteLine($"Result:{pi.path}");
            _projectInfos[pi.Path] = pi;

            foreach (var reference in result.References)
            {
                var name = Path.GetFileName(reference);
                if (_stdLibs.Any(p => name.StartsWith(p))) continue;

                if (!_refInfos.TryGetValue(reference, out var refInfo))
                {
                    refInfo = new CompilerReferenceInfo();
                    _refInfos[reference] = refInfo;
                }
                //Console.WriteLine($"ref->{reference}");
                refInfo.ProjInfos.TryAdd(pi.Path, pi);
            }
        }

        private readonly string[] _stdLibs = new string[]
        {
            "System", "Microsoft", "Windows", "netstandard", "mscorlib"
        };

        private void Prepare(CompilerProjectInfo pi)
        {
            if (pi.Ws != null) return;

            var result = pi.BuildResult;

            var path = result.ProjectPath;

            var workspace = new AdhocWorkspace();

            var projectId = ProjectId.CreateNewId();

            var cscString = pi.BuildResult.CscString;

            var outputKind = OutputKind.DynamicallyLinkedLibrary;
            if (cscString.Contains("/target:winexe"))
                outputKind = OutputKind.WindowsRuntimeApplication;

            var options = new CSharpCompilationOptions(outputKind)
                .WithDeterministic(cscString.Contains("/deterministic+"))
                .WithAllowUnsafe(cscString.Contains("/unsafe+"));

            ProjectInfo projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                result.ProjectName,
                result.ProjectName,
                LanguageNames.CSharp,
                filePath: path,
                outputFilePath: result.OutputFilePath,
                documents: Builder.GetDocuments(result, projectId, ExcludeSource),
                projectReferences: Array.Empty<ProjectReference>(),
                metadataReferences: Builder.GetMetadataReferences(result),
                analyzerReferences: Array.Empty<AnalyzerReference>(),
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: result.Constants),
                compilationOptions: options);

            var proj = workspace.AddProject(projectInfo);
            pi.Ws = workspace;
            pi.Proj = proj;

            foreach (var doc in pi.Proj.Documents.ToList())
            {
                //??
                //if(!doc.FilePath.StartsWith(pi.Directory)) continue;
                var docInfo = new CompilerDocumentInfo()
                {
                    Doc = doc,
                    ProjInfo = pi
                };

                _docInfos[doc.FilePath] = docInfo;
            }

            _projectInfos[path] = pi;
        }

        public void FileChanged(string path)
        {
            if (_docInfos.TryGetValue(path, out var docInfo))
            {
                var text = File.ReadAllText(path);
                var proj = docInfo.ProjInfo.Proj;
                var doc = docInfo.Doc;

                var src = SourceText.From(text, Encoding.UTF8);
                //var src = docInfo.doc.GetTextAsync().Result;
                //src = src.WithChanges(new TextChange(new TextSpan(0, src.Length), text));

                doc = doc.WithText(src);
                proj = doc.Project;

                docInfo.Doc = doc;
                docInfo.ProjInfo.Proj = proj;
            }
            else
            {
                var project = SourceToProject?.Invoke(path);
                if (project != null)
                {
                    ProjectChanged(project);
                }
                else
                {
                    foreach (var projectInfo in _projectInfos.Values.ToList())
                    {
                        if (path.StartsWith(projectInfo.Directory))
                        {
                            ProjectChanged(projectInfo.Path);
                        }
                    }
                }
            }
        }

        public Func<string, string> SourceToProject;
        public Func<string, bool> ExcludeSource;

        public void ProjectChanged(string path)
        {
            if (!_projectInfos.TryGetValue(path, out var projInfo)) return;

            _projectInfos.Remove(projInfo.Path);

            foreach (var pair in _docInfos.Where(p => p.Value.ProjInfo == projInfo).ToList())
            {
                _docInfos.Remove(pair.Key);
            }
        }

        private void ReferenceChanged(string path)
        {
            if (!_refInfos.TryGetValue(path, out var refInfo)) return;
            foreach (var projInfo in refInfo.ProjInfos.Values)
            {
                var proj = projInfo.Proj;
                if (proj == null) continue;

                projInfo.Ws = null;//HACK
                // var old = proj.MetadataReferences.FirstOrDefault(p => p.Display == path);
                // if(old == null) continue;
                //
                // proj = proj.RemoveMetadataReference(old);
                // var newRef = MetadataReference.CreateFromFile(path);
                // proj = proj.AddMetadataReference(newRef);
                //projInfo.proj = proj;
            }
        }
    }

    public class CompilerProjectInfo
    {
        public AdhocWorkspace Ws;
        public Project Proj;
        public string Directory;
        public string Path;
        public string TargetFile;
        public string SymbolFile => TargetFile.Replace(".dll", ".pdb");
        public string OutputDirectory => System.IO.Path.GetDirectoryName(TargetFile);
        public BuildResult BuildResult;
    }

    public class CompilerDocumentInfo
    {
        public Document Doc;
        public CompilerProjectInfo ProjInfo;
    }

    public class CompilerReferenceInfo
    {
        public readonly Dictionary<string, CompilerProjectInfo> ProjInfos = new();
    }
}