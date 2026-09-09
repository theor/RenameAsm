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
          RenameAsm <assembly> <newname> [options]
          RenameAsm --prefix <prefix> <assembly>... [options]

        Options:
          --out <dir>          output directory (default: current directory)
          --also <old>=<new>   rename an extra name; may be repeated
          --strip-key          remove the strong-name public key

        Each input assembly is disassembled, every occurrence of its name is replaced
        (assembly, module, namespaces, string literals, references from the other inputs),
        and it is reassembled into <dir> (default: current directory).

        --also renames an extra name, typically a namespace that does not start with the
        assembly name. The tool warns about public namespaces the rename leaves untouched.

        --strip-key drops the public key from each input, the public key token from references
        between the inputs, and the PublicKey= part of InternalsVisibleTo entries, so the
        outputs are plain unsigned assemblies instead of signed-looking ones with no signature.
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

sealed record Options(IReadOnlyList<string> Inputs, Func<string, string> NewName, string OutDir, IReadOnlyDictionary<string, string> Also, bool StripKey)
{
    public static Options? Parse(string[] args)
    {
        string? prefix = null, outDir = null;
        var positional = new List<string>();
        var also = new Dictionary<string, string>();
        var stripKey = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--prefix" when i + 1 < args.Length: prefix = args[++i]; break;
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--also" when i + 1 < args.Length && args[i + 1].Split('=') is [var old, var @new] && old != "" && @new != "":
                    also[old] = @new; i++; break;
                case "--strip-key": stripKey = true; break;
                case "-h" or "--help": return null;
                default: positional.Add(args[i]); break;
            }
        }
        outDir = Path.GetFullPath(outDir ?? Directory.GetCurrentDirectory());

        if (prefix is not null)
            return positional.Count == 0 ? null : new Options(positional, old => prefix + old, outDir, also, stripKey);
        if (positional.Count == 2)
            return new Options([positional[0]], _ => positional[1], outDir, also, stripKey);
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

        var outputNames = jobs.Select(j => renames[j.OldName]).ToHashSet();
        var keyStripper = options.StripKey ? new KeyStripper(outputNames) : null;

        foreach (var job in jobs)
        {
            var output = Path.Combine(options.OutDir, renames[job.OldName] + job.Extension);
            Console.WriteLine($"{job.Input} -> {output}");
            Process(job, replacer, keyStripper, output);
            Verify(output, renames[job.OldName], renames, keyStripper is not null);
        }
    }

    void Process(Job job, NameReplacer replacer, KeyStripper? keyStripper, string output)
    {
        var work = Directory.CreateTempSubdirectory("RenameAsm-").FullName;
        try
        {
            var preIl = Path.Combine(work, "pre.il");
            var postIl = Path.Combine(work, "post.il");

            Exec(_ildasm, ["-UTF8", "-CAVERBAL", $"-OUT={preIl}", job.Input]);
            if (!File.Exists(preIl)) throw new RenameException($"ildasm produced no output for {job.Input}");

            var il = replacer.Apply(File.ReadAllText(preIl));
            if (keyStripper is not null) il = keyStripper.Strip(il);
            File.WriteAllText(postIl, il);

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
    static void Verify(string output, string expectedName, IReadOnlyDictionary<string, string> renames, bool keyStripped)
    {
        using var pe = new PEReader(File.OpenRead(output));
        var md = pe.GetMetadataReader();
        var definition = md.GetAssemblyDefinition();
        var name = md.GetString(definition.Name);
        if (name != expectedName)
            throw new RenameException($"{output}: assembly name is '{name}', expected '{expectedName}'");

        var references = md.AssemblyReferences.Select(md.GetAssemblyReference).ToList();
        var stale = references.Select(r => md.GetString(r.Name)).Where(renames.ContainsKey).ToList();
        if (stale.Count > 0)
            throw new RenameException($"{output}: still references un-renamed assemblies: {string.Join(", ", stale)}");

        if (!keyStripped) return;
        if (!definition.PublicKey.IsNil)
            throw new RenameException($"{output}: still carries a public key");
        if (pe.PEHeaders.CorHeader is { Flags: var flags } && flags.HasFlag(CorFlags.StrongNameSigned))
            throw new RenameException($"{output}: CLR header still has the StrongNameSigned flag");
        var signedReferences = references
            .Where(r => renames.Values.Contains(md.GetString(r.Name)) && !r.PublicKeyOrToken.IsNil)
            .Select(r => md.GetString(r.Name)).ToList();
        if (signedReferences.Count > 0)
            throw new RenameException($"{output}: still references by public key token: {string.Join(", ", signedReferences)}");
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

/// <summary>Turns signed inputs into plain unsigned assemblies at the IL level: drops the
/// .publickey of the definition, the .publickeytoken of references to the other outputs, and the
/// PublicKey= part of InternalsVisibleTo entries (a friend without a key cannot match one).</summary>
sealed class KeyStripper(IReadOnlySet<string> outputAssemblies)
{
    static readonly Regex IvtPublicKey = new(@",\s*PublicKey\s*=\s*[0-9A-Fa-f]+", RegexOptions.Compiled);
    static readonly Regex CorFlags = new(@"^(\.corflags 0x)([0-9A-Fa-f]{8})", RegexOptions.Compiled);
    const int StrongNameSignedFlag = 0x8;

    public string Strip(string il)
    {
        var lines = il.Split('\n');
        var kept = new List<string>(lines.Length);
        string? block = null; // "" inside the .assembly definition, the name inside an .assembly extern, else null

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith(".assembly extern ")) block = FirstIdentifier(trimmed[".assembly extern ".Length..]);
            else if (trimmed.StartsWith(".assembly ")) block = "";
            else if (line.StartsWith('}')) block = null;

            var dropDefinitionKey = block == "" && trimmed.StartsWith(".publickey = (");
            var dropReferenceToken = block is { Length: > 0 } && outputAssemblies.Contains(block) && trimmed.StartsWith(".publickeytoken = (");
            if (dropDefinitionKey || dropReferenceToken)
            {
                // The byte blob spans lines; each ends with a "// ascii" comment that may itself contain ')'.
                while (!ClosesBlob(lines[i])) i++;
                continue;
            }

            // InternalsVisibleTo("Friend, PublicKey=...") entries live in the definition block; the
            // verbal string sits on the line after the .custom header, so scan the whole block.
            if (block == "") line = IvtPublicKey.Replace(line, "");
            // The CLR header must not claim a signature that is no longer there.
            if (line.StartsWith(".corflags 0x"))
                line = CorFlags.Replace(line, m => m.Groups[1].Value + (Convert.ToInt32(m.Groups[2].Value, 16) & ~StrongNameSignedFlag).ToString("X8"));
            kept.Add(line);
        }
        return string.Join('\n', kept);
    }

    static bool ClosesBlob(string line)
    {
        var comment = line.IndexOf("//", StringComparison.Ordinal);
        return (comment < 0 ? line : line[..comment]).Contains(')');
    }

    static string FirstIdentifier(string text)
    {
        var end = text.IndexOfAny([' ', '\t', '\r', '\n']);
        var name = end < 0 ? text : text[..end];
        return name.Trim('\'');
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
