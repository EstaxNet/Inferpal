using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.Execution;

/// <summary>
/// The narrow rule an approval card offers beside "Always this session": the commands that start like this one, the
/// writes under this folder — never the whole tool. Written to the machine's permission rules when the person picks it,
/// read back by <see cref="PermissionPolicy"/> like any rule they wrote.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ A proposal never matches more than one command: its program and subcommands as written, then arguments that keep it
/// one command (no chain, pipe, substitution or redirection). The policy judges a command line whole AND command by
/// command, so a chain that leaves the prefix is prompted. ⚠ The arguments are not read: an option that writes elsewhere
/// or runs a program (<c>git diff --output</c>, <c>dotnet build -o</c>) is covered like any other — the card says
/// "commands that start with", and the docs say so.
/// </para>
/// <para>
/// None is offered where a rule would grant more than it reads: an opaque execution or a call the prompt is forced for
/// (the caller does not ask), a first word that is not a program name (a quoted path, <c>FOO=1</c>), a launcher that runs
/// any code it is given (<c>python script.py</c>, <c>python3.12 x.py</c>, <c>bash x.sh</c>, <c>uv run x</c>,
/// <c>npm exec x</c>), a program or subcommand that deletes, overwrites, installs or publishes what another machine serves,
/// or changes what later commands run (<c>git config</c>), a subcommand whose own subcommands the arguments would choose
/// (<c>git stash</c> covers <c>git stash drop</c>), a file at the workspace root or under a folder whose name starts with a
/// dot (<c>.inferpal/</c>, <c>.git/</c>, <c>.github/</c>: tool settings, hooks, workflows). The lists below fail on the
/// safe side: a program missing from them gets a proposal the card spells out before the click.
/// </para>
/// </remarks>
internal static class ApprovalRuleProposal
{
    /// <summary>A rule offered on a card.</summary>
    /// <param name="Tool">The tool the rule names.</param>
    /// <param name="Pattern">The regex the rule matches the call's subject with.</param>
    /// <param name="Shown">What it covers, as the card names it: <c>dotnet test</c>, <c>tests/</c>.</param>
    /// <param name="IsFolder">A folder of writes rather than a command prefix.</param>
    /// <param name="Folder">The folder a write rule covers, absolute; <c>null</c> for a command.</param>
    internal sealed record Proposal(string Tool, string Pattern, string Shown, bool IsFolder, string? Folder = null)
    {
        /// <summary>The rule as the setting holds it: one line for a command, one per write tool for a folder — "writes
        /// under tests/" covers a file written whole, a diff and a batch alike.</summary>
        public IReadOnlyList<string> Lines => IsFolder
            ? [.. WriteTools.Order(StringComparer.Ordinal).Select(t => $"allow {t} {Pattern}")]
            : [$"allow {Tool} {Pattern}"];

        /// <summary>The rule as one line of text — what the card's tooltip and the history name.</summary>
        public string Key => IsFolder ? $"allow (writes) {Pattern}" : Lines[0];

        /// <summary>What the rule allows, for the person: "commands that start with `dotnet test`".</summary>
        public string Describe => IsFolder ? Strings.ApprovalRuleWrites(Shown) : Strings.ApprovalRuleCommands(Shown);
    }

    /// <summary>The tools whose calls a narrow rule can describe.</summary>
    private static readonly HashSet<string> WriteTools = new(StringComparer.Ordinal) { "write_file", "apply_diff", "apply_edits" };

    /// <summary>What may follow the prefix: arguments that stay inside the one command.</summary>
    /// <remarks>⚠ Horizontal whitespace only, never <c>\s</c>: <c>\s</c> crosses a line break, and the rule would match a
    /// second command on the next line — the policy judging each line too must not be what holds the rule.</remarks>
    private const string Arguments = @"([ \t]+[^;&|`$<>(){}\r\n\v\f\u0085\u2028\u2029]*)?[ \t]*$";

    /// <summary>A command with any of these is not one plain command.</summary>
    internal static readonly char[] Metacharacters =
        [';', '&', '|', '`', '$', '<', '>', '(', ')', '{', '}', '\r', '\n', '\v', '\f', '\u0085', '\u2028', '\u2029'];

    /// <summary>Programs that run whatever code they are given — a script, an option (<c>find -exec</c>): no rule on their
    /// name alone.</summary>
    private static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "python", "python3", "py", "pythonw", "node", "deno", "bun", "ruby", "perl", "php", "lua", "java", "bash", "sh", "zsh",
        "fish", "dash", "ksh", "pwsh", "powershell", "cmd", "wsl", "npx", "pnpx", "bunx", "uvx", "env", "sudo", "doas", "su",
        "xargs", "nohup", "time", "timeout", "watch", "nice", "start", "call", "exec", "source", "eval", "osascript",
        "cscript", "wscript", "mshta", "rundll32", "regsvr32", "invoke-expression", "iex", "invoke-command", "icm", "find",
        "awk", "gawk", "sed", "vim", "vi", "nvim", "emacs", "open", "xdg-open", "explorer", "invoke-item", "ii",
        "start-process", "saps",
        // Modules and packages that run the file or the code they are given: `python -m pdb x.py`, `npx tsx x.ts`.
        "runpy", "pdb", "trace", "cprofile", "profile", "timeit", "code", "tsx", "ts-node", "zx", "vite-node", "esno", "jiti",
        // And those that serve the folder to the network.
        "http.server", "simplehttpserver", "serve", "http-server",
        // And the interpreters and wrappers that run the command or the script after them.
        "nodejs", "ipython", "julia", "rscript", "groovy", "kotlin", "scala", "swift", "elixir", "escript", "tclsh", "csi",
        "cross-env", "concurrently", "nodemon", "dotenv", "env-cmd",
    };

    /// <summary>A versioned interpreter is that interpreter: <c>python3.12</c>, <c>node22</c>, <c>ruby3.2</c>.</summary>
    private static readonly Regex Versioned = new(@"^(?<name>[A-Za-z][A-Za-z-]*?)-?[0-9][0-9.]*$", RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>Subcommands whose own subcommands the arguments would choose (<c>git stash drop</c>,
    /// <c>dotnet nuget push</c>, <c>gh repo delete</c>, <c>docker image rm</c>): a rule ending on one of them is not offered.</summary>
    private static readonly HashSet<string> GroupVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "stash", "branch", "remote", "tag", "worktree", "submodule", "notes", "reflog", "lfs", "bisect", "nuget", "tool",
        "workload", "cache", "repo", "sln", "ef", "migrations", "database", "secret", "secrets", "user-secrets", "gist",
        "release", "pr", "issue", "auth", "container", "image", "images", "volume", "network", "system", "compose",
        "context", "locals", "dev-certs", "package", "reference", "env", "codespace", "extension", "registry", "project",
        "workflow", "run", "group", "vm", "storage", "webapp", "functionapp", "keyvault", "deployment", "s3", "ec2", "iam",
        "lambda", "compute", "pod", "pods", "service", "namespace", "fsi", "node", "script", "eval", "shell", "repl",
    };

    /// <summary>Programs whose <c>run</c> runs whatever command or file follows it (<c>uv run x.py</c>,
    /// <c>docker run image</c>, <c>go run main.go</c>) — where <c>npm run build</c> names a script of the project.</summary>
    private static readonly HashSet<string> RunLaunchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "uv", "poetry", "pipenv", "pdm", "hatch", "conda", "mamba", "pipx", "docker", "podman", "kubectl", "go",
    };

    /// <summary>Programs that need a subcommand for a rule to say what they do (<c>dotnet test</c>, never <c>dotnet</c>).</summary>
    private static readonly HashSet<string> NeedVerb = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "git", "npm", "pnpm", "yarn", "cargo", "go", "docker", "podman", "kubectl", "gh", "az", "aws", "gcloud",
        "mvn", "gradle", "uv", "pip", "pip3", "conda", "brew", "winget", "choco", "apt", "apt-get", "dnf", "yum", "make",
    };

    /// <summary>Programs that delete, overwrite or reach another machine: no rule.</summary>
    private static readonly HashSet<string> Destructive = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "rmdir", "del", "erase", "rd", "mv", "move", "ren", "rename", "dd", "shred", "truncate", "mkfs", "format",
        "rimraf", "del-cli", "trash", "shx",
        "chmod", "chown", "icacls", "takeown", "attrib", "kill", "pkill", "killall", "taskkill", "shutdown", "reboot", "reg",
        "diskpart", "remove-item", "ri", "move-item", "mi", "clear-content", "set-content", "stop-process", "cipher",
        "curl", "wget", "invoke-webrequest", "iwr", "invoke-restmethod", "irm", "ssh", "scp", "sftp", "rsync", "ftp", "nc",
        "ncat", "telnet", "cp", "copy", "xcopy", "robocopy", "copy-item", "cpi", "new-item", "ni", "out-file",
        "add-content", "ac", "tee", "tee-object", "rename-item", "rni", "set-itemproperty", "sp", "ln", "tar", "unzip",
        "7z", "expand-archive", "terraform",
    };

    /// <summary>Subcommands that rewrite history, throw work away, delete, install or publish what another machine
    /// serves, run what they are given, or change what later commands run: no rule (<c>git push</c>, <c>git checkout</c>,
    /// <c>npm install</c>, <c>npm exec</c>, <c>git config</c>…).</summary>
    private static readonly HashSet<string> DestructiveVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "push", "reset", "clean", "rebase", "filter-branch", "filter-repo", "gc", "prune", "rm", "uninstall", "unpublish",
        "checkout", "restore", "drop", "clear", "delete", "remove", "del", "destroy", "kill", "install", "i", "ci", "update",
        "upgrade", "up", "publish", "deploy", "login", "exec", "dlx", "x", "config", "apply",
    };

    private static readonly Regex Verb = new(@"^[A-Za-z][A-Za-z0-9_:-]*$", RegexOptions.CultureInvariant, RegexBudget.Default);
    private static readonly Regex Module = new(@"^[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.CultureInvariant, RegexBudget.Default);
    private static readonly Regex Package = new(@"^@?[A-Za-z0-9][A-Za-z0-9._/-]*(@[A-Za-z0-9._-]+)?$", RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>
    /// The narrow rule for this call, or <c>null</c> when none describes it without granting more.
    /// </summary>
    /// <param name="subject">The text the rules match: the command, or the path(s) one per line.</param>
    /// <param name="root">The workspace root: a file at its top, or outside it, gets no folder rule.</param>
    public static Proposal? For(string toolName, string? subject, string? root)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        if (toolName == "run_command") return ForCommand(subject, root);
        if (WriteTools.Contains(toolName)) return ForWrites(toolName, subject, root);
        return null;
    }

    /// <summary>A first word that names a program: a path or a name, never a quoted path cut at its space, an assignment
    /// (<c>FOO=1 python x.py</c>) or an option.</summary>
    private static readonly Regex ProgramToken = new(@"^[A-Za-z0-9_.~/\\:+-]+$", RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>The program without its version, lower case: <c>Python3.12</c> is <c>python</c>.</summary>
    private static string BaseName(string program) =>
        (Versioned.Match(program) is { Success: true } m ? m.Groups["name"].Value : program).ToLowerInvariant();

    /// <summary>Whether <paramref name="program"/> runs what it is given — as named, or without its version.</summary>
    private static bool IsLauncher(string program) => Launchers.Contains(program) || Launchers.Contains(BaseName(program));

    /// <summary>Whether the first word names a file the agent can rewrite: a relative path (resolved in the working
    /// directory, the workspace's), or an absolute one under the workspace — never a name looked up on the PATH, never a
    /// system program named by its absolute path (<c>/usr/bin/dotnet</c>).</summary>
    private static bool IsRewritable(string program, string? root)
    {
        if (program.IndexOfAny(['/', '\\']) < 0) return false;
        if (!Path.IsPathRooted(program) || program.StartsWith('~') || string.IsNullOrEmpty(root)) return true;
        try { return Tools.PathSanitizer.IsUnderRoot(Path.GetFullPath(program), Path.GetFullPath(root)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
    }

    private static Proposal? ForCommand(string command, string? root)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0 || trimmed.IndexOfAny(Metacharacters) >= 0) return null;
        if (PermissionPolicy.IsOpaqueExecution(trimmed)) return null;

        var tokens = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (!ProgramToken.IsMatch(tokens[0]) || tokens[0].StartsWith('-')) return null;
        // ⚠ A program the workspace holds (`./build.sh`, `scripts\run.ps1`, `bin/Debug/tool.exe`) is a file the agent can
        // rewrite: a rule for it approves in advance whatever that file will hold.
        if (IsRewritable(tokens[0], root)) return null;
        var program = ProgramName(tokens[0]);
        if (Destructive.Contains(program)) return null;

        var prefix = new List<string> { tokens[0] };
        var next = 1;
        if (IsLauncher(program))
        {
            // The module or the package says what runs: `python -m pytest`, `npx eslint`. Anything else is any code.
            if (BaseName(program) is "python" or "py" && tokens.Length >= 3 && tokens[1] == "-m" && Module.IsMatch(tokens[2]))
            {
                prefix.AddRange([tokens[1], tokens[2]]);
                program = tokens[2];
                next = 3;
            }
            else if (program is "npx" or "pnpx" or "bunx" or "uvx" && tokens.Length >= 2 && !tokens[1].StartsWith('-')
                     && Package.IsMatch(tokens[1]))
            {
                prefix.Add(tokens[1]);
                program = ProgramName(tokens[1]);
                next = 2;
            }
            else
                return null;
            if (Destructive.Contains(program) || IsLauncher(program)) return null;
        }

        // ⚠ The subcommands are read after the module or the package too: `python -m pip install` installs whatever
        // follows, `npx prisma migrate reset` empties a database.
        var start = prefix.Count;
        for (var i = next; i < tokens.Length && prefix.Count < start + 2 && Verb.IsMatch(tokens[i]); i++)
            prefix.Add(tokens[i]);
        var verbs = prefix.Skip(start).ToList();
        if (verbs.Count == 0 && NeedVerb.Contains(program)) return null;
        if (verbs.Any(DestructiveVerbs.Contains)) return null;
        if (verbs.Count > 0 && RunLaunchers.Contains(program) && verbs[0].Equals("run", StringComparison.OrdinalIgnoreCase))
            return null;
        // A rule that ends on a group of subcommands would let the arguments pick one: `git stash` → `git stash drop`.
        if (verbs.Count > 0 && GroupVerbs.Contains(verbs[^1])) return null;

        var pattern = "^[ \\t]*" + string.Join("[ \\t]+", prefix.Select(Regex.Escape)) + Arguments;
        return new Proposal("run_command", pattern, string.Join(' ', prefix), IsFolder: false);
    }

    /// <summary>The program a command starts with, as the lists name it: <c>/usr/bin/git</c> and <c>git.exe</c> are git.</summary>
    private static string ProgramName(string token)
    {
        var name = token.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        foreach (var ext in (string[])[".exe", ".cmd", ".bat", ".ps1"])
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length];
        return name;
    }

    private static Proposal? ForWrites(string toolName, string subject, string? root)
    {
        if (string.IsNullOrEmpty(root)) return null;
        string fullRoot;
        try { fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

        string? folder = null;
        foreach (var line in subject.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string full;
            try { full = Path.GetFullPath(line); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
            // The rule matches the path as the tool names it: one written another way (relative, with `..`) is not the
            // folder the card would name, and is never resolved against the process's directory.
            if (!Path.IsPathRooted(line) || !PathComparer.Default.Equals(full, line)) return null;
            var parent = Path.GetDirectoryName(full);
            if (parent is null) return null;
            if (folder is null) folder = parent;
            else if (!PathComparer.Default.Equals(folder, parent)) return null;   // several folders: no single one to name
        }
        if (folder is null || PathComparer.SameDirectory(folder, fullRoot)
            || !Tools.PathSanitizer.IsUnderRoot(folder, fullRoot)) return null;
        var relative = Path.GetRelativePath(fullRoot, folder).Replace('\\', '/');
        // ⚠ A folder whose name starts with a dot holds tool settings, hooks or workflows — what later commands run, or
        // the agent's own instructions (`.inferpal/`, `.git/hooks/`, `.github/workflows/`, `.vscode/`): any case, any depth.
        if (relative.Split('/').Any(segment => segment.StartsWith('.'))) return null;
        // A folder that is a link (or under one) inside the workspace writes elsewhere than the card says: no rule. The
        // root is resolved too, so a link above the workspace (macOS /var) changes nothing.
        var resolved = Path.GetRelativePath(Tools.PathSanitizer.Resolved(fullRoot), Tools.PathSanitizer.Resolved(folder));
        if (!string.Equals(resolved.Replace('\\', '/'), relative, PathComparer.Comparison)) return null;

        // ⚠ The rules are matched ignoring case: on a file system that does not fold case (Linux), `Tests/` is another
        // folder than the one the card named, so the folder keeps its case there.
        var escaped = Regex.Escape(Path.TrimEndingDirectorySeparator(folder));
        if (PathComparer.Comparison == StringComparison.Ordinal) escaped = "(?-i:" + escaped + ")";
        // ⚠ No `..` after the folder: a subject that climbs out of it is not under it. ⚠ And no name starting with a dot
        // at any depth below it: the folder proposed is never a dot folder, and its rule must not reach one either
        // (`src/.husky/pre-commit`, `src/.vscode/tasks.json`, the hooks of a repository nested under it).
        var pattern = "^" + escaped + @"[\\/](?!\.)(?![^\r\n]*[\\/]\.)(?![^\r\n]*\.\.)[^\r\n]+$";
        return new Proposal(toolName, pattern, relative + "/", IsFolder: true, Folder: Path.TrimEndingDirectorySeparator(folder));
    }
}
