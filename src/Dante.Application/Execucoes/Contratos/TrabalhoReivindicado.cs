using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Posse de um trabalho reivindicado: só vale enquanto geração, dono e lease conferirem no banco.
public sealed record PosseDoTrabalho(Guid IdTrabalho, long Geracao, string Trabalhador, Guid Boot);

// Resultado de uma reivindicação. Tentativas já inclui esta.
public sealed record TrabalhoReivindicado(Guid Id, TipoDeTrabalho Tipo, Guid? IdExecucao, Guid? IdEtapa, string? Dados,
    string ChaveDeDeduplicacao, int Tentativas, int MaximoDeTentativas, long Geracao, DateTimeOffset LeaseAte,
    string Trabalhador, Guid Boot)
{
    public PosseDoTrabalho Posse => new(Id, Geracao, Trabalhador, Boot);
}
