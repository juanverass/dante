using System.Diagnostics;
using Dante.Infrastructure.Contextos;

namespace Dante.Tests;

// #232 review: the per-repository report reads git without changing anything, so a partial delivery is visible.
public sealed class LeitorDeCheckoutsGitTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-checkouts-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReportsBranchUncommittedFilesAndUnpushedCommits()
    {
        var remote = Path.Combine(root, "remote.git");
        Git(root, "init", "--bare", remote);
        var checkout = Path.Combine(root, "checkout");
        Git(root, "init", "-b", "main", checkout);
        Commit(checkout, "a.txt");
        Git(checkout, "remote", "add", "origin", remote);
        Git(checkout, "push", "-u", "origin", "main");
        var reader = new LeitorDeCheckoutsGit();

        Assert.Equal(new("main", 0, 0, true), await reader.LerAsync(checkout));

        Git(checkout, "switch", "-c", "feat/metas");
        Commit(checkout, "b.txt");
        Commit(checkout, "c.txt");
        File.WriteAllText(Path.Combine(checkout, "pendente.txt"), "x");
        File.AppendAllText(Path.Combine(checkout, "a.txt"), "y");
        Assert.Equal(new("feat/metas", 2, null, false), await reader.LerAsync(checkout));

        Git(checkout, "push", "-u", "origin", "feat/metas");
        Commit(checkout, "d.txt");
        Assert.Equal(new("feat/metas", 2, 1, true), await reader.LerAsync(checkout));
        Assert.Empty(Git(checkout, "status", "--porcelain", "--", "d.txt"));
    }

    [Fact]
    public async Task UnbornDetachedAndMissingCheckoutsDoNotThrow()
    {
        var reader = new LeitorDeCheckoutsGit();
        var unborn = Path.Combine(root, "unborn");
        Git(root, "init", "-b", "main", unborn);
        Assert.Equal(new("main", 0, null, false), await reader.LerAsync(unborn));

        Commit(unborn, "a.txt");
        Commit(unborn, "b.txt");
        Git(unborn, "checkout", "--detach", "HEAD~1");
        Assert.Equal("(HEAD destacado)", (await reader.LerAsync(unborn)).Branch);

        var missing = await reader.LerAsync(Path.Combine(root, "missing"));
        Assert.Equal((null, "não foi possível ler o Git deste checkout"), (missing.Branch, missing.Erro));
    }

    private static void Commit(string checkout, string file)
    {
        File.WriteAllText(Path.Combine(checkout, file), file);
        Git(checkout, "add", file);
        Git(checkout, "-c", "user.name=Teste", "-c", "user.email=teste@example.test", "commit", "-q", "-m", file);
    }

    private static string Git(string directory, params string[] arguments)
    {
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
