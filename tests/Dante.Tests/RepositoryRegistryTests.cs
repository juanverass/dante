using Dante.Worker.Repositories;
using System.Diagnostics;

namespace Dante.Tests;

public sealed class RepositoryRegistryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-registry-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PersistsAndRemovesRepositoriesAcrossInstances()
    {
        var repository = CreateRepository("project");
        var file = Path.Combine(root, "config", "repositories.json");
        var registry = new RepositoryRegistry(file);
        var added = registry.Add("@Project", repository, "owner/project");
        Assert.Equal("@project", added.Alias);
        Assert.Equal(repository, added.Path);
        Assert.Equal("owner/project", added.GitHub);

        var reopened = new RepositoryRegistry(file);
        Assert.Equal(added, reopened.Get("@PROJECT"));
        Assert.Single(reopened.List());
        Assert.Throws<ArgumentException>(() => reopened.Add("@project", repository));
        Assert.True(reopened.Remove("@PROJECT"));
        Assert.False(reopened.Remove("@project"));
        Assert.Empty(new RepositoryRegistry(file).List());
    }

    [Fact]
    public void RejectsInvalidAliasesPathsAndMismatchedOrigin()
    {
        var repository = CreateRepository("project");
        var registry = new RepositoryRegistry(Path.Combine(root, "repositories.json"));
        Assert.Throws<ArgumentException>(() => registry.Add("@bad-name", repository));
        Assert.Throws<ArgumentException>(() => registry.Add("@relative", "project"));
        Assert.Throws<ArgumentException>(() => registry.Add("@missing", Path.Combine(root, "missing")));
        var plain = Path.Combine(root, "plain");
        Directory.CreateDirectory(plain);
        Assert.Throws<ArgumentException>(() => registry.Add("@plain", plain));
        Assert.Throws<ArgumentException>(() => registry.Add("@remote", repository, "owner/other"));
        Assert.Empty(registry.List());
    }

    [Fact]
    public void AcceptsEquivalentPathWithTrailingDirectorySeparator()
    {
        var repository = CreateRepository("project");
        var registry = new RepositoryRegistry(Path.Combine(root, "repositories.json"));
        var added = registry.Add("@project", repository + Path.DirectorySeparatorChar);
        Assert.Equal(repository, added.Path);
    }

    private string CreateRepository(string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        RunGit(path, "init");
        RunGit(path, "remote", "add", "origin", "https://github.com/owner/project.git");
        return path;
    }

    private static void RunGit(string path, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = path, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
