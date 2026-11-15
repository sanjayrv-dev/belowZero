using System.Diagnostics;
using System.Text;

namespace SSDShredder.Worker.Tests;

/// <summary>
/// Drives Install/Install-BzoShell.ps1 through Windows PowerShell 5.1 against temp profiles (never the real
/// $PROFILE). The "bzo.exe" under test is a copy of where.exe: it takes arguments and returns a known exit code.
/// </summary>
public sealed class ShellInstallTests : IDisposable
{
    private static readonly string Script = Path.Combine(AppContext.BaseDirectory, "Install", "Install-BzoShell.ps1");
    private static readonly string PowerShell = Path.Combine(
        Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private readonly TempDir _dir = new();
    private readonly string _profile;
    private readonly string _macro;
    private readonly string _exe;

    public ShellInstallTests()
    {
        _profile = Path.Combine(_dir.Path, "profile.ps1");
        _macro = Path.Combine(_dir.Path, "m", "bzo.doskey");
        string installDir = Path.Combine(_dir.Path, "Prog Files \u00fcn\u00ef");
        Directory.CreateDirectory(installDir);
        _exe = Path.Combine(installDir, "bzo.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), _exe);
    }

    public void Dispose() => _dir.Dispose();

    private static async Task<(int Exit, string Output)> RunAsync(params string[] args)
    {
        var psi = new ProcessStartInfo(PowerShell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass" }.Concat(args)) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, output + await err);
    }

    private Task<(int Exit, string Output)> InstallerAsync(string action) =>
        RunAsync("-File", Script, "-Action", action, "-ExePath", _exe, "-ProfilePath", _profile, "-MacroFile", _macro);

    [Fact]
    public async Task Install_Then_Uninstall_Preserves_Existing_Profile_Exactly()
    {
        string original = "# mine\r\nfunction prompt { 'x> ' }\r\n";
        File.WriteAllText(_profile, original, new UTF8Encoding(false));

        Assert.Equal(0, (await InstallerAsync("Install")).Exit);
        string installed = File.ReadAllText(_profile);
        Assert.StartsWith(original, installed);
        Assert.Contains("Set-Alias -Name '/bzo' -Value '" + _exe + "' -Scope Global", installed);
        Assert.True(File.Exists(_macro));

        Assert.Equal(0, (await InstallerAsync("Install")).Exit); // idempotent: one block only
        Assert.Equal(1, File.ReadAllText(_profile).Split("# >>> belowZero").Length - 1);

        Assert.Equal(0, (await InstallerAsync("Uninstall")).Exit);
        Assert.Equal(original, File.ReadAllText(_profile)); // a BOM added for the Unicode path is invisible here
        Assert.False(File.Exists(_macro));
    }

    [Fact]
    public async Task Uninstall_Deletes_A_Profile_That_Held_Only_Our_Block()
    {
        Assert.Equal(0, (await InstallerAsync("Install")).Exit);
        Assert.Equal(0, (await InstallerAsync("Uninstall")).Exit);
        Assert.False(File.Exists(_profile));
    }

    [Theory]
    [InlineData("Set-Alias -Name '/bzo' -Value other.exe -Scope Global")]
    [InlineData("function /bzo { 'x' }")]
    public async Task Existing_Definition_Is_Reported_As_Conflict_And_Left_Untouched(string existing)
    {
        File.WriteAllText(_profile, existing + "\r\n");
        var (exit, _) = await InstallerAsync("Install");
        Assert.Equal(3, exit);
        Assert.Equal(existing + "\r\n", File.ReadAllText(_profile));
    }

    [Fact]
    public async Task Hand_Edited_Block_Is_Not_Removed()
    {
        Assert.Equal(0, (await InstallerAsync("Install")).Exit);
        string edited = File.ReadAllText(_profile).Replace("-Scope Global", "-Scope Global; calc");
        File.WriteAllText(_profile, edited);
        Assert.Equal(3, (await InstallerAsync("Uninstall")).Exit);
        Assert.Equal(edited, File.ReadAllText(_profile));
    }

    [Fact]
    public async Task Fresh_PowerShell_Resolves_Slash_Bzo_With_Spaced_Unicode_Path_And_Propagates_Exit_Code()
    {
        Assert.Equal(0, (await InstallerAsync("Install")).Exit);
        string present = "füï file.bin";
        File.WriteAllText(Path.Combine(_dir.Path, present), "x");

        // where.exe /R <dir> <name>: exit 0 when found, 1 when not; both args carry spaces and non-ASCII.
        string Cmd(string name) =>
            $". '{_profile}'; /bzo /Q /R '{_dir.Path}' '{name}'; exit $LASTEXITCODE";

        // The profile and command line carry non-ASCII; pass them as a UTF-16 -EncodedCommand, not argv text.
        static string Enc(string s) => Convert.ToBase64String(Encoding.Unicode.GetBytes(s));

        Assert.Equal(0, (await RunAsync("-EncodedCommand", Enc(Cmd(present)))).Exit);
        Assert.Equal(1, (await RunAsync("-EncodedCommand", Enc(Cmd("missing é.bin")))).Exit);
    }
}
