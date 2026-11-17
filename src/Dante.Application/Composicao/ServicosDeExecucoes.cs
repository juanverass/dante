using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dante.Application;

// Ledger de execuções (AD-66). Como no Brain, o caso de uso é sempre registrado; as portas do ledger só existem
// com banco configurado, e o host decide usá-lo pela chave Orquestracao:Habilitada.
internal static class ServicosDeExecucoes
{
    internal static void Registrar(IServiceCollection services)
    {
        services.TryAddScoped<Dante.Application.Execucoes.ILedgerDeExecucoesAppService>(provider =>
            ActivatorUtilities.CreateInstance<Dante.Application.Execucoes.LedgerDeExecucoesAppService>(provider));
        // Fila durável e recuperação (AD-67).
        services.TryAddScoped<Dante.Application.Execucoes.IFilaDeExecucoesAppService>(provider =>
            ActivatorUtilities.CreateInstance<Dante.Application.Execucoes.FilaDeExecucoesAppService>(provider));
        services.TryAddScoped<Dante.Application.Execucoes.IRecuperacaoDeExecucoesAppService>(provider =>
            ActivatorUtilities.CreateInstance<Dante.Application.Execucoes.RecuperacaoDeExecucoesAppService>(provider));
        // Acompanhamento e controle pelo proprietário (#305), neutros de interface. O canal de cancelamento é o atalho
        // em memória entre o caso de uso e quem tem a posse neste processo; o pedido durável é o evento no ledger.
        services.TryAddSingleton<Dante.Application.Execucoes.CanalDeCancelamento>();
        services.TryAddScoped<Dante.Application.Execucoes.IAcompanhamentoDeExecucoesAppService>(provider =>
            ActivatorUtilities.CreateInstance<Dante.Application.Execucoes.AcompanhamentoDeExecucoesAppService>(provider));
        services.TryAddScoped<Dante.Application.Execucoes.IControleDeExecucoesAppService>(provider =>
            ActivatorUtilities.CreateInstance<Dante.Application.Execucoes.ControleDeExecucoesAppService>(provider));
    }
}
