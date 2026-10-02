using System.Globalization;
using Dante.Worker.Attachments;

namespace Dante.Worker.Artifacts;

// A file produced in a session and accepted for delivery: a private copy, so retry and /resend never depend on the
// original staying in place. Name is only shown back; Path is the copy.
public sealed record Artifact(
    string Id,
    long OwnerId,
    string Path,
    string Name,
    string MediaType,
    long Bytes,
    bool IsImage,
    bool AsPhoto);

public sealed class ArtifactRejectedException(string message) : Exception(message);

// Files leave the host only through an explicit channel (AD-29): a structured event of the CLI or a /send of the user.
// Whatever the channel, a file is accepted only if its real path, with every symlink resolved, is inside the root that
// channel allows; if it is a regular, non-empty file within the size limit; and if nothing in its path looks like a
// credential. Accepted files are copied to ~/.dante/artifacts/<owner>/ with owner-only permissions.
public sealed class ArtifactStore
{
    public const long MaxBytes = 50 * 1024 * 1024;
    // sendPhoto: up to 10 MB, width + height up to 10000 and a ratio up to 20.
    public const long MaxPhotoBytes = 10 * 1024 * 1024;
    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly string[] SecretDirectories = [".git", ".ssh", ".aws", ".gnupg", ".docker", ".kube", ".azure",
        ".config"];
    private static readonly string[] SecretNames = [".netrc", ".npmrc", ".pypirc", ".git-credentials", "credentials",
        "credentials.json"];
    private static readonly string[] SecretPrefixes = [".env", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519"];
    private static readonly string[] SecretExtensions = [".pem", ".key", ".p12", ".pfx", ".jks", ".keystore", ".kdbx"];
    private long nextId;

    public ArtifactStore(string? root = null, string? generatedImagesRoot = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        root ??= System.IO.Path.Combine(home, ".dante", "artifacts");
        // Codex saves generated images under $CODEX_HOME/generated_images (default ~/.codex), #93 spike.
        generatedImagesRoot ??= System.IO.Path.Combine(
            Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codexHome
                ? codexHome : System.IO.Path.Combine(home, ".codex"), "generated_images");
        if (!System.IO.Path.IsPathFullyQualified(root) || !System.IO.Path.IsPathFullyQualified(generatedImagesRoot))
            throw new ArgumentException("Os diretórios de artefatos devem ter path absoluto.");
        Root = System.IO.Path.GetFullPath(root);
        GeneratedImagesRoot = System.IO.Path.GetFullPath(generatedImagesRoot);
    }

    public string Root { get; }

    // The only root accepted for images the Codex session reports as generated.
    public string GeneratedImagesRoot { get; }

    // path is absolute or relative to allowedRoot. Throws ArtifactRejectedException with a reason the user can read.
    public Artifact Capture(long ownerId, string path, string allowedRoot, bool imageOnly = false)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
            throw new ArtifactRejectedException("caminho inválido");
        var root = RealPath(System.IO.Path.GetFullPath(allowedRoot));
        var requested = System.IO.Path.GetFullPath(path, System.IO.Path.GetFullPath(allowedRoot));
        string real;
        try { real = RealPath(requested); }
        catch (IOException) { throw new ArtifactRejectedException("caminho inválido"); }
        if (!IsWithin(real, root)) throw new ArtifactRejectedException("fora do diretório permitido");
        var relative = System.IO.Path.GetRelativePath(root, real);
        if (LooksLikeSecret(relative)) throw new ArtifactRejectedException("arquivo protegido (credenciais ou configuração)");

        if (Directory.Exists(real)) throw new ArtifactRejectedException("não é um arquivo");
        var info = new FileInfo(real);
        if (!info.Exists) throw new ArtifactRejectedException("arquivo não encontrado");
        if (info.Length == 0) throw new ArtifactRejectedException("arquivo vazio");
        if (info.Length > MaxBytes) throw new ArtifactRejectedException($"acima de {MaxBytes / 1024 / 1024} MB");

        var directory = CreateDirectory(ownerId);
        var id = $"F{Interlocked.Increment(ref nextId).ToString("D6", CultureInfo.InvariantCulture)}";
        var copy = System.IO.Path.Combine(directory, id + ".part");
        try
        {
            using (var source = new FileStream(real, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // The file opened is the one checked: a swap of the path for a link after the checks is caught here.
                if (OperatingSystem.IsLinux() &&
                    File.ResolveLinkTarget($"/proc/self/fd/{source.SafeFileHandle.DangerousGetHandle()}", false)?.FullName
                        is { } opened && opened != real)
                    throw new ArtifactRejectedException("o arquivo mudou durante a verificação");
                if (source.Length > MaxBytes) throw new ArtifactRejectedException($"acima de {MaxBytes / 1024 / 1024} MB");
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnlyFile;
                using var destination = new FileStream(copy, options);
                source.CopyTo(destination);
            }

            var image = ImageInspector.Inspect(copy);
            if (imageOnly && image is null) throw new ArtifactRejectedException("não é uma imagem JPEG, PNG, GIF ou WebP");
            var name = System.IO.Path.GetFileName(real);
            var extension = image?.Extension ?? SafeExtension(name);
            var final = System.IO.Path.Combine(directory, id + extension);
            File.Move(copy, final);
            var bytes = new FileInfo(final).Length;
            var asPhoto = image is { } photo && bytes <= MaxPhotoBytes && photo.Width + photo.Height <= 10000 &&
                Math.Max(photo.Width, photo.Height) <= 20 * Math.Max(1, Math.Min(photo.Width, photo.Height));
            return new Artifact(id, ownerId, final, name, image?.MediaType ?? "application/octet-stream", bytes,
                image is not null, asPhoto);
        }
        catch
        {
            File.Delete(copy);
            throw;
        }
    }

    // Only files under this store are ever deleted.
    public void Delete(Artifact artifact)
    {
        var path = System.IO.Path.GetFullPath(artifact.Path);
        if (IsWithin(path, Root) && path != Root) File.Delete(path);
    }

    // Records live in memory only: at startup every copy left by a previous run is unreachable and goes away.
    public int SweepAll()
    {
        if (!Directory.Exists(Root)) return 0;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
            removed++;
        }
        return removed;
    }

    private string CreateDirectory(long ownerId)
    {
        var directory = System.IO.Path.Combine(Root, ownerId.ToString(CultureInfo.InvariantCulture));
        foreach (var path in new[] { Root, directory })
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
            else Directory.CreateDirectory(path, OwnerOnlyDirectory);
        }
        return directory;
    }

    private static bool LooksLikeSecret(string relative)
    {
        var segments = relative.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (segments[..^1].Any(segment => SecretDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            return true;
        var name = segments[^1];
        return SecretNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            SecretPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
            SecretExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    // Only a short alphanumeric extension of the original name is kept for the copy.
    private static string SafeExtension(string name)
    {
        var extension = System.IO.Path.GetExtension(name);
        return extension.Length is > 1 and <= 10 && extension[1..].All(char.IsAsciiLetterOrDigit) ? extension : "";
    }

    private static bool IsWithin(string path, string root) =>
        path == root || path.StartsWith(root.TrimEnd(System.IO.Path.DirectorySeparatorChar) +
            System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);

    // The full path with every symbolic link resolved, component by component; a missing tail is kept as written.
    private static string RealPath(string path)
    {
        var result = System.IO.Path.GetPathRoot(path)!;
        var pending = new Queue<string>(path[result.Length..].Split(System.IO.Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries));
        for (var links = 0; pending.TryDequeue(out var segment);)
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                result = System.IO.Path.GetDirectoryName(result) ?? result;
                continue;
            }
            var next = System.IO.Path.Combine(result, segment);
            FileSystemInfo entry = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (entry.LinkTarget is { } target)
            {
                if (++links > 40) throw new IOException("Links simbólicos demais.");
                var resolved = System.IO.Path.GetFullPath(target, result);
                var rest = pending.ToArray();
                pending.Clear();
                result = System.IO.Path.GetPathRoot(resolved)!;
                foreach (var part in resolved[result.Length..].Split(System.IO.Path.DirectorySeparatorChar,
                             StringSplitOptions.RemoveEmptyEntries).Concat(rest)) pending.Enqueue(part);
                continue;
            }
            result = next;
        }
        return result;
    }
}
