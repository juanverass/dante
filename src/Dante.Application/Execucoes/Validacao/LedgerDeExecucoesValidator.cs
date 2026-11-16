using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Validação de entrada do ledger: só olha os valores recebidos (AD-52). Dados de evento: até 16 pares de até 200
// caracteres; só os tipos de evento que pertencem a uma sessão.
internal static class LedgerDeExecucoesValidator
{
    internal static void ValidarNumero(long numero)
    {
        if (numero < 1) throw new ArgumentOutOfRangeException(nameof(numero), "Número inválido.");
    }

    internal static void Validar(JobRegistradoDto job)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(job.Contexto, nameof(job.Contexto));
        ValidarNumero(job.Numero);
    }

    internal static void Validar(JobEncerradoDto fim)
    {
        ArgumentNullException.ThrowIfNull(fim);
        ValidarNumero(fim.Numero);
        if (fim.EstadoFinal is not (EstadoDaExecucao.Concluida or EstadoDaExecucao.Falhou or EstadoDaExecucao.Cancelada))
            throw new ArgumentOutOfRangeException(nameof(fim.EstadoFinal), "Estado final de job inválido.");
    }

    internal static void Validar(SessaoRegistradaDto sessao)
    {
        ArgumentNullException.ThrowIfNull(sessao);
        ArgumentNullException.ThrowIfNull(sessao.Contexto, nameof(sessao.Contexto));
        ValidarNumero(sessao.Numero);
    }

    internal static void Validar(SessaoEncerradaDto fim)
    {
        ArgumentNullException.ThrowIfNull(fim);
        ValidarNumero(fim.Numero);
        if (fim.EstadoFinal is not (EstadoDaSessaoDeAgente.Encerrada or EstadoDaSessaoDeAgente.Falhou or
            EstadoDaSessaoDeAgente.Interrompida))
            throw new ArgumentOutOfRangeException(nameof(fim.EstadoFinal), "Estado final de sessão inválido.");
    }

    internal static void Validar(EventoDeSessaoDto evento)
    {
        ArgumentNullException.ThrowIfNull(evento);
        ValidarNumero(evento.NumeroDaSessao);
        if (evento.Tipo is not (TipoDeEventoDeOrquestracao.TurnoIniciado or TipoDeEventoDeOrquestracao.TurnoConcluido or
            TipoDeEventoDeOrquestracao.FerramentaIniciada or TipoDeEventoDeOrquestracao.FerramentaConcluida or
            TipoDeEventoDeOrquestracao.AprovacaoSolicitada or TipoDeEventoDeOrquestracao.EntradaSolicitada or
            TipoDeEventoDeOrquestracao.SolicitacaoResolvida or TipoDeEventoDeOrquestracao.SolicitacaoEncerrada))
            throw new ArgumentOutOfRangeException(nameof(evento.Tipo), "Tipo de evento não pertence a uma sessão.");
        if (evento.Dados is null) return;
        if (evento.Dados.Count > 16)
            throw new ArgumentException("Dados do evento excedem o limite.", nameof(evento.Dados));
        if (evento.Dados.Any(par => string.IsNullOrWhiteSpace(par.Key) || par.Key.Length > 200 ||
                                    par.Value is { Length: > 200 }))
            throw new ArgumentException("Dado do evento inválido.", nameof(evento.Dados));
    }
}
