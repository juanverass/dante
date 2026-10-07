using System.Text;
using System.Text.Json;
using Dante.Application.Planilhas;

namespace Dante.Infrastructure.Planilhas;

// Auditoria local e só de acréscimo das escritas em planilhas (#224): uma célula por linha JSON em
// ~/.dante/planilhas/auditoria.jsonl (0600), com conta lógica e nunca token.
public sealed class AuditoriaDePlanilhasEmArquivo(string diretorio) : IAuditoriaDePlanilhas
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public string Caminho => Path.Combine(diretorio, "auditoria.jsonl");

    public async Task RegistrarAsync(RegistroDeAuditoriaDePlanilha registro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registro);
        var linha = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(registro) + "\n");
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var arquivoLock = await LockDeArquivo.AdquirirAsync(Caminho + ".lock", cancellationToken);
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(diretorio);
            else Directory.CreateDirectory(diretorio, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var stream = new FileStream(Caminho, options);
            await stream.WriteAsync(linha, cancellationToken);
        }
        finally { gate.Release(); }
    }
}
