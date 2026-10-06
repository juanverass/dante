namespace Dante.Application.RelacoesDeConhecimento;

public sealed record VizinhancaDto(IReadOnlyList<RelacaoDeConhecimentoDto> Relacoes, bool LimiteAtingido);
