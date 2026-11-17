using Dante.Domain.Execucoes;

namespace Dante.Application.Agentes;

// Etapa que uma execução nova vai ter, antes de existir no ledger.
public sealed record EtapaPlanejada(int Numero, TipoDeEtapa Tipo, bool Escrita, PoliticaDeRetomada PoliticaDeRetomada);
