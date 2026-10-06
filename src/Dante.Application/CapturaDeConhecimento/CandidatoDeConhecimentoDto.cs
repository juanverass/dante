using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
namespace Dante.Application.CapturaDeConhecimento;

public sealed record CandidatoDeConhecimentoDto(Guid Id, Guid IdEspacoDeConhecimento, Guid? IdProjeto,
    TipoDeConhecimento Tipo, string Conteudo, Sensibilidade Sensibilidade, NaturezaDoConteudo Natureza,
    ModoDeCaptura Modo, EstadoDoCandidato Estado, int Revisao, Guid? IdConhecimento, IReadOnlyList<AtoDoCandidatoDto> Historico);
