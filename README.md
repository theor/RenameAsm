# RenameAsm

Renames a .net assembly/module/namespaces:
- Will change the file name, but also the actual .net assembly name and the matching module name
- Will also rename all namespaces

Usage: `RenameAsm.exe <dll to rename> <new name>`

Example: `RenameAsm C:\my.dll newname` will output `newname.dll`

That requires the dll name and its namespaces to match in the first place: if you have a dll/assembly named `AB` but its namespace is `A.B`, it won't match. in that case, either call the tool twice (rename AB to A.B first, then A.B to C.D) or just add a `.Replace("AB", "A.B")` line in Program.cs.

## How it works

- `ildasm /output:pre.il my.dll` disassembles the dll and write the il to a pre.il file
- text replace `my` with `newname` and write that to `post.il`
- `ilasm /dll /out:newname.dll post.il`

## Embedded resources

if reassembling fails because embedded resources cannot be found: the disassembling has extracted those resources to the disk (eg. `my.someresource.resource`). rename these files by hand (`newname.someresource.resource`), then re-run the tool
