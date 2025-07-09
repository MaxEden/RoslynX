using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
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

        public bool RebuildAfterInitialization { get; set; } = false;
        public bool WritePdb { get; set; } = true;
        public bool AddCounter { get; set; } = false;
        public bool BuildInMemory { get; set; } = false;

        public CompilerProjectInfo BuildProject(string path, string outputFile = null)
        {
            path = Path.GetFullPath(path);

            if (!_projectInfos.TryGetValue(path, out var pi))
            {
                var result = Builder.BuildAndAnalyze(path);

                ProcessResult(result);
                if (!result.Success) return new CompilerProjectInfo()
                {
                    Name = result.ProjectName,
                    Path = result.ProjectPath,
                    Directory = Path.GetDirectoryName(result.ProjectPath),
                    Success = false,
                    Errors = result.Errors.ToList(),
                    BuildResult = result,
                };

                if (!RebuildAfterInitialization)
                {
                    var res = _projectInfos[path];
                    return res;
                }
            }

            pi = _projectInfos[path];
            
            if (pi.Success && pi.Version == _version && File.Exists(pi.OutputFile))
            {
                return pi;
            }

            PrepareRoslyn(pi);

            pi.OutputFile ??= outputFile;
            pi.OutputFile ??= pi.Proj.OutputFilePath;

            var compilation = pi.Proj.GetCompilationAsync().Result;

            if (AddCounter)
            {
                _counter++;
                pi.OutputFile = pi.OriginalOutputFile.Replace(".dll", _counter + ".dll");
            }

            var emitOptions = new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb);
            EmitResult emitResult = default;

            if (BuildInMemory)
            {
                if (WritePdb)
                {
                    var dll = pi.OutputStream;
                    dll.Position=0;
                    dll.SetLength(0);

                    var pdb = pi.OutputSymbolStream;
                    pdb.Position=0;
                    pdb.SetLength(0);

                    emitResult = compilation.Emit(dll, pdb, options: emitOptions);
                }
                else
                {
                    var dll = pi.OutputStream;
                    dll.Position = 0;
                    dll.SetLength(0);

                    emitResult = compilation.Emit(dll, null);
                }
            }
            else
            {
                if (WritePdb)
                {
                    using var dll = File.Open(pi.OutputFile, FileMode.Create, FileAccess.Write);
                    using var pdb = File.Open(pi.SymbolFile, FileMode.Create, FileAccess.Write);
                    emitResult = compilation.Emit(dll, pdb, options: emitOptions);
                }
                else
                {
                    using var dll = File.Open(pi.OutputFile, FileMode.Create, FileAccess.Write);
                    emitResult = compilation.Emit(dll, null);
                }
            }

            


            bool built = true;
            foreach (var diagnostic in emitResult.Diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    if (built)
                    {
                        pi.Errors = new List<string>();
                    }

                    built = false;
                    var msg = diagnostic.GetMessage();
                    Console.WriteLine(msg);
                    pi.Errors.Add(msg);
                }
            }

            pi.Success = built;

            if (built)
            {
                Console.WriteLine("RoslynX ->" + pi.OutputFile);
                //Console.WriteLine("RoslynX ->" + pi.SymbolFile);
            }


            foreach (var reference in pi.Proj.AllProjectReferences)
            {
                var refPath = reference.ProjectId.ToString().Replace(")", "")
                    .Split(" - ", StringSplitOptions.RemoveEmptyEntries)[1];

                if (!_projectInfos.TryGetValue(refPath, out var refInfo)) continue;

                var outputDllPath = Path.Join(pi.OutputDirectory, Path.GetFileName(refInfo.OutputFile));
                var outputPdbPath = Path.Join(pi.OutputDirectory, Path.GetFileName(refInfo.SymbolFile));

                File.Copy(refInfo.OutputFile!, outputDllPath, true);
                File.Copy(refInfo.SymbolFile!, outputPdbPath, true);

                Console.WriteLine("RoslynX copy:" + outputDllPath);
                //Console.WriteLine("RoslynX:" + outputPdbPath);
            }

            ReferenceChanged(pi.OutputFile);

            return pi;
        }

        private static string GetFullClassName(ClassDeclarationSyntax classDeclaration)
        {
            // Get the namespace of the class
            var namespaceDeclaration = classDeclaration.Ancestors()
                .OfType<NamespaceDeclarationSyntax>()
                .FirstOrDefault();

            // Build the full name
            var fullName = namespaceDeclaration != null
                ? $"{namespaceDeclaration.Name}.{classDeclaration.Identifier.Text}"
                : classDeclaration.Identifier.Text;

            // If the class is within a partial class, it might be nested
            var containingClass = classDeclaration.Ancestors()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault();

            while (containingClass != null)
            {
                fullName = $"{containingClass.Identifier.Text}.{fullName}";
                containingClass = containingClass.Ancestors()
                    .OfType<ClassDeclarationSyntax>()
                    .FirstOrDefault();
            }

            return fullName;
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
                    result.Errors ??= new List<string>();
                    result.Errors.Add(line);
                }
            }

            Console.WriteLine();
        }

        private void ProcessResult(BuildResult result)
        {
            if (result.Success && File.Exists(result.OutputFilePath))
            {
                var pi = new CompilerProjectInfo()
                {
                    Name = result.ProjectName,
                    Path = result.ProjectPath,
                    Directory = Path.GetDirectoryName(result.ProjectPath),
                    BuildResult = result
                };

                pi.Version = _version;
                pi.Success = true;
                pi.OutputFile = pi.BuildResult.OutputFilePath;
                pi.OriginalOutputFile = pi.BuildResult.OutputFilePath;

                //Console.WriteLine($"Result:{pi.path}");
                _projectInfos[result.ProjectPath] = pi;

                if (BuildInMemory)
                {
                    {
                        var memoryStream = new MemoryStream();
                        var bytes = File.ReadAllBytes(result.OutputFilePath);
                        memoryStream.Write(bytes);
                        pi.OutputStream = memoryStream;
                    }

                    if (WritePdb)
                    {
                        var path = result.OutputFilePath.Substring(0, result.OutputFilePath.Length - 4) + ".pdb";
                        if (File.Exists(path))
                        {
                            var symbolStream = new MemoryStream();
                            var bytes = File.ReadAllBytes(path);
                            symbolStream.Write(bytes);
                            pi.OutputSymbolStream = symbolStream;
                        }
                    }
                }

                foreach (var reference in result.References)
                {
                    if (!_refInfos.TryGetValue(reference, out var refInfo))
                    {
                        refInfo = new CompilerReferenceInfo();
                        _refInfos[reference] = refInfo;
                    }

                    //Console.WriteLine($"ref->{reference}");
                    refInfo.ProjInfos.TryAdd(pi.Path, pi);
                }
            }
            else
            {
                Console.WriteLine($"RoslynX: {Path.GetFileName(result.ProjectPath)} has failed!");
                WriteDebugInfo(result);
                _projectInfos.Remove(result.ProjectPath);
                return;
            }

            foreach (var subResult in result.SubResults)
            {
                ProcessResult(subResult.Value);
            }
        }

        public static HostServices CreateMinimalCSharpHostServices()
        {
            var csharpAssembly = typeof(CSharpCompilation).GetTypeInfo().Assembly;
            var workspaceAssembly = typeof(Workspace).GetTypeInfo().Assembly;
            var csharpWorkspacesAssembly = Assembly.Load("Microsoft.CodeAnalysis.CSharp.Workspaces");

            // Create an array of assemblies that only contain the minimal services
            var minimalAssemblies = new[] { csharpAssembly, workspaceAssembly, csharpWorkspacesAssembly };

            // Create host services based solely on these assemblies.
            return MefHostServices.Create(minimalAssemblies);
        }

        private void PrepareRoslyn(CompilerProjectInfo pi)
        {
            if (pi.Ws != null) return;

            var result = pi.BuildResult;

            var path = result.ProjectPath;

            var host = CreateMinimalCSharpHostServices();

            var workspace = new AdhocWorkspace(host);

            var projectId = ProjectId.CreateNewId();

            var cscString = pi.BuildResult.CscString;

            var outputKind = OutputKind.DynamicallyLinkedLibrary;
            if (cscString.Contains("/target:winexe"))
                outputKind = OutputKind.WindowsRuntimeApplication;

            var options = new CSharpCompilationOptions(outputKind)
                .WithDeterministic(cscString.Contains("/deterministic+"))
                .WithAllowUnsafe(cscString.Contains("/unsafe+"));
            
            var documents = Builder.GetDocuments(result, projectId, ExcludeSource).ToArray();
            var metadataReferences = Builder.GetMetadataReferences(result).ToArray();
            var parseOptions = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: result.Constants);
            var outputFilePath = result.OutputFilePath;

            ProjectInfo projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                result.ProjectName,
                result.ProjectName,
                LanguageNames.CSharp,
                filePath: path,
                outputFilePath: outputFilePath,
                documents: documents,
                projectReferences: Array.Empty<ProjectReference>(),
                metadataReferences: metadataReferences,
                analyzerReferences: Array.Empty<AnalyzerReference>(),
                parseOptions: parseOptions,
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
            _version++;
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
        private int _counter;
        private int _version;

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

                projInfo.Ws = null; //HACK
                // var old = proj.MetadataReferences.FirstOrDefault(p => p.Display == path);
                // if(old == null) continue;
                //
                // proj = proj.RemoveMetadataReference(old);
                // var newRef = MetadataReference.CreateFromFile(path);
                // proj = proj.AddMetadataReference(newRef);
                //projInfo.proj = proj;
            }
        }

        public void Invalidate()
        {
            _version++;
        }

        public void ResolveDocumentClassNames(CompilerProjectInfo projectInfo)
        {
            PrepareRoslyn(projectInfo);
            foreach (var pair in _docInfos)
            {
                if (pair.Value.ProjInfo != projectInfo) continue;

                var root = pair.Value.Doc.GetSyntaxRootAsync().Result;

                var classDeclaration = root.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>().FirstOrDefault();

                if (classDeclaration != null)
                {
                    pair.Value.FullTypeName = GetFullClassName(classDeclaration);
                }
            }
        }
    }

    public class CompilerProjectInfo
    {
        public AdhocWorkspace Ws { get; internal set; }
        public Project Proj { get; internal set; }
        public string Directory { get; internal set; }
        public string Path { get; internal set; }
        public string OriginalOutputFile { get; internal set; }
        public string OutputFile { get; internal set; }
        public string SymbolFile => OutputFile.Replace(".dll", ".pdb");
        public string OutputDirectory => System.IO.Path.GetDirectoryName(OutputFile);
        public bool Success { get; internal set; }
        public List<string> Errors { get; internal set; }
        public int Version { get; internal set; }
        public MemoryStream OutputStream { get; internal set; }
        public MemoryStream OutputSymbolStream { get; internal set; }
        public BuildResult BuildResult { get; internal set; }
        public string Name { get; internal set; }
    }

    public class CompilerDocumentInfo
    {
        public Document Doc { get; internal set; }
        public CompilerProjectInfo ProjInfo { get; internal set; }
        public string FullTypeName { get; internal set; }
    }

    public class CompilerReferenceInfo
    {
        public readonly Dictionary<string, CompilerProjectInfo> ProjInfos = new();
    }
}