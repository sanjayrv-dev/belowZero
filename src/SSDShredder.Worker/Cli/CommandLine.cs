using SSDShredder.Worker.Protocol;

namespace SSDShredder.Worker.Cli;

public enum Verb
{
    Inspect,
    Shred,
    VaultInit,
    VaultStatus,
    VaultClose,

    /// <summary>Internal launcher form: --ipc &lt;suffix&gt; --parent-pid &lt;pid&gt;.</summary>
    Ipc,
}

public sealed record ParsedCommand(
    Verb Verb,
    string? Path = null,
    string? VaultId = null,
    bool Confirm = false,
    string? PipeSuffix = null,
    uint ParentPid = 0);

/// <summary>Exact-arity parser for the published command grammar. Anything else is invalid.</summary>
public static class CommandLine
{
    public const string Usage =
        "Usage:\n" +
        "  SSDShredder.Worker --inspect <absolute-local-file>\n" +
        "  SSDShredder.Worker --shred <absolute-local-file> [--confirm]\n" +
        "  SSDShredder.Worker --vault-init\n" +
        "  SSDShredder.Worker --vault-status <vault-uuid>\n" +
        "  SSDShredder.Worker --vault-close <vault-uuid> [--confirm]";

    public static bool TryParse(IReadOnlyList<string> args, out ParsedCommand? command, out string error)
    {
        command = null;
        error = "";
        if (args.Count == 0)
        {
            error = "missing command";
            return false;
        }

        switch (args[0])
        {
            case "--inspect":
                if (args.Count != 2) return Fail("--inspect takes exactly one path", out error);
                if (!Validation.IsAbsoluteLocalFilePath(args[1])) return Fail("path must be an absolute local file path", out error);
                command = new ParsedCommand(Verb.Inspect, Path: args[1]);
                return true;

            case "--shred":
                if (!TryConfirmTail(args, out bool shredConfirm)) return Fail("--shred takes one path and an optional --confirm", out error);
                if (!Validation.IsAbsoluteLocalFilePath(args[1])) return Fail("path must be an absolute local file path", out error);
                command = new ParsedCommand(Verb.Shred, Path: args[1], Confirm: shredConfirm);
                return true;

            case "--vault-init":
                if (args.Count != 1) return Fail("--vault-init takes no arguments", out error);
                command = new ParsedCommand(Verb.VaultInit);
                return true;

            case "--vault-status":
                if (args.Count != 2) return Fail("--vault-status takes exactly one vault UUID", out error);
                if (!TryVaultId(args[1], out string statusId)) return Fail("vault id must be a UUID", out error);
                command = new ParsedCommand(Verb.VaultStatus, VaultId: statusId);
                return true;

            case "--vault-close":
                if (!TryConfirmTail(args, out bool closeConfirm)) return Fail("--vault-close takes one vault UUID and an optional --confirm", out error);
                if (!TryVaultId(args[1], out string closeId)) return Fail("vault id must be a UUID", out error);
                command = new ParsedCommand(Verb.VaultClose, VaultId: closeId, Confirm: closeConfirm);
                return true;

            case "--ipc":
                if (args.Count != 4 || args[2] != "--parent-pid") return Fail("malformed internal invocation", out error);
                if (!Validation.IsLowerHex32(args[1])) return Fail("malformed internal invocation", out error);
                if (!TryPid(args[3], out uint pid)) return Fail("malformed internal invocation", out error);
                command = new ParsedCommand(Verb.Ipc, PipeSuffix: args[1], ParentPid: pid);
                return true;

            default:
                return Fail("unknown command", out error);
        }
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static bool TryConfirmTail(IReadOnlyList<string> args, out bool confirm)
    {
        confirm = false;
        if (args.Count == 2) return true;
        if (args.Count == 3 && args[2] == "--confirm")
        {
            confirm = true;
            return true;
        }
        return false;
    }

    private static bool TryVaultId(string s, out string id)
    {
        id = s.ToLowerInvariant();
        return Validation.IsCanonicalUuid(id);
    }

    private static bool TryPid(string s, out uint pid)
    {
        pid = 0;
        if (s.Length is 0 or > 10) return false;
        foreach (char c in s)
        {
            if (c is < '0' or > '9') return false;
        }
        return uint.TryParse(s, out pid) && pid != 0;
    }
}
