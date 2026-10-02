namespace Dante.Worker.Attachments;

public enum AttachmentKind { Image, Audio, Video, Document }

// The neutral attachment of AD-29: no Telegram type reaches sessions, drivers or runners. Name is only metadata.
public sealed record Attachment(
    string Id,
    long OwnerId,
    AttachmentKind Kind,
    string MediaType,
    string Path,
    long Bytes,
    int? Width,
    int? Height,
    string? Name);

public sealed class AttachmentRejectedException(string message) : Exception(message);

// Files received from users, kept outside any checkout in ~/.dante/attachments/<owner>/<scope>/ with owner-only
// permissions. File names are generated here; nothing the sender declared becomes part of a path (AD-29).
public sealed class AttachmentStore
{
    public const long MaxImageBytes = 7 * 1024 * 1024;
    public const int MaxImageSide = 8000;
    // Audio and video only have to fit the getFile download (#96); duration is limited when they are prepared.
    public const long MaxMediaBytes = 20 * 1024 * 1024;
    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private long nextId;

    public AttachmentStore(string? root = null)
    {
        root ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dante",
            "attachments");
        if (!System.IO.Path.IsPathFullyQualified(root))
            throw new ArgumentException("O diretório de anexos deve ter path absoluto.", nameof(root));
        Root = System.IO.Path.GetFullPath(root);
    }

    public string Root { get; }

    // write fills the file (e.g. a size-limited download); the content must then be a supported image.
    public async Task<Attachment> SaveImageAsync(long ownerId, string scope, string? name,
        Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        var directory = CreateDirectory(ownerId, scope);
        var (id, partial, stream) = CreateFile(directory);
        try
        {
            await using (stream) await write(stream, cancellationToken);
            var image = ImageInspector.Inspect(partial) ??
                throw new AttachmentRejectedException("formato não suportado; envie JPEG, PNG, GIF ou WebP");
            if (image.Width > MaxImageSide || image.Height > MaxImageSide)
                throw new AttachmentRejectedException($"imagem acima de {MaxImageSide} px por lado");
            var path = System.IO.Path.Combine(directory, id + image.Extension);
            File.Move(partial, path);
            return new Attachment(id, ownerId, AttachmentKind.Image, image.MediaType, path, new FileInfo(path).Length,
                image.Width, image.Height, name);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    // write fills the file; the content must then be an audio or video container of the kind declared by the field
    // Telegram used (voice, audio, video, video_note or the document's MIME type). Its streams are checked later.
    public async Task<Attachment> SaveMediaAsync(long ownerId, string scope, AttachmentKind kind, string? name,
        Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        if (kind is not (AttachmentKind.Audio or AttachmentKind.Video))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Somente áudio ou vídeo.");
        var directory = CreateDirectory(ownerId, scope);
        var (id, partial, stream) = CreateFile(directory);
        try
        {
            await using (stream) await write(stream, cancellationToken);
            var media = MediaInspector.Inspect(partial, kind) ?? throw new AttachmentRejectedException(kind ==
                AttachmentKind.Audio ? "formato de áudio não suportado; envie OGG, MP3, M4A, WAV, FLAC ou WebM"
                : "formato de vídeo não suportado; envie MP4, MOV, WebM ou OGG");
            var path = System.IO.Path.Combine(directory, id + media.Extension);
            File.Move(partial, path);
            return new Attachment(id, ownerId, kind, media.MediaType, path, new FileInfo(path).Length, null, null, name);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    // Only files under this store are ever deleted, whatever path an attachment carries.
    public void Delete(Attachment attachment)
    {
        var path = System.IO.Path.GetFullPath(attachment.Path);
        if (IsUnderRoot(path)) File.Delete(path);
    }

    // A consumed attachment moves to the directory of the session or job that uses it, kept until that one ends (AD-29).
    public Attachment MoveTo(Attachment attachment, string scope)
    {
        var source = System.IO.Path.GetFullPath(attachment.Path);
        if (!IsUnderRoot(source)) throw new ArgumentException("Anexo fora do diretório de anexos.", nameof(attachment));
        var path = System.IO.Path.Combine(CreateDirectory(attachment.OwnerId, scope), System.IO.Path.GetFileName(source));
        File.Move(source, path);
        return attachment with { Path = path };
    }

    // Session and job ids restart with the process, so a directory found for a new one is a leftover: removed first.
    public void DeleteScope(long ownerId, string scope)
    {
        var directory = ScopeDirectory(ownerId, scope);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    // At startup the in-memory registry is empty, so files older than maxAge are leftovers of a previous run.
    public int SweepStale(TimeSpan maxAge)
    {
        if (!Directory.Exists(Root)) return 0;
        var limit = DateTime.UtcNow - maxAge;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(file) >= limit) continue;
            File.Delete(file);
            removed++;
        }
        foreach (var directory in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        return removed;
    }

    private bool IsUnderRoot(string path) =>
        path.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private string ScopeDirectory(long ownerId, string scope)
    {
        if (scope.Length == 0 || !scope.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            throw new ArgumentException("Escopo de anexo inválido.", nameof(scope));
        return System.IO.Path.Combine(Root, ownerId.ToString(System.Globalization.CultureInfo.InvariantCulture), scope);
    }

    private string CreateDirectory(long ownerId, string scope)
    {
        var directory = ScopeDirectory(ownerId, scope);
        foreach (var path in new[] { Root, System.IO.Path.GetDirectoryName(directory)!, directory })
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
            else Directory.CreateDirectory(path, OwnerOnlyDirectory);
        }
        return directory;
    }

    // Ids restart with the process, and files younger than the sweep limit survive a restart: an id is used only if
    // no file of a previous run has it under any extension. CreateNew guards the partial file itself.
    private (string Id, string Path, FileStream Stream) CreateFile(string directory)
    {
        while (true)
        {
            var id = $"A{Interlocked.Increment(ref nextId):D6}";
            if (Directory.EnumerateFiles(directory, id + ".*").Any()) continue;
            var path = System.IO.Path.Combine(directory, id + ".part");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnlyFile;
            try { return (id, path, new FileStream(path, options)); }
            catch (IOException) when (File.Exists(path)) { }
        }
    }
}
