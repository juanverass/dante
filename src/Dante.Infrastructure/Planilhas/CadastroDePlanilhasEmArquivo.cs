using System.Text.Json;
using Dante.Application.Planilhas;

namespace Dante.Infrastructure.Planilhas;

// Cadastro local de planilhas autorizadas (#224) em ~/.dante/planilhas/cadastro.json (0600), lido a cada operação
// porque o Worker e os servidores MCP das sessões compartilham o arquivo. Gravação atômica por arquivo temporário.
public sealed class CadastroDePlanilhasEmArquivo(string diretorio) : ICadastroDePlanilhas
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);

    public string Caminho => Path.Combine(diretorio, "cadastro.json");

    public async Task<IReadOnlyList<PlanilhaCadastradaDto>> ListarAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var arquivoLock = await LockDeArquivo.AdquirirAsync(Caminho + ".lock", cancellationToken);
            return await LerAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<PlanilhaCadastradaDto?> ObterAsync(string alias, CancellationToken cancellationToken = default) =>
        (await ListarAsync(cancellationToken)).FirstOrDefault(p => string.Equals(p.Alias, alias, StringComparison.OrdinalIgnoreCase));

    public async Task SalvarAsync(PlanilhaCadastradaDto planilha, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planilha);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var arquivoLock = await LockDeArquivo.AdquirirAsync(Caminho + ".lock", cancellationToken);
            var planilhas = (await LerAsync(cancellationToken))
                .Where(p => !string.Equals(p.Alias, planilha.Alias, StringComparison.OrdinalIgnoreCase))
                .Append(planilha).OrderBy(p => p.Alias, StringComparer.Ordinal).ToArray();
            await GravarAsync(planilhas, cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> RemoverAsync(string alias, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var arquivoLock = await LockDeArquivo.AdquirirAsync(Caminho + ".lock", cancellationToken);
            var planilhas = await LerAsync(cancellationToken);
            var restantes = planilhas.Where(p => !string.Equals(p.Alias, alias, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (restantes.Length == planilhas.Count) return false;
            await GravarAsync(restantes, cancellationToken);
            return true;
        }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyList<PlanilhaCadastradaDto>> LerAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Caminho)) return [];
        await using var stream = File.OpenRead(Caminho);
        return await JsonSerializer.DeserializeAsync<List<PlanilhaCadastradaDto>>(stream, Json, cancellationToken)
               ?? throw new InvalidDataException("O cadastro de planilhas está vazio ou inválido.");
    }

    private async Task GravarAsync(IReadOnlyList<PlanilhaCadastradaDto> planilhas, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(diretorio);
        else Directory.CreateDirectory(diretorio, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporario = Caminho + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var stream = new FileStream(temporario, options))
            await JsonSerializer.SerializeAsync(stream, planilhas, Json, cancellationToken);
        File.Move(temporario, Caminho, overwrite: true);
    }
}
