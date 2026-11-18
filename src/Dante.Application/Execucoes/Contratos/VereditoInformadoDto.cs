using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Veredito como o revisor o entrega pela ferramenta: estruturado, sem texto livre interpretado como estado.
public sealed record VereditoInformadoDto(VereditoDeRevisao Veredito, string Resumo, IReadOnlyList<string> Itens);

// Registrado agora ou já registrado antes com o mesmo conteúdo (entrega repetida é idempotente).
public sealed record RegistroDeVereditoDto(VereditoDeRevisao Veredito, bool JaRegistrado);

// Leitura do revisor pelo dante_coordenacao (estado_da_execucao): só fatos da própria execução.
public sealed record EstadoDaRevisaoDto(string Codigo, string Grupo, int Rodada, int RodadasDeRevisaoMaximas,
    string HashDoArtefato, IReadOnlyList<VereditoAnteriorDto> VereditosAnteriores, string? InstrucaoHumana);

public sealed record VereditoAnteriorDto(int Rodada, VereditoDeRevisao Veredito, string Resumo, IReadOnlyList<string> Itens);
