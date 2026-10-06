using Dante.Domain.Conhecimentos;
using Dante.Domain.ContextosDeTrabalho;
namespace Dante.Application.ContextosDeTrabalho;

public sealed record ContextoDeTrabalhoDto(Guid Id, Guid IdEspacoDeConhecimento, Guid? IdProjeto, DadosDoContexto Dados,
    Sensibilidade Sensibilidade, int Revisao, Guid IdResponsavel, string Origem, DateTimeOffset AtualizadoEm,
    DateTimeOffset? ExpiraEm, AuditoriaDoContexto? AuditoriaAnterior);
