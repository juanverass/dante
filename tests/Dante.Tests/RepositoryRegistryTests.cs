using Dante.Application.Contextos;
using Dante.Infrastructure.Contextos;
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

    [Fact]
    public void EnvironmentBindingsResolveAtExecutionAndSurviveRestart()
    {
        var repository = CreateRepository("project");
        var file = Path.Combine(root, "repositories.json");
        var registry = new RepositoryRegistry(file);
        registry.Add("@project", repository);
        registry.SetLiteral("@project", "API_BASE_URL", "https://example.test");
        registry.Bind("@project", "DATABASE_PASSWORD", "DANTE_TEST_HOST_SECRET");
        Assert.Throws<ArgumentException>(() => registry.SetLiteral("@project", "API_KEY", "unsafe"));

        var reopened = new RepositoryRegistry(file);
        Assert.Equal(2, reopened.Get("@project")!.Environment!.Count);
        var previous = Environment.GetEnvironmentVariable("DANTE_TEST_HOST_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("DANTE_TEST_HOST_SECRET", null);
            Assert.Throws<InvalidOperationException>(() => reopened.ResolveEnvironment("@project"));
            Environment.SetEnvironmentVariable("DANTE_TEST_HOST_SECRET", "first");
            Assert.Equal("first", reopened.ResolveEnvironment("@project").Values["DATABASE_PASSWORD"]);
            Environment.SetEnvironmentVariable("DANTE_TEST_HOST_SECRET", "second");
            var resolved = reopened.ResolveEnvironment("@project");
            Assert.Equal("second", resolved.Values["DATABASE_PASSWORD"]);
            Assert.Equal("https://example.test", resolved.Values["API_BASE_URL"]);
            Assert.True(resolved.HasSecrets);
        }
        finally { Environment.SetEnvironmentVariable("DANTE_TEST_HOST_SECRET", previous); }

        Assert.True(reopened.RemoveEnvironment("@project", "DATABASE_PASSWORD"));
        Assert.False(reopened.RemoveEnvironment("@project", "DATABASE_PASSWORD"));
        Assert.DoesNotContain(new RepositoryRegistry(file).Get("@project")!.Environment!,
            entry => entry.Key == "DATABASE_PASSWORD");
    }

    // #232 (AD-58): links are directional, persisted with the catalog and only between registered repositories.
    [Fact]
    public void LinksArePersistedDirectionalAndValidated()
    {
        var file = Path.Combine(root, "repositories.json");
        var registry = new RepositoryRegistry(file);
        registry.Add("@front", CreateRepository("front"));
        registry.Add("@back", CreateRepository("back"));
        registry.Add("@docs", CreateRepository("docs"));

        Assert.True(registry.Link("@FRONT", "@Back"));
        Assert.False(registry.Link("@front", "@back"));
        Assert.True(registry.Link("@front", "@docs"));
        Assert.Throws<ArgumentException>(() => registry.Link("@front", "@front"));
        Assert.Throws<ArgumentException>(() => registry.Link("@front", "@ghost"));
        Assert.Throws<ArgumentException>(() => registry.Link("@ghost", "@back"));
        Assert.Throws<ArgumentException>(() => registry.Link("@front", "@bad-alias"));

        var reopened = new RepositoryRegistry(file);
        Assert.Equal(["@back", "@docs"], reopened.Get("@front")!.Links!);
        Assert.Null(reopened.Get("@back")!.Links);
        ICatalogoDeRepositorios catalog = reopened;
        Assert.Equal(["@back", "@docs"], catalog.ResolverVinculos("@front").Select(linked => linked.Alias));
        Assert.Equal(reopened.Get("@back")!.Path, catalog.ResolverVinculos("@front")[0].Caminho);
        // Directional and direct: the back gains nothing from being linked.
        Assert.Empty(catalog.ResolverVinculos("@back"));

        Assert.True(reopened.Unlink("@front", "@docs"));
        Assert.False(reopened.Unlink("@front", "@docs"));
        Assert.Throws<ArgumentException>(() => reopened.Unlink("@ghost", "@docs"));
        Assert.Equal(["@back"], new RepositoryRegistry(file).Get("@front")!.Links!);
    }

    [Fact]
    public void LinksAreNotTransitive()
    {
        var registry = new RepositoryRegistry(Path.Combine(root, "repositories.json"));
        registry.Add("@front", CreateRepository("front"));
        registry.Add("@back", CreateRepository("back"));
        registry.Add("@infra", CreateRepository("infra"));
        registry.Link("@front", "@back");
        registry.Link("@back", "@infra");

        Assert.Equal(["@back"], ((ICatalogoDeRepositorios)registry).ResolverVinculos("@front").Select(linked => linked.Alias));
    }

    [Fact]
    public void NestedCheckoutsCannotBeLinked()
    {
        var registry = new RepositoryRegistry(Path.Combine(root, "repositories.json"));
        var outer = CreateRepository("outer");
        registry.Add("@outer", outer);
        registry.Add("@inner", CreateRepository(Path.Combine("outer", "inner")));

        Assert.Throws<ArgumentException>(() => registry.Link("@outer", "@inner"));
        Assert.Throws<ArgumentException>(() => registry.Link("@inner", "@outer"));
        Assert.Null(registry.Get("@outer")!.Links);
    }

    [Fact]
    public void RemovingARepositoryRemovesTheLinksToItInTheSameSave()
    {
        var file = Path.Combine(root, "repositories.json");
        var registry = new RepositoryRegistry(file);
        registry.Add("@front", CreateRepository("front"));
        registry.Add("@admin", CreateRepository("admin"));
        registry.Add("@back", CreateRepository("back"));
        registry.Link("@front", "@back");
        registry.Link("@admin", "@back");
        registry.Link("@front", "@admin");

        Assert.True(registry.Remove("@back"));

        var reopened = new RepositoryRegistry(file);
        Assert.Equal(["@admin"], reopened.Get("@front")!.Links!);
        Assert.Empty(reopened.Get("@admin")!.Links!);
    }

    // A catalog edited by hand, or a checkout moved away, blocks new executions instead of dropping the link.
    [Fact]
    public void BrokenLinksAreRefusedAtResolution()
    {
        var file = Path.Combine(root, "repositories.json");
        var registry = new RepositoryRegistry(file);
        registry.Add("@front", CreateRepository("front"));
        var back = CreateRepository("back");
        registry.Add("@back", back);
        registry.Link("@front", "@back");

        Directory.Delete(back, recursive: true);
        var moved = Assert.Throws<InvalidOperationException>(() =>
            ((ICatalogoDeRepositorios)new RepositoryRegistry(file)).ResolverVinculos("@front"));
        Assert.Contains("/repo unlink @front @back", moved.Message);

        File.WriteAllText(file, File.ReadAllText(file).Replace("\"@back\"\n", "\"@ghost\"\n"));
        var removed = Assert.Throws<InvalidOperationException>(() =>
            ((ICatalogoDeRepositorios)new RepositoryRegistry(file)).ResolverVinculos("@front"));
        Assert.Contains("@ghost não está mais cadastrado", removed.Message);
    }

    [Theory]
    [InlineData("[\"@back\", \"@back\"]")]
    [InlineData("[\"@front\"]")]
    [InlineData("[\"bad\"]")]
    public void InvalidLinksInTheFileStopTheLoad(string links)
    {
        var file = Path.Combine(root, "repositories.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(file, $$"""[{"Alias":"@front","Path":"/repos/front","GitHub":null,"Links":{{links}}}]""");

        Assert.ThrowsAny<Exception>(() => new RepositoryRegistry(file));
    }

    // Catalogs written before #232 have no Links field and keep working unchanged.
    [Fact]
    public void CatalogWithoutLinksStillLoads()
    {
        var file = Path.Combine(root, "repositories.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(file, """[{"Alias":"@front","Path":"/repos/front","GitHub":null,"Environment":null}]""");

        var registry = new RepositoryRegistry(file);
        Assert.Null(registry.Get("@front")!.Links);
        Assert.Empty(((ICatalogoDeRepositorios)registry).ResolverVinculos("@front"));
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
