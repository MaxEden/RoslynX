# RoslynX
RoslynX is a continuous compilation library based on Roslyn.

RoslynX omits the MSBuild dependency notorious for its initialization order and compatibility issues.   
Instead RoslynX relies on assumption that dotnet cli tools are installed and working correctly.  
RoslynX parses verbose output of a common "dotnet build" command and constructs AdHoc workspace based on gathered data.  

RoslynX drastically decreases build times for small changes (Up to x100).
Minimal dependencies list allow it to integrate in any project with minimal impact and coupled with wonderful [DotNetCorePlugins](https://github.com/natemcmaster/DotNetCorePlugins) RoslynX can provide a robust live coding solution.

Example:
```c#
var compiler = new RoslynX.Compiler();

//first build uses 'dotnet build' and gathers compilation data
compiler.BuildProject(projPath); 

//call whenever it's known that a source file has been changed to notify RoslynX
//it will update roslyn compilation tree
compiler.FileChanged(filePath);

compiler.BuildProject(projPath);
//any subsequent call will use roslyn to recompile all changed projects
//RoslynX will fallback to regular 'dotnet build' if changes were to drastic 
//RoslynX will also copy all rebuilt dependencies to the output directory 
```
Rough measurements for hello world type of program
```
Finished dotnet build first in 1163,0451 ms
Finished dotnet build second in 1086,7927 ms

Finished RoslynX First build in 3821,2385 ms
Finished RoslynX Subsequent build 0 in 44,2804 ms
Finished RoslynX Subsequent build 1 in 12,8669 ms
Finished RoslynX Subsequent build 2 in 6,4452 ms
Finished RoslynX Subsequent build 3 in 7,1468 ms
Finished RoslynX Subsequent build 4 in 6,0338 ms
```