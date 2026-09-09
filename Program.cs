using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace RenameAsm;

/// <summary>Renames .NET assemblies: file name, assembly name, module name and namespaces.
/// When several assemblies are given, references between them are rewritten too.</summary>
static class Program
{
    const string Usage = """
        Usage:
          RenameAsm <assembly> <newname> [--out <dir>] [--also <old>=<new>]...
          RenameAsm --prefix <prefix> <assembly>... [--out <dir>] [--also <old>=<new>]...

        Each input assembly is disassembled, every occurrence of its name is replaced
        (assembly, module, namespaces, string literals, references from the other inputs),
        and it is reassembled into <dir> (default: current directory).

        --also renames an extra name, typically a namespace that does not start with the
        assembly name. The tool warns about public namespaces the rename leaves untouched.
        """;

    static int Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options is null)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }
            new Renamer(options).Run();
            Console.WriteLine("done");
            return 0;
        }
        catch (RenameException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }
}

sealed record Options(IReadOnlyList<string> Inputs, Func<string, string> NewName, string OutDir, IReadOnlyDictionary<string, string> Also)
{
    public static Options? Parse(string[] args)
    {
        string? prefix = null, outDir = null;
        var positional = new List<string>();
        var also = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--prefix" when i + 1 < args.Length: prefix = args[++i]; break;
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--also" when i + 1 < args.Length && args[i + 1].Split('=') is [var old, var @new] && old != "" && @new != "":
                    also[old] = @new; i++; break;
                case "-h" or "--help": return null;
                default: positional.Add(args[i]); break;
            }
        }
        outDir = Path.GetFullPath(outDir ?? Directory.GetCurrentDirectory());

        if (prefix is not null)
            return positional.Count == 0 ? null : new Options(positional, old => prefix + old, outDir, also);
        if (positional.Count == 2)
            return new Options([positional[0]], _ => positional[1], outDir, also);
        return null;
    }
}

sealed class RenameException(string message) : Exception(message);

sealed class Renamer(Options options)
{
    readonly string _ildasm = FindTool("ildasm");
    readonly string _ilasm = FindTool("ilasm");

    public void Run()
    {
        var jobs = options.Inputs.Select(Job.Create).ToList();
        var renames = jobs.ToDictionary(j => j.OldName, j => options.NewName(j.OldName));
        foreach (var (old, @new) in options.Also) renames[old] = @new;
        var replacer = new NameReplacer(renames);
        WarnAboutUncoveredNamespaces(jobs, replacer);
        Directory.CreateDirectory(options.OutDir);

        foreach (var job in jobs)
        {
            var output = Path.Combine(options.OutDir, renames[job.OldName] + job.Extension);
            Console.WriteLine($"{job.Input} -> {output}");
            Process(job, replacer, output);
            Verify(output, renames[job.OldName], renames);
        }
    }

    void Process(Job job, NameReplacer replacer, string output)
    {
        var work = Directory.CreateTempSubdirectory("RenameAsm-").FullName;
        try
        {
            var preIl = Path.Combine(work, "pre.il");
            var postIl = Path.Combine(work, "post.il");

            Exec(_ildasm, ["-UTF8", "-CAVERBAL", $"-OUT={preIl}", job.Input]);
            if (!File.Exists(preIl)) throw new RenameException($"ildasm produced no output for {job.Input}");

            File.WriteAllText(postIl, replacer.Apply(File.ReadAllText(preIl)));

            // ildasm extracts embedded resources next to the .il; ilasm looks them up by their new names.
            foreach (var resource in Directory.GetFiles(work).Where(f => f != preIl && f != postIl))
            {
                var renamed = Path.Combine(work, replacer.Apply(Path.GetFileName(resource)));
                if (renamed != resource) File.Move(resource, renamed);
            }

            Exec(_ilasm, ["-QUIET", "-NOLOGO", job.IsExe ? "-EXE" : "-DLL", $"-OUTPUT={output}", postIl]);
            if (!File.Exists(output)) throw new RenameException($"ilasm produced no output for {job.Input}");
            Directory.Delete(work, recursive: true);
        }
        catch (RenameException e)
        {
            throw new RenameException($"{e.Message}\n  intermediate files kept in {work}");
        }
    }

    /// <summary>A public type the rename does not touch keeps its old full name, which collides with
    /// the original assembly if both end up loaded. Point them out, grouped by namespace.</summary>
    static void WarnAboutUncoveredNamespaces(IEnumerable<Job> jobs, NameReplacer replacer)
    {
        foreach (var job in jobs)
        {
            var untouched = job.PublicTypes.Where(t => replacer.Apply(t) == t).ToList();
            foreach (var group in untouched.GroupBy(t => t[..t.LastIndexOf('.')]))
                Console.Error.WriteLine($"warning: {job.OldName}: namespace '{group.Key}' keeps public types {string.Join(", ", group.Select(t => t[(group.Key.Length + 1)..]))}; use --also {group.Key}=<new> or --also <type>=<new> if they may collide");
        }
    }

    /// <summary>Reads the result back and checks the rename took, including references to the other inputs.</summary>
    static void Verify(string output, string expectedName, IReadOnlyDictionary<string, string> renames)
    {
        using var pe = new PEReader(File.OpenRead(output));
        var md = pe.GetMetadataReader();
        var name = md.GetString(md.GetAssemblyDefinition().Name);
        if (name != expectedName)
            throw new RenameException($"{output}: assembly name is '{name}', expected '{expectedName}'");

        var stale = md.AssemblyReferences
            .Select(h => md.GetString(md.GetAssemblyReference(h).Name))
            .Where(renames.ContainsKey)
            .ToList();
        if (stale.Count > 0)
            throw new RenameException($"{output}: still references un-renamed assemblies: {string.Join(", ", stale)}");
    }

    static void Exec(string exe, string[] args)
    {
        Console.WriteLine($"  {Path.GetFileName(exe)} {string.Join(' ', args)}");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) start.ArgumentList.Add(a);

        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new RenameException($"could not start {exe}");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new RenameException($"{Path.GetFileName(exe)} exited with code {process.ExitCode}");
    }

    static string FindTool(string name)
    {
        var file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, file),
            Path.Combine(AppContext.BaseDirectory, "runtimes", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "native", file),
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new RenameException($"{file} not found next to the application ({AppContext.BaseDirectory})");
    }
}

sealed record Job(string Input, string OldName, string Extension, bool IsExe, IReadOnlyList<string> PublicTypes)
{
    public static Job Create(string path)
    {
        var input = Path.GetFullPath(path);
        if (!File.Exists(input)) throw new RenameException($"{path} does not exist");

        string assemblyName;
        List<string> publicTypes;
        try
        {
            using var pe = new PEReader(File.OpenRead(input));
            var md = pe.GetMetadataReader();
            assemblyName = md.GetString(md.GetAssemblyDefinition().Name);
            // Top-level public types and forwarded types, as namespace-qualified names.
            publicTypes = md.TypeDefinitions.Select(md.GetTypeDefinition)
                .Where(t => (t.Attributes & System.Reflection.TypeAttributes.VisibilityMask) == System.Reflection.TypeAttributes.Public)
                .Select(t => (ns: md.GetString(t.Namespace), name: md.GetString(t.Name)))
                .Concat(md.ExportedTypes.Select(md.GetExportedType).Select(t => (ns: md.GetString(t.Namespace), name: md.GetString(t.Name))))
                .Where(t => t.ns != "")
                .Select(t => $"{t.ns}.{t.name}").Distinct().Order().ToList();
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException)
        {
            throw new RenameException($"{path} is not a managed assembly: {e.Message}");
        }

        var fileName = Path.GetFileNameWithoutExtension(input);
        if (fileName != assemblyName)
            Console.Error.WriteLine($"warning: {path}: file name '{fileName}' differs from assembly name '{assemblyName}'; renaming '{assemblyName}'");

        var extension = Path.GetExtension(input);
        return new Job(input, assemblyName, extension, extension.Equals(".exe", StringComparison.OrdinalIgnoreCase), publicTypes);
    }
}

/// <summary>Replaces each old name with its new one in a single pass, longest names first,
/// only where the name is not part of a longer dotted identifier.</summary>
sealed class NameReplacer
{
    readonly Regex _pattern;
    readonly IReadOnlyDictionary<string, string> _renames;

    public NameReplacer(IReadOnlyDictionary<string, string> renames)
    {
        _renames = renames;
        var alternatives = renames.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape);
        _pattern = new Regex($@"(?<![A-Za-z0-9_.]) (?:{string.Join('|', alternatives)}) (?![A-Za-z0-9_])",
            RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);
    }

    public string Apply(string text) => _pattern.Replace(text, m => _renames[m.Value]);
}
