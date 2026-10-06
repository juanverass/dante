namespace Dante.Application.Planilhas;

// Falha acionável e segura para exibir: a mensagem nunca contém token, segredo ou URL de requisição. Candidatos traz
// as células plausíveis quando o motivo é Ambigua.
public sealed class FalhaDePlanilhaException(MotivoDaFalhaDePlanilha motivo, string message,
    IReadOnlyList<OcorrenciaNaPlanilhaDto>? candidatos = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public MotivoDaFalhaDePlanilha Motivo { get; } = motivo;
    public IReadOnlyList<OcorrenciaNaPlanilhaDto> Candidatos { get; } = candidatos ?? [];
}
