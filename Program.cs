using System.Text;
using System.Text.RegularExpressions;

const string Usage = """
    Rename folders and/or files in a directory (top level only) with a regex.

    Usage: RegexFileName directory pattern [replacement] [options]

    Every match of PATTERN (case-insensitive unless --case-sensitive) is replaced
    with REPLACEMENT, using .NET Regex.Replace syntax: $1 or ${name} insert groups,
    $$ is a literal $. Leave REPLACEMENT out (or pass "") to delete the match.
    Names with no match are left alone. For files only the part before the
    extension is matched, so "a Notes.pdf" becomes "a Draft.pdf"; use --whole-name
    to match the extension too.

    If a new name is already taken (by an existing entry or by another rename in
    the same run), " (2)", " (3)", ... is added before the extension, the way
    Windows does.

    Options:
      --apply           rename for real (otherwise dry run)
      --folders         rename folders only (default)
      --files           rename files only
      --both            rename folders and files
      --case-sensitive  match case exactly
      --whole-name      for files, match against the full name including the extension
      -h, --help        show this help

    Examples:
      RegexFileName "C:\dir" " Notes$" " Draft"                     dry run, folders
      RegexFileName "C:\dir" " Notes$" " Draft" --apply             replace suffix on folders
      RegexFileName "C:\dir" " - Shortcut$" --files --apply         remove suffix from files
      RegexFileName "C:\dir" "(?<! Notes)$" " Notes" --both         add suffix where missing
      RegexFileName "C:\dir" "^(\d+)_(.*)" "$2 ($1)" --files        "12_Report" -> "Report (12)"
      RegexFileName "C:\dir" "\.jpeg$" ".jpg" --files --whole-name  change extension

    """;

// Characters Windows doesn't allow in file or folder names.
char[] invalidChars = Path.GetInvalidFileNameChars();

Console.OutputEncoding = Encoding.UTF8;
return Run(args);

int Run(string[] args)
{
    bool apply = false, caseSensitive = false, wholeName = false;
    string? target = null;
    var positionals = new List<string>();

    // Anything that isn't one of these exact flags is positional, so a pattern or
    // replacement may start with "-" (e.g. "-\d+$").
    foreach (var arg in args)
    {
        switch (arg)
        {
            case "-h" or "--help":
                Console.Write(Usage);
                return 0;
            case "--apply": apply = true; break;
            case "--case-sensitive": caseSensitive = true; break;
            case "--whole-name": wholeName = true; break;
            case "--folders" or "--files" or "--both":
                if (target is not null && target != arg[2..])
                    return UsageError($"{arg} can't be combined with --{target}.");
                target = arg[2..];
                break;
            default: positionals.Add(arg); break;
        }
    }
    target ??= "folders";
    bool doFolders = target is "folders" or "both";
    bool doFiles = target is "files" or "both";

    // On Windows, a path ending in \" swallows the closing quote and merges the next
    // arguments into it: "C:\dir\" " Notes" arrives as one argument, C:\dir" Notes.
    if (positionals.Count > 0 && positionals[0].Contains('"'))
        return UsageError(
            $"DIRECTORY contains a quote: {Quote(positionals[0])}. "
            + "Remove the trailing backslash before the closing quote (use \"C:\\dir\", not \"C:\\dir\\\").");

    // The replacement is optional because Windows PowerShell 5.1 drops "" arguments,
    // so an empty replacement may arrive as no argument at all.
    if (positionals.Count < 2)
        return UsageError("DIRECTORY and PATTERN are required.");
    if (positionals.Count > 3)
        return UsageError($"too many arguments: {string.Join(" ", positionals.Skip(3).Select(Quote))}");
    string rawDir = positionals[0].Trim();
    string pattern = positionals[1];
    string replacement = positionals.Count == 3 ? positionals[2] : "";

    // An empty path would silently mean the current directory, so refuse it.
    if (rawDir.Length == 0)
        return UsageError("DIRECTORY is empty; give the full path of the folder to process.");

    Regex regex;
    try
    {
        var options = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        regex = new Regex(pattern, options);
    }
    catch (ArgumentException e)
    {
        return UsageError($"PATTERN is not a valid regular expression: {e.Message}");
    }

    if (File.Exists(rawDir))
        return UsageError($"DIRECTORY is a file, not a folder: {rawDir}");
    if (!Directory.Exists(rawDir))
        return UsageError($"DIRECTORY does not exist: {rawDir}");
    var root = new DirectoryInfo(Path.GetFullPath(rawDir));
    Console.WriteLine($"Directory: {root.FullName}\n");

    var allEntries = root.EnumerateFileSystemInfos()
        .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    var entries = allEntries
        .Where(e => e is DirectoryInfo ? doFolders : doFiles)
        .ToList();
    int skipped = 0;
    var planned = new List<(FileSystemInfo Entry, string NewName)>();

    // Work out every new name first, so collisions can be resolved across the whole batch.
    foreach (var entry in entries)
    {
        string name = entry.Name;
        bool isFile = entry is FileInfo;
        // Files keep their extension; the pattern is matched on the part before it.
        var (baseName, ext) = isFile && !wholeName ? SplitName(name, isFile) : (name, "");

        if (!regex.IsMatch(baseName))
        {
            skipped++;
            continue;
        }
        string newBase = regex.Replace(baseName, replacement);
        string newName = newBase + ext;
        if (newName == name)
        {
            skipped++;
            continue;
        }
        if (newBase.Trim().Length == 0)
        {
            Console.WriteLine($"SKIP (name would be empty): {Quote(name)}");
            skipped++;
            continue;
        }
        if (newName.IndexOfAny(invalidChars) >= 0 || newName.EndsWith('.') || newName.EndsWith(' '))
        {
            Console.WriteLine($"SKIP (invalid name): {Quote(name)} -> {Quote(newName)}");
            skipped++;
            continue;
        }
        planned.Add((entry, newName));
    }

    // Names that stay put are taken; names of entries being renamed are freed.
    var renaming = planned.Select(p => p.Entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var taken = allEntries
        .Select(e => e.Name)
        .Where(n => !renaming.Contains(n))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var renames = new List<(string Src, string Dst)>();
    int collided = 0;

    foreach (var (entry, newName) in planned)
    {
        string finalName = UniqueName(newName, entry is FileInfo, taken);
        taken.Add(finalName);
        string note = "";
        if (finalName != newName)
        {
            collided++;
            note = $"  ({Quote(newName)} is taken)";
        }
        Console.WriteLine($"{(apply ? "RENAME" : "WOULD RENAME")}: {Quote(entry.Name)} -> {Quote(finalName)}{note}");
        renames.Add((entry.FullName, Path.Combine(root.FullName, finalName)));
    }

    int failed = apply ? Execute(renames) : 0;

    string verb = apply ? "Renamed" : "Would rename";
    string kind = target == "both" ? "folders and files" : target;
    Console.WriteLine(
        $"\n{verb} {renames.Count - failed} {kind} ({Quote(pattern)} -> {Quote(replacement)}), "
        + $"skipped {skipped}, numbered to avoid collisions {collided}"
        + (failed > 0 ? $", failed {failed}." : "."));
    if (!apply && renames.Count > 0)
        Console.WriteLine("Re-run with --apply to make the changes.");
    return failed > 0 ? 1 : 0;
}

// Perform the renames, ordering them so chains (A->B, B->C) and swaps work.
// Returns how many failed.
static int Execute(List<(string Src, string Dst)> renames)
{
    var pending = new List<(string Src, string Dst)>(renames);
    int failed = 0;

    bool TryMove(string src, string dst, string shownDst)
    {
        try
        {
            if (Directory.Exists(src))
                Directory.Move(src, dst);
            else
                File.Move(src, dst);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"FAILED: {Quote(Path.GetFileName(src))} -> {Quote(Path.GetFileName(shownDst))}: {e.Message}");
            failed++;
            return false;
        }
    }

    while (pending.Count > 0)
    {
        // A case-only change points at the same entry on Windows, so it's free to go.
        var ready = pending
            .Where(r => !Path.Exists(r.Dst) || string.Equals(r.Src, r.Dst, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var r in ready)
        {
            pending.Remove(r);
            TryMove(r.Src, r.Dst, r.Dst);
        }
        if (ready.Count == 0)
        {
            // Every remaining target is held by another pending entry (a cycle):
            // move one out of the way under a temporary name to break it.
            var (src, dst) = pending[0];
            pending.RemoveAt(0);
            int n = 1;
            string tmp;
            while (Path.Exists(tmp = $"{src}.renaming{n}"))
                n++;
            if (TryMove(src, tmp, dst))
                pending.Add((tmp, dst));
        }
    }
    return failed;
}

// Split NAME into (base, extension); folders and names like ".env" have no extension.
static (string Base, string Ext) SplitName(string name, bool isFile)
{
    string ext = isFile ? Path.GetExtension(name) : "";
    if (ext.Length == name.Length)
        ext = "";
    return (name[..^ext.Length], ext);
}

// Return NAME, or NAME with " (2)", " (3)", ... before the extension, whichever is free.
static string UniqueName(string name, bool isFile, HashSet<string> taken)
{
    if (!taken.Contains(name))
        return name;
    var (baseName, ext) = SplitName(name, isFile);
    int n = 2;
    while (taken.Contains($"{baseName} ({n}){ext}"))
        n++;
    return $"{baseName} ({n}){ext}";
}

static string Quote(string s) => $"'{s}'";

static int UsageError(string message)
{
    Console.Error.WriteLine("Usage: RegexFileName directory pattern [replacement] [--apply] [--folders | --files | --both]");
    Console.Error.WriteLine("                     [--case-sensitive] [--whole-name]       (--help for details)");
    Console.Error.WriteLine($"error: {message}");
    return 2;
}
