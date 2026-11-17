using System.Text.Json;
using Dante.Application.SegurancaDoBrain;

namespace Dante.Application.Execucoes;

// Dados do evento CancelamentoSolicitado (#305): autor (canal:usuário) e motivo, nunca outro conteúdo. O motivo vem
// de quem pede: é cortado, vira uma linha e é substituído quando parece segredo.
internal static class PedidoDeCancelamento
{
    public const int TamanhoMaximoDoMotivo = 200;

    public static string? MotivoSeguro(string? motivo)
    {
        if (string.IsNullOrWhiteSpace(motivo)) return null;
        var linha = string.Join(' ', motivo.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (linha.Length > TamanhoMaximoDoMotivo) linha = linha[..TamanhoMaximoDoMotivo];
        return ProtecaoDeSegredos.Redigir(linha);
    }

    public static string Dados(ProprietarioDaExecucao autor, string? motivoSeguro) =>
        JsonSerializer.Serialize(new Dictionary<string, string?> { ["autor"] = autor.Autor, ["motivo"] = motivoSeguro });

    public static string? Motivo(string? dados)
    {
        if (dados is null) return null;
        try
        {
            using var json = JsonDocument.Parse(dados);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                   json.RootElement.TryGetProperty("motivo", out var motivo) && motivo.ValueKind == JsonValueKind.String
                ? motivo.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Motivo gravado na execução e na etapa canceladas.
    public static string MotivoDaTransicao(string? motivoSeguro) =>
        motivoSeguro is null ? "Cancelada pelo dono." : $"Cancelada pelo dono: {motivoSeguro}";
}
