namespace Dante.Application.Execucoes;

// Detalhe de uma execução do proprietário (#305). O contexto aparece só como modo e aliases (nunca o diretório);
// objetivo, motivo e erro passam pela proteção de segredos e são cortados. A saída do agente não é persistida e por
// isso não existe aqui.
public sealed record DetalheDaExecucaoDto
{
    public ResumoDaExecucaoDto Resumo { get; init; } = new();
    public string ModoDoContexto { get; init; } = "";
    public string? AliasDoContexto { get; init; }
    public IReadOnlyList<string> AliasesVinculados { get; init; } = [];
    public string? Objetivo { get; init; }
    public string? Motivo { get; init; }
    public string? Erro { get; init; }
    public DateTimeOffset? IniciadaEm { get; init; }
    public DateTimeOffset? CancelamentoSolicitadoEm { get; init; }
    public string? MotivoDoCancelamento { get; init; }
    public IReadOnlyList<EtapaExibidaDto> Etapas { get; init; } = [];
}
