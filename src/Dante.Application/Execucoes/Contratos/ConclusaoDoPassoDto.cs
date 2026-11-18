using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Fim de um passo de agente do roteiro. A revisão concluída traz o veredito estruturado (registrado pelo caso de uso
// confiável, nunca lido de texto) e o hash do artefato revisado (o diff), ao qual o gate se vincula.
public sealed record ConclusaoDoPassoDto(ResultadoDaEtapaDto Resultado, VereditoDeRevisao? Veredito = null,
    string? HashDoArtefato = null);
