using System.Text.RegularExpressions;
using Dante.Application.Execucoes;

namespace Dante.Infrastructure.Agentes;

// Listagem e parada de scopes dante-agent-* do systemd de usuário para a recuperação na inicialização (AD-67).
// Só nomes no formato exato criado por ContencaoSystemd (dante-agent-<guid sem hífens>.scope) passam; qualquer outro é
// recusado antes de chamar o gerenciador. Quem decide o que parar é a Application, comparando com o ledger.
public sealed partial class UnidadesDeContencaoSystemd : IUnidadesDeContencao
{
    public async Task<IReadOnlyList<string>> ListarAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) return [];
        var saida = await ContencaoSystemd.ExecutarControleAsync(["list-units", "--type=scope", "--all", "--plain",
            "--no-legend", "--no-pager", "dante-agent-*.scope"], cancellationToken);
        return Interpretar(saida);
    }

    public async Task PararAsync(string unidade, CancellationToken cancellationToken = default)
    {
        if (!NomeValido(unidade)) throw new ArgumentException("Unidade fora do formato do D.A.N.T.E.; não será parada.", nameof(unidade));
        await ContencaoSystemd.ExecutarControleAsync(["stop", unidade], cancellationToken);
    }

    internal static bool NomeValido(string? unidade) => unidade is not null && Formato().IsMatch(unidade);

    // Primeira coluna de cada linha; o que não tem o formato exato é ignorado.
    internal static IReadOnlyList<string> Interpretar(string saida) => saida
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(linha => linha.TrimStart('●', '*', ' ').Split(' ', 2)[0])
        .Where(NomeValido).Distinct(StringComparer.Ordinal).ToArray();

    [GeneratedRegex("^dante-agent-[0-9a-f]{32}\\.scope$", RegexOptions.CultureInvariant)]
    private static partial Regex Formato();
}
