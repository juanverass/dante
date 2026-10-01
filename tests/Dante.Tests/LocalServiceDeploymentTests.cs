namespace Dante.Tests;

// The service files are not exercised by the suite at runtime; these checks keep the guarantees of #39 from drifting.
public sealed class LocalServiceDeploymentTests
{
    private static readonly string Root = FindRepositoryRoot();

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
