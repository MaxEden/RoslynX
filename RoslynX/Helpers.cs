using System;
using System.IO;
using System.Linq;

namespace RoslynX
{
    public class Helpers
    {
        public static string[] SearchDown(string path, string pattern, params string[] ignore)
        {
            if(path == null) return null;

            var dir = path;
            if(!Directory.Exists(dir))
            {
                dir = Path.GetDirectoryName(path);
            }

            if(dir == null) return null;
            return Directory.GetFiles(dir, pattern, SearchOption.AllDirectories)
                .Where(p => !ignore.Any(x => p.Contains(x)))
                .ToArray();
        }

        public static string GetArgsString(string[] args)
        {
            var args1 = args.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            for(int i = 0; i < args1.Count; i++)
            {
                if(args1[i].Any(p => Char.IsWhiteSpace(p)))
                {
                    args1[i] = "\"" + args1[i] + "\"";
                }
            }

            var argsString = String.Join(' ', args1);
            return argsString;
        }

    }
}