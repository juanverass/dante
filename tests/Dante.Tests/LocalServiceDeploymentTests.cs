using System.Diagnostics;

namespace Dante.Tests;

// The service files are not exercised by the suite at runtime; these checks keep the guarantees of #39 from drifting.
// The script tests run the real scripts on Linux with systemctl, loginctl and dotnet replaced by recording stubs.
public sealed class LocalServiceDeploymentTests : IDisposable
{
    private static readonly string Root = FindRepositoryRoot();
    private readonly string temp = Path.Combine(Path.GetTempPath(), "dante-service-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void UnitRunsThePublishedWorkerUnprivilegedAndRestartsOnFailure()
    {
        var unit = Directives(Read("deploy", "systemd", "dante.service"));

        Assert.Equal("/usr/bin/env dotnet %h/.local/share/dante/app/Dante.Worker.dll", Assert.Single(unit["ExecStart"]));
        Assert.Equal("%h/.config/dante/dante.env", Assert.Single(unit["EnvironmentFile"]));
        Assert.Equal("on-failure", Assert.Single(unit["Restart"]));
        Assert.Equal("mixed", Assert.Single(unit["KillMode"]));
        Assert.Equal("yes", Assert.Single(unit["NoNewPrivileges"]));
        Assert.Equal("default.target", Assert.Single(unit["WantedBy"]));
        Assert.Contains(unit["Environment"], value => value.StartsWith("PATH=%h/.local/bin:", StringComparison.Ordinal));
        // A user unit never names another account, and secrets only come from the environment file.
        Assert.False(unit.ContainsKey("User"));
        Assert.DoesNotContain(unit["Environment"], value => value.StartsWith("Telegram__", StringComparison.Ordinal));
    }

    [Fact]
    public void EnvironmentTemplateCarriesNoSecretValues()
    {
        var assignments = Read("deploy", "dante.env.example").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();

        Assert.Equal(["Telegram__BotToken=", "Telegram__AllowedUserIds="], assignments);
    }

    [Fact]
    public void ScriptsRefuseRootAndInstallTheEnvironmentFileWithOwnerOnlyAccess()
    {
        var script = Read("deploy", "dante-service.sh");
        Assert.Contains("[ \"$(id -u)\" -ne 0 ]", script);
        Assert.Contains("install -m 600 \"$REPO_ROOT/deploy/dante.env.example\" \"$ENV_FILE\"", script);
        Assert.Contains("chmod 600 \"$ENV_FILE\"", script);
        Assert.DoesNotContain("sudo systemctl", script);

        var autostart = Read("deploy", "windows", "Register-DanteAutostart.ps1");
        Assert.Contains("-LogonType Interactive -RunLevel Limited", autostart);
        Assert.Contains("wslg.exe", autostart);
    }

    [Fact]
    public void ManualEnvironmentConvertsOnlyLiteralValues()
    {
        if (!CanRunScripts()) return;
        Directory.CreateDirectory(temp);
        var source = Path.Combine(temp, "env");
        File.WriteAllText(source, "# manual\nexport Telegram__BotToken=\"123:abc\"\n  export Telegram__AllowedUserIds=1,2\n" +
            "DANTE_GENERAL_WORKSPACE='/home/u/$x'\n");
        var converted = Path.Combine(temp, "dante.env");

        Assert.Equal(0, Run(["deploy/dante-env.sh", "convert", source, converted]).ExitCode);
        Assert.Equal("# manual\nTelegram__BotToken=\"123:abc\"\nTelegram__AllowedUserIds=1,2\n" +
            "DANTE_GENERAL_WORKSPACE='/home/u/$x'\n", File.ReadAllText(converted));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(converted));
    }

    [Theory]
    [InlineData("export DANTE_GENERAL_WORKSPACE=\"$HOME/.dante/workspaces/general\"")]
    [InlineData("export PATH=$PATH:/opt/bin")]
    [InlineData("export DANTE_GENERAL_WORKSPACE=~/general")]
    [InlineData("export SECRET_VALUE=\"a\\\"b\"")]
    [InlineData("export SECRET_VALUE=`cat /tmp/secret`")]
    [InlineData("export SECRET_VALUE=abc # comentário")]
    public void ShellDependentValuesAreRefusedWithoutWritingOrEchoingThem(string line)
    {
        if (!CanRunScripts()) return;
        Directory.CreateDirectory(temp);
        var source = Path.Combine(temp, "env");
        File.WriteAllText(source, "export Telegram__BotToken=\"123:abc\"\n" + line + "\n");
        var converted = Path.Combine(temp, "dante.env");

        var result = Run(["deploy/dante-env.sh", "convert", source, converted]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(converted));
        Assert.Contains(": 2.", result.Output);
        Assert.DoesNotContain("123:abc", result.Output);
        Assert.Empty(Directory.GetFiles(temp, ".dante-env.*"));
    }

    [Fact]
    public void InstallRefusesAnUnconvertibleManualEnvironmentBeforeInstallingAnything()
    {
        if (!CanRunScripts()) return;
        var home = Home("export Telegram__BotToken=\"123:abc\"\nexport DANTE_GENERAL_WORKSPACE=\"$HOME/general\"\n");

        var result = RunInstall(home);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("rode install novamente", result.Output);
        Assert.False(File.Exists(Path.Combine(home, ".config", "dante", "dante.env")));
        Assert.False(File.Exists(Path.Combine(home, ".config", "systemd", "user", "dante.service")));
        Assert.False(Directory.Exists(Path.Combine(home, ".local", "share", "dante")));
        Assert.DoesNotContain(Calls(), call => call.StartsWith("dotnet", StringComparison.Ordinal) ||
            call.Contains(" enable", StringComparison.Ordinal) || call.Contains(" restart", StringComparison.Ordinal));
    }

    [Fact]
    public void InstallEnablesAndStartsOnlyAConfiguredService()
    {
        if (!CanRunScripts()) return;
        var home = Home("export Telegram__BotToken=\"123:abc\"\nexport Telegram__AllowedUserIds=\"1\"\n");

        var result = RunInstall(home);

        Assert.True(result.ExitCode == 0, result.Output);
        var environment = Path.Combine(home, ".config", "dante", "dante.env");
        Assert.Equal("Telegram__BotToken=\"123:abc\"\nTelegram__AllowedUserIds=\"1\"\n", File.ReadAllText(environment));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(environment));
        Assert.True(File.Exists(Path.Combine(home, ".local", "share", "dante", "app", "Dante.Worker.dll")));
        var calls = Calls();
        Assert.Contains("systemctl --user enable dante.service", calls);
        Assert.Contains("systemctl --user restart dante.service", calls);
    }

    [Fact]
    public void InstallWithoutTokenLeavesTheServiceDisabled()
    {
        if (!CanRunScripts()) return;
        var home = Home(null);

        var result = RunInstall(home);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("não habilitado", result.Output);
        Assert.True(File.Exists(Path.Combine(home, ".config", "systemd", "user", "dante.service")));
        var calls = Calls();
        Assert.DoesNotContain("systemctl --user enable dante.service", calls);
        Assert.DoesNotContain("systemctl --user restart dante.service", calls);
    }

    private string Home(string? manualEnvironment)
    {
        var home = Path.Combine(temp, "home");
        Directory.CreateDirectory(Path.Combine(home, ".config", "dante"));
        if (manualEnvironment is not null) File.WriteAllText(Path.Combine(home, ".config", "dante", "env"), manualEnvironment);
        var bin = Path.Combine(temp, "bin");
        Directory.CreateDirectory(bin);
        var log = Path.Combine(temp, "calls");
        Stub(bin, "systemctl", $"echo \"systemctl $*\" >> '{log}'");
        Stub(bin, "loginctl", $"echo \"loginctl $*\" >> '{log}'\n[ \"$1\" = show-user ] && echo yes\nexit 0");
        Stub(bin, "dotnet", $"echo \"dotnet $*\" >> '{log}'\nwhile [ $# -gt 0 ]; do [ \"$1\" = -o ] && mkdir -p \"$2\" && " +
            "touch \"$2/Dante.Worker.dll\"; shift; done");
        return home;
    }

    private (int ExitCode, string Output) RunInstall(string home) =>
        Run(["deploy/dante-service.sh", "install"], new Dictionary<string, string>
        {
            ["HOME"] = home,
            ["PATH"] = Path.Combine(temp, "bin") + ":/usr/bin:/bin"
        });

    private string[] Calls()
    {
        var log = Path.Combine(temp, "calls");
        return File.Exists(log) ? File.ReadAllLines(log) : [];
    }

    private static void Stub(string bin, string name, string body)
    {
        var path = Path.Combine(bin, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static (int ExitCode, string Output) Run(string[] arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo("bash")
        {
            WorkingDirectory = Root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment ?? new Dictionary<string, string>()) start.Environment[key] = value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.Result + error.Result);
    }

    // The scripts target Linux and refuse root, so elsewhere these tests have nothing to check.
    private static bool CanRunScripts() =>
        OperatingSystem.IsLinux() && Environment.UserName != "root" && File.Exists("/bin/bash");

    public void Dispose()
    {
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
    }

    private static Dictionary<string, List<string>> Directives(string unit)
    {
        var directives = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in unit.Split('\n').Select(line => line.Trim()))
        {
            var separator = line.IndexOf('=');
            if (line.Length == 0 || line[0] is '#' or ';' or '[' || separator < 0) continue;
            var key = line[..separator];
            if (!directives.TryGetValue(key, out var values)) directives[key] = values = [];
            values.Add(line[(separator + 1)..]);
        }
        return directives;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root, .. parts])).Replace("\r\n", "\n");

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Dante.sln"))) return directory.FullName;
        }
        throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }
}
