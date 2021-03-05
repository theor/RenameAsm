using System;
using System.Diagnostics;
using System.IO;

namespace RenameAsm
{
    class Program
    {
        
        static void Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine(@"Usage: RenameAsm.exe <assembly> <newname>");
                return;
            }
            string ildasmPath = "ildasm.exe";
            string ilasmPath = "ilasm.exe";
            string dllPath = args[0];
            string newName = args[1];

            string previousName = Path.GetFileNameWithoutExtension(dllPath);
            Console.WriteLine($"replace {previousName} with {newName}");
            Run(ildasmPath, $"{dllPath} /output:pre.il");

            var contents = File.ReadAllText("pre.il")
                    .Replace(previousName, newName)
                // .Replace("Antlr.Runtime", "Unity.VisualScripting.Antlr3.Runtime")
                ;
            File.WriteAllText("post.il", contents);
            Run(ilasmPath, $"/dll /out:{newName}.dll post.il");
            Console.WriteLine("done");

        }

        static void Run(string exe, string args)
        {
            Console.WriteLine($"\"{exe}\" {args}");
            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            p.Start();
            while (!p.StandardOutput.EndOfStream)
            {
                string line = p.StandardOutput.ReadLine();
                Console.WriteLine(line);
            }
            while (!p.StandardError.EndOfStream)
            {
                string line = p.StandardError.ReadLine();
                Console.Error.WriteLine(line);
            }
            p.WaitForExit();
            
        }
    }
}