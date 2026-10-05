using System.Security.Cryptography;

namespace Dante.Worker.Brain;

public sealed class BrainSourceFiles
{
    public string Root { get; }
    public BrainSourceFiles(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Raiz de fontes deve ser absoluta.");
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        ValidateRoot();
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public async Task<SourceFile> PublishAsync(Stream bytes, CancellationToken ct = default)
    {
        ValidateRoot();
        var name = Guid.NewGuid().ToString("N");
        var temp = Path.Combine(Root, name + ".tmp");
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await bytes.CopyToAsync(file, ct);
                await file.FlushAsync(ct);
                file.Flush(true);
            }
            var reference = name + ".source";
            File.Move(temp, Path.Combine(Root, reference));
            return await DescribeAsync(reference, ct);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal string Resolve(string reference)
    {
        ValidateRoot();
        if (reference.Length != 39 || !reference.EndsWith(".source", StringComparison.Ordinal) ||
            !Guid.TryParseExact(reference[..32], "N", out _))
            throw new BrainStorageException(BrainStorageFailure.Integrity);
        var path = Path.Combine(Root, reference);
        if (new FileInfo(path).LinkTarget is not null) throw new BrainStorageException(BrainStorageFailure.Integrity);
        return path;
    }

    public async Task VerifyAsync(SourceFile original, CancellationToken ct = default)
    {
        try
        {
            if (original != await DescribeAsync(original.Reference, ct)) throw new BrainStorageException(BrainStorageFailure.Integrity);
        }
        catch (IOException) { throw new BrainStorageException(BrainStorageFailure.Integrity); }
        catch (UnauthorizedAccessException) { throw new BrainStorageException(BrainStorageFailure.Integrity); }
    }

    internal async Task<SourceFile> DescribeAsync(string reference, CancellationToken ct)
    {
        await using var file = new FileStream(Resolve(reference), FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(file, ct);
        return new(reference, Convert.ToHexStringLower(hash), file.Length);
    }

    private void ValidateRoot()
    {
        for (var directory = new DirectoryInfo(Root); directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null) throw new BrainStorageException(BrainStorageFailure.Integrity);
    }
}
