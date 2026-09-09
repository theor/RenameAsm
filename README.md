# RenameAsm

Renames .NET assemblies: file name, assembly name, module name and namespaces.
Give it several assemblies at once and the references between them are rewritten too.

```
RenameAsm <assembly> <newname> [options]
RenameAsm --prefix <prefix> <assembly>... [options]

  --out <dir>          output directory (default: current directory)
  --also <old>=<new>   rename an extra namespace or type; may be repeated
  --strip-key          remove the strong-name public key (see below)
```

Examples:

```
RenameAsm my.dll newname                       # writes ./newname.dll
RenameAsm --prefix UnityPipeline. --out out/ Plugins/CodeAnalysis/*.dll
# writes out/UnityPipeline.Microsoft.CodeAnalysis.dll, out/UnityPipeline.Microsoft.CodeAnalysis.CSharp.dll, ...
# and UnityPipeline.Microsoft.CodeAnalysis.CSharp now references UnityPipeline.Microsoft.CodeAnalysis
```

Runs on macOS, Linux and Windows. Needs the .NET 10 SDK; `dotnet build` pulls the
matching `ilasm`/`ildasm` from NuGet for the host platform.

## How it works

For each input assembly:

1. `ildasm` disassembles it to a temp directory. Custom attributes are emitted in verbal
   form so strings inside them (`AssemblyTitle`, `InternalsVisibleTo`, ...) get renamed too.
2. Every old name is replaced with its new name. The match is bounded, so `Foo` is renamed
   in `Foo.Bar` and `[Foo]Foo.Baz` but not in `MyFoo` or `Foo2`. All inputs are renamed in
   one pass, longest name first, so `Microsoft.CodeAnalysis.CSharp` and
   `Microsoft.CodeAnalysis` do not step on each other.
3. Embedded resources extracted by `ildasm` are renamed on disk to match.
4. `ilasm` reassembles into the output directory.
5. The result is read back: the assembly name must be the new one, and no reference to
   another input may still use an old name.

Any step failing makes the tool exit non-zero and keeps the temp directory for inspection.

Only names that start with the assembly name are renamed. A public type in another namespace
(`System.Reflection.PortableExecutable.PEReader` in `System.Reflection.Metadata.dll`, say) keeps
its full name, and collides with the original if both get loaded. The tool warns about every
such type. Add `--also <namespace>=<new>` or `--also <full type name>=<new>` to rename them too:

```
RenameAsm --prefix UnityPipeline. System.Reflection.Metadata.dll \
  --also System.Reflection.PortableExecutable=UnityPipeline.System.Reflection.PortableExecutable \
  --also System.Reflection.AssemblyFlags=UnityPipeline.System.Reflection.AssemblyFlags
```

The rename uses the assembly name from metadata, not the file name. If the two differ the
tool warns. Namespaces are only renamed where they start with the assembly name, so an
assembly `AB` with namespace `A.B` needs two runs (`AB` to `A.B`, then `A.B` to `C.D`).

## Picking a prefix

Pick a prefix whose first segment is not a namespace your own code lives in. With `Unity.` as
the prefix, `System.Reflection.Metadata` becomes `Unity.System.Reflection.Metadata`, and any
`System.Type` written inside `namespace Unity.Pipeline` then resolves to the new `Unity.System`
namespace, because C# looks names up outward through the enclosing namespaces before the global
one. A single-segment prefix such as `UnityPipeline.` avoids this.

## Strong names

The signature cannot survive the rename, so by default the output keeps its public key but has no
valid signature. Mono, Unity and .NET Core do not verify signatures, so this usually works.

`--strip-key` makes the outputs plain unsigned assemblies instead. It removes the public key from
each input, the public key token from references between the inputs, the `PublicKey=` part of
every `InternalsVisibleTo` entry (a friend without a key can no longer match one), and the
StrongNameSigned bit in the CLR header. The read-back check fails if any of these remain.

## Limitations

- Win32 resources (file version info) are lost. The .NET ildasm/ilasm cannot round-trip them.
- A short assembly name such as `A` will also match single-letter identifiers and string
  literals. Check the output.
