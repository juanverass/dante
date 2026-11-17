using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// A execução e a etapa que um trabalho AvancarExecucao avança, no momento da avaliação.
public sealed record AlvoDoTrabalhoDto(Guid IdExecucao, long NumeroDaExecucao, string Codigo, TipoDeExecucao Tipo,
    EstadoDaExecucao Estado, string Canal, long IdDoUsuarioNoCanal, Guid IdEtapa, int NumeroDaEtapa,
    EstadoDaEtapa EstadoDaEtapa, bool Escrita, PoliticaDeRetomada PoliticaDeRetomada, int Tentativa);
