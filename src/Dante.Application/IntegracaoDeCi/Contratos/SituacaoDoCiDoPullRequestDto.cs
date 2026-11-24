namespace Dante.Application.IntegracaoDeCi;

// Resposta neutra da consulta do CI de um PR (#238), para a interface formatar. Exatamente um caminho é preenchido:
// ProblemaDoRepositorio (não houve o que consultar), FalhaDoPullRequest (o PR não pôde ser lido: conexão ausente, sem
// permissão, inexistente, indisponível ou resposta que não confere) ou PullRequest com o Check do head SHA atual. Check
// SemPermissao ou Indisponivel diz que o D.A.N.T.E. não sabe o resultado, nunca que o CI falhou.
public sealed record SituacaoDoCiDoPullRequestDto
{
    public required int NumeroDoPr { get; init; }
    public string? Alias { get; init; }
    public OrigemDoRepositorioDeCi? Origem { get; init; }
    public ProblemaDoRepositorioDeCi? ProblemaDoRepositorio { get; init; }
    public MotivoDaFalhaNaConsultaDeCi? FalhaDoPullRequest { get; init; }
    public PullRequestDeCiDto? PullRequest { get; init; }
    public CheckAgregadoDeCiDto? Check { get; init; }
    // Quando o provedor limitou a taxa, a partir de quando vale tentar de novo.
    public TimeSpan? RepetirApos { get; init; }
}
