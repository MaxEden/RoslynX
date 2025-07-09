using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Project = Microsoft.Build.Evaluation.Project;

namespace RoslynX
{
    public class MsBuilder : IBuilder
    {
        public BuildResult BuildAndAnalyze(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));

            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Project file not found", fullPath);

            MSBuildLocator.RegisterDefaults();
            var result = new BuildResult
            {
                ProjectPath = fullPath,
                ProjectName = Path.GetFileNameWithoutExtension(fullPath)
            };

            try
            {
                // Set up global properties
                var globalProperties = new Dictionary<string, string>()
                {
                    ["Configuration"] = "Debug"
                };

                // if (!string.IsNullOrEmpty(_targetFramework))
                // {
                //     globalProperties["TargetFramework"] = _targetFramework;
                // }

                // Load the project
                using var projectCollection = new ProjectCollection(globalProperties);
                var project = new Project(fullPath, globalProperties, null, projectCollection);

                // Build the project
                var buildResult = project.Build(new[] { "Build" });
                result.Success = buildResult;

                if (!buildResult)
                {
                    result.Errors = new List<string> { "Build failed. Check build output for details." };
                    return result;
                }

                // Collect build information
                result.OutputFilePath = GetOutputFilePath(project);
                result.Constants = project.GetPropertyValue("DefineConstants")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries);

                // Collect source files
                result.Sources = project.GetItems("Compile")
                    .Select(i => Path.Combine(project.DirectoryPath, i.EvaluatedInclude))
                    .ToArray();

                // Collect references
                foreach (var reference in project.GetItems("Reference"))
                {
                    var hintPath = reference.GetMetadataValue("HintPath");
                    if (!string.IsNullOrEmpty(hintPath))
                    {
                        var fullHintPath = Path.Combine(project.DirectoryPath, hintPath);
                        if (File.Exists(fullHintPath))
                        {
                            result.AddReference(fullHintPath);
                        }
                    }
                    else if (File.Exists(reference.EvaluatedInclude + ".dll"))
                    {
                        result.AddReference(reference.EvaluatedInclude + ".dll");
                    }
                }

                // Collect project references
                foreach (var projectRef in project.GetItems("ProjectReference"))
                {
                    var refProjectPath = Path.Combine(project.DirectoryPath, projectRef.EvaluatedInclude);
                    var refProject = new Project(refProjectPath, globalProperties, null, projectCollection);

                    var refResult = new BuildResult
                    {
                        ProjectPath = refProjectPath,
                        ProjectName = refProject.GetPropertyValue("AssemblyName"),
                        Success = true,
                        OutputFilePath = GetOutputFilePath(refProject)
                    };

                    result.SubResults[refProjectPath] = refResult;
                }

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors = new List<string> { $"Build error: {ex.Message}" };
                return result;
            }
        }

        public IEnumerable<DocumentInfo> GetDocuments(BuildResult result, ProjectId projectId,
            Func<string, bool> excludeSource)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (projectId == null) throw new ArgumentNullException(nameof(projectId));
            if (excludeSource == null) throw new ArgumentNullException(nameof(excludeSource));

            if (result.Sources == null)
                return Enumerable.Empty<DocumentInfo>();

            List<DocumentInfo> documents = new List<DocumentInfo>();
            foreach (var sourceFile in result.Sources)
            {
                if (excludeSource(sourceFile))
                    continue;

                var sourceText = File.Exists(sourceFile)
                    ? SourceText.From(File.ReadAllText(sourceFile))
                    : SourceText.From("");

                documents.Add(DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId, sourceFile),
                    sourceFile,
                    loader: TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create())),
                    filePath: sourceFile));
            }

            return documents;
        }

        public List<MetadataReference> GetMetadataReferences(BuildResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var references = new List<MetadataReference>();

            if (result.References == null) return references;

            foreach (var reference in result.References)
            {
                try
                {
                    if (File.Exists(reference))
                    {
                        references.Add(MetadataReference.CreateFromFile(reference));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to load reference '{reference}': {ex.Message}");
                }
            }

            return references;
        }

        private static string GetOutputFilePath(Project project)
        {
            var outputPath = project.GetPropertyValue("OutputPath");
            var assemblyName = project.GetPropertyValue("AssemblyName");
            var targetExt = project.GetPropertyValue("TargetExt");

            if (string.IsNullOrEmpty(targetExt))
                targetExt = ".dll";
            else if (!targetExt.StartsWith("."))
                targetExt = "." + targetExt;

            return Path.Combine(
                project.DirectoryPath,
                outputPath,
                $"{assemblyName}{targetExt}");
        }
    }
}