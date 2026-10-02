using Dante.Worker.Artifacts;

namespace Dante.Tests;

// #97: a produced file leaves the host only from the root its channel allows, as a private copy, and never when it
// escapes that root, is not a regular file, exceeds the limit or looks like a credential.
public sealed class ArtifactStoreTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("dante-artifacts-").FullName;
    private readonly ArtifactStore store;

    public ArtifactStoreTests() => store = new ArtifactStore(Path.Combine(root, "store"), Path.Combine(root, "generated"));

    private string Workspace => Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;

    [Fact]
    public void AcceptedFileIsAPrivateCopyThatOutlivesTheOriginal()
    {
        var original = Write("out/relatorio.csv", "a,b\n1,2\n");

        var artifact = store.Capture(42, "out/relatorio.csv", Workspace);

        Assert.Equal(("F000001", 42L, "relatorio.csv", "application/octet-stream", false, false),
            (artifact.Id, artifact.OwnerId, artifact.Name, artifact.MediaType, artifact.IsImage, artifact.AsPhoto));
        Assert.Equal(Path.Combine(store.Root, "42", "F000001.csv"), artifact.Path);
        File.WriteAllText(original, "mudou");
        File.Delete(original);
        Assert.Equal("a,b\n1,2\n", File.ReadAllText(artifact.Path));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(artifact.Path));
    }

    [Fact]
    public void ImagesAreRecognisedByContentAndGoAsPhotoOnlyWithinTelegramLimits()
    {
        File.WriteAllBytes(Path.Combine(Workspace, "grafico.bin"), TestImages.Png(800, 600));
        File.WriteAllBytes(Path.Combine(Workspace, "faixa.png"), TestImages.Png(4000, 100));

        var chart = store.Capture(42, "grafico.bin", Workspace);
        var strip = store.Capture(42, "faixa.png", Workspace);

        Assert.Equal(("image/png", true, true), (chart.MediaType, chart.IsImage, chart.AsPhoto));
        Assert.EndsWith(".png", chart.Path);
        // A ratio over 20 is refused by sendPhoto: the original still goes as document.
        Assert.Equal((true, false), (strip.IsImage, strip.AsPhoto));
    }

    [Theory]
    [InlineData("../fora.txt", "fora do diretório permitido")]
    [InlineData("/etc/hostname", "fora do diretório permitido")]
    [InlineData("nao-existe.txt", "arquivo não encontrado")]
    [InlineData("pasta", "não é um arquivo")]
    [InlineData("vazio.txt", "arquivo vazio")]
    [InlineData(".env", "arquivo protegido (credenciais ou configuração)")]
    [InlineData(".env.local", "arquivo protegido (credenciais ou configuração)")]
    [InlineData("deploy/id_ed25519", "arquivo protegido (credenciais ou configuração)")]
    [InlineData("certs/server.pem", "arquivo protegido (credenciais ou configuração)")]
    [InlineData(".git/config", "arquivo protegido (credenciais ou configuração)")]
    [InlineData("home/.ssh/known_hosts", "arquivo protegido (credenciais ou configuração)")]
    public void RefusedPathsNeverBecomeArtifacts(string path, string reason)
    {
        Write("../fora.txt", "segredo de fora");
        Directory.CreateDirectory(Path.Combine(Workspace, "pasta"));
        Write("vazio.txt", "");
        foreach (var file in new[] { ".env", ".env.local", "deploy/id_ed25519", "certs/server.pem", ".git/config",
                     "home/.ssh/known_hosts" }) Write(file, "x");

        var error = Assert.Throws<ArtifactRejectedException>(() => store.Capture(42, path, Workspace));

        Assert.Equal(reason, error.Message);
        Assert.False(Directory.Exists(store.Root) && Directory.EnumerateFiles(store.Root, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void SymbolicLinksAreFollowedAndMayNotLeaveTheRoot()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Write("../segredo.txt", "fora");
        File.CreateSymbolicLink(Path.Combine(Workspace, "atalho.txt"), outside);
        Directory.CreateSymbolicLink(Path.Combine(Workspace, "pasta-fora"), Path.GetDirectoryName(outside)!);
        Write("dentro.txt", "ok");
        File.CreateSymbolicLink(Path.Combine(Workspace, "atalho-dentro.txt"), Path.Combine(Workspace, "dentro.txt"));
        File.CreateSymbolicLink(Path.Combine(Workspace, "chave"), Path.Combine(Workspace, ".env"));
        Write(".env", "TOKEN=1");

        Assert.Equal("fora do diretório permitido",
            Assert.Throws<ArtifactRejectedException>(() => store.Capture(42, "atalho.txt", Workspace)).Message);
        Assert.Equal("fora do diretório permitido",
            Assert.Throws<ArtifactRejectedException>(() => store.Capture(42, "pasta-fora/segredo.txt", Workspace)).Message);
        // A link inside the root to a protected file is judged by the file it reaches.
        Assert.Equal("arquivo protegido (credenciais ou configuração)",
            Assert.Throws<ArtifactRejectedException>(() => store.Capture(42, "chave", Workspace)).Message);
        Assert.Equal("dentro.txt", store.Capture(42, "atalho-dentro.txt", Workspace).Name);
    }

    [Fact]
    public void FilesOverTheLimitAndNonImagesOfAnImageChannelAreRefused()
    {
        using (var big = File.Create(Path.Combine(Workspace, "grande.bin"))) big.SetLength(ArtifactStore.MaxBytes + 1);
        Write("notas.txt", "texto");

        Assert.Equal("acima de 50 MB",
            Assert.Throws<ArtifactRejectedException>(() => store.Capture(42, "grande.bin", Workspace)).Message);
        Assert.Equal("não é uma imagem JPEG, PNG, GIF ou WebP",
            Assert.Throws<ArtifactRejectedException>(() => store.Capture(42, "notas.txt", Workspace, imageOnly: true)).Message);
        Assert.Empty(Directory.EnumerateFiles(store.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void DeleteStaysInsideTheStoreAndStartupSweepsEveryCopy()
    {
        var kept = Write("fora-do-store.txt", "x");
        var artifact = store.Capture(42, "fora-do-store.txt", Workspace);
        store.Capture(7, "fora-do-store.txt", Workspace);

        store.Delete(artifact with { Path = kept });
        Assert.True(File.Exists(kept));
        store.Delete(artifact);
        Assert.False(File.Exists(artifact.Path));
        Assert.Equal(1, store.SweepAll());
        Assert.Empty(Directory.EnumerateFiles(store.Root, "*", SearchOption.AllDirectories));
    }

    // Review of #107: a link left inside the store never takes the startup sweep, a copy or a deletion outside it.
    [Fact]
    public void LinksInsideTheStoreAreNeverFollowed()
    {
        if (OperatingSystem.IsWindows()) return;
        var external = Directory.CreateDirectory(Path.Combine(root, "externo")).FullName;
        File.WriteAllText(Path.Combine(external, "keep.txt"), "do host");
        File.WriteAllText(Path.Combine(external, "keep2.txt"), "do host");
        Directory.CreateDirectory(store.Root);
        Directory.CreateSymbolicLink(Path.Combine(store.Root, "123"), external);
        File.CreateSymbolicLink(Path.Combine(store.Root, "solto"), Path.Combine(external, "keep2.txt"));
        Directory.CreateDirectory(Path.Combine(store.Root, "7"));
        File.WriteAllText(Path.Combine(store.Root, "7", "F000001.csv"), "cópia");

        Assert.Equal(1, store.SweepAll());

        Assert.Equal(["keep.txt", "keep2.txt"], Directory.GetFiles(external).Select(Path.GetFileName).Order());
        // Only the emptied real directory remains: the copy and both links are gone.
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(store.Root, "*", SearchOption.AllDirectories),
            entry => !Directory.Exists(entry) || new DirectoryInfo(entry).LinkTarget is not null);

        // A copy for an owner whose directory is a link is refused, and a deletion through it does nothing.
        Directory.CreateSymbolicLink(Path.Combine(store.Root, "42"), external);
        Write("relatorio.csv", "a");
        Assert.Equal("diretório de cópias inválido",
            Assert.Throws<ArtifactRejectedException>(() => store.Capture(42, "relatorio.csv", Workspace)).Message);
        store.Delete(new Artifact("F000009", 42, Path.Combine(store.Root, "42", "keep.txt"), "keep.txt",
            "application/octet-stream", 7, false, false));
        Assert.Equal(["keep.txt", "keep2.txt"], Directory.GetFiles(external).Select(Path.GetFileName).Order());
    }

    private string Write(string relative, string content)
    {
        var path = Path.GetFullPath(Path.Combine(Workspace, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(root, true);
}
