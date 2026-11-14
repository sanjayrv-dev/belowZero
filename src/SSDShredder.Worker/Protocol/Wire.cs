namespace SSDShredder.Worker.Protocol;

/// <summary>Process exit codes and terminal result codes of wire contract v1.</summary>
public enum ExitCode
{
    Completed = 0,
    Invalid = 2,
    Cancelled = 3,
    Unsupported = 4,
    Denied = 5,
    Busy = 6,
    Failed = 7,
    Indeterminate = 8,
    Recovery = 9,
}

public static class Wire
{
    public const int Version = 1;

    /// <summary>Maximum frame size in UTF-8 bytes, excluding the LF terminator.</summary>
    public const int MaxFrameBytes = 65536;

    public static string StateName(ExitCode code) => code switch
    {
        ExitCode.Completed => "completed",
        ExitCode.Invalid => "invalid",
        ExitCode.Cancelled => "cancelled",
        ExitCode.Unsupported => "unsupported",
        ExitCode.Denied => "denied",
        ExitCode.Busy => "busy",
        ExitCode.Failed => "failed",
        ExitCode.Indeterminate => "indeterminate",
        ExitCode.Recovery => "recovery",
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };
}

public static class Ops
{
    public const string Inspect = "inspect";
    public const string Shred = "shred";
    public const string VaultInit = "vault-init";
    public const string VaultStatus = "vault-status";
    public const string VaultClose = "vault-close";
    public const string ProbeVolume = "probe-volume";

    public static bool IsFileOp(string op) => op is Inspect or Shred;

    public static bool IsPrivilegedOp(string op) => op is VaultInit or VaultStatus or VaultClose or ProbeVolume;

    public static bool IsKnown(string op) => IsFileOp(op) || IsPrivilegedOp(op);
}

public static class Phases
{
    public const string Inspecting = "inspecting";
    public const string Confirming = "confirming";
    public const string Trimming = "trimming";
    public const string Unlinking = "unlinking";
    public const string Creating = "creating";
    public const string Encrypting = "encrypting";
    public const string Locking = "locking";
    public const string Reconciling = "reconciling";
}

/// <summary>Vault phases reported in vault evidence.</summary>
public static class VaultPhases
{
    public const string Creating = "creating";
    public const string Ready = "ready";
    public const string Closing = "closing";
    public const string Retired = "retired";
    public const string Orphan = "orphan";
    public const string Unknown = "unknown";
}
