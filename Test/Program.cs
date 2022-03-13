using RoslynX;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Test
{
    class Program
    {
        static void Main(string[] args)
        {
            Test();
        }

        static void Test()
        {
            var compiler = new Compiler();
            var projPath = Path.GetFullPath(@"..\..\..\..\TestHelloWorld\TestHelloWorld.csproj");
            var filePath = Path.GetFullPath(@"..\..\..\..\TestHelloWorld\Program.cs");
            var resultPath = Path.GetFullPath(@"..\..\..\..\TestHelloWorld\bin\Debug\net6.0\TestHelloWorld.dll");

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{Path.GetFullPath(projPath)}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            using (new Measurer("dotnet build first"))
            {
                var proc = new Process
                {
                    StartInfo = startInfo
                };

                proc.Start();
                proc.WaitForExit();
            }
            Console.WriteLine();
            using (new Measurer("dotnet build second"))
            {
                var proc = new Process
                {
                    StartInfo = startInfo
                };

                proc.Start();
                proc.WaitForExit();
            }
            Console.WriteLine();
            using (new Measurer("RoslynX First build"))
            {
                compiler.BuildProject(projPath);
            }
            Console.WriteLine();
            var origText = File.ReadAllText(filePath);
            var srcText = origText;
            for (int i = 0; i < 5; i++)
            {
                var addText = $"\n public class TestClass_{i}_{DateTime.Now.Second} {{}}";
                srcText = srcText + addText;
                File.WriteAllText(filePath, srcText);

                using (new Measurer("RoslynX Subsequent build " + i))
                {
                    compiler.FileChanged(filePath);
                    compiler.BuildProject(projPath);
                }
                Console.WriteLine();
            }

            var assembly = Assembly.LoadFile(resultPath);
            Console.WriteLine("Types found:");
            foreach (var type in assembly.GetTypes())
            {
                Console.WriteLine("-" + type.Name);
            }

            File.WriteAllText(filePath, origText);
            Console.WriteLine("Done!");
        }
    }
}
