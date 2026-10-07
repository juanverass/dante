using Dante.Application.Conhecimentos;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
namespace Dante.Application.CapturaDeConhecimento;

public sealed record AtoDoCandidatoDto(int Revisao, string Acao, TipoDeConhecimento Tipo, string Conteudo,
    Sensibilidade Sensibilidade, string Justificativa, ProvenienciaDto Proveniencia,
    DateTimeOffset Instante, EstadoDoCandidato Estado, Guid? IdConhecimento, string? Titulo = null, IReadOnlyList<string>? Tags = null);
