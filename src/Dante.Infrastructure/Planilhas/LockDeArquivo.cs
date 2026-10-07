namespace Dante.Infrastructure.Planilhas;

internal static class LockDeArquivo
{
    public static async Task<FileStream> AdquirirAsync(string caminho, CancellationToken cancellationToken)
    {
        var diretorio = Path.GetDirectoryName(caminho)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(diretorio);
        else Directory.CreateDirectory(diretorio, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var prazo = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(caminho, options); }
            catch (IOException) when (File.Exists(caminho) && prazo.Elapsed < TimeSpan.FromSeconds(30))
            { await Task.Delay(25, cancellationToken); }
        }
    }
}
