# RegexFileName

A Windows command-line tool that renames the files and/or folders in a directory by replacing regex matches in their names.

- **Dry run by default.** It shows what would be renamed; add `--apply` to do it.
- **No name collisions.** If a new name is already taken, it adds ` (2)`, ` (3)`, … before the extension, the way Windows does.
- **Safe batch renames.** All new names are worked out before anything is renamed, so two entries can't end up with the same name, and chains (`A → B` while `B → C`) and swaps are ordered so they work.
- **Extensions are kept.** For files, only the part before the extension is matched, unless you use `--whole-name`.
- **Top level only.** Subfolders are not searched.

## Usage

```
RegexFileName directory pattern [replacement] [options]
```

Every match of `pattern` is replaced with `replacement`. Leave the replacement out (or pass `""`) to delete the match. Names with no match are left alone. Matching is case-insensitive unless you use `--case-sensitive`.

| Option | Effect |
|---|---|
| `--apply` | Rename for real (otherwise dry run) |
| `--folders` | Rename folders only (default) |
| `--files` | Rename files only |
| `--both` | Rename folders and files |
| `--case-sensitive` | Match case exactly |
| `--whole-name` | For files, match against the full name including the extension |
| `-h`, `--help` | Show help |

The replacement uses .NET [`Regex.Replace`](https://learn.microsoft.com/dotnet/standard/base-types/substitutions-in-regular-expressions) syntax: `$1` or `${name}` insert a captured group, and `$$` is a literal `$`.

## Examples

```bat
rem Preview replacing " Notes" with " Draft" at the end of folder names
RegexFileName "C:\dir" " Notes$" " Draft"

rem Do it
RegexFileName "C:\dir" " Notes$" " Draft" --apply

rem Remove " - Shortcut" from the end of file names
RegexFileName "C:\dir" " - Shortcut$" --files --apply

rem Remove a leading "(12)" and any spaces after it
RegexFileName "C:\dir" "^\(\d+\) *" --files --apply

rem Add " Notes" to every folder and file name that doesn't already end with it
RegexFileName "C:\dir" "(?<! Notes)$" " Notes" --both

rem Reorder with groups: "12_Report.docx" -> "Report (12).docx"
RegexFileName "C:\dir" "^(\d+)_(.*)" "$2 ($1)" --files

rem Change an extension
RegexFileName "C:\dir" "\.jpeg$" ".jpg" --files --whole-name
```

Sample output, for a folder containing `a Notes.pdf`, `a Draft.pdf`, `c Notes.txt` and `other.txt`:

```
> RegexFileName "C:\dir" " notes$" " Draft" --files
Directory: C:\dir

WOULD RENAME: 'a Notes.pdf' -> 'a Draft (2).pdf'  ('a Draft.pdf' is taken)
WOULD RENAME: 'c Notes.txt' -> 'c Draft.txt'

Would rename 2 files (' notes$' -> ' Draft'), skipped 2, numbered to avoid collisions 1.
Re-run with --apply to make the changes.
```

### Quoting tips

- **cmd:** double quotes work for everything above. Don't end a quoted folder path with a backslash: `"C:\dir\"` makes Windows treat `\"` as a literal quote. Use `"C:\dir"`. The tool detects this and tells you.
- **PowerShell:** put patterns and replacements containing `$` in single quotes (`'$2 ($1)'`), or PowerShell will treat `$1` as one of its own variables. Windows PowerShell 5.1 also drops empty `""` arguments, which is why the replacement is optional.
- **Batch files:** don't `call` another batch file that passes a regex on, because `call` doubles `^` characters.

### Skipped names

A rename is skipped, with a message, if the new name would be empty, would contain characters Windows doesn't allow (`< > : " / \ | ? *`), or would end in a dot or space. If a rename fails (for example, because a file is open in another program), it prints `FAILED`, carries on with the rest, and exits with code 1.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bat
dotnet build -c Release
```

The `FolderProfile` publish profile builds a self-contained, trimmed, single-file `RegexFileName.exe` (about 13 MB, no .NET install needed to run it) into `D:\Tools\bin`. Change `PublishDir` in [`Properties/PublishProfiles/FolderProfile.pubxml`](Properties/PublishProfiles/FolderProfile.pubxml) to publish somewhere else, then run:

```bat
dotnet publish RegexFileName.csproj -p:PublishProfile=FolderProfile
```

or use **Publish** in Visual Studio.

## License

[MIT](LICENSE)
