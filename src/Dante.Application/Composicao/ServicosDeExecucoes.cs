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
        // Worktrees de execução de escrita (#307, AD-71): depende de OpcoesDeWorktrees e do gerenciador Git, compostos
        // pelo host junto com a fila.
        services.TryAddScoped<Dante.Application.Execucoes.IReservasDeWorktreeAppService>(provider =>
            ActivatorUtilities.CreateInstance<Dante.Application.Execucoes.ReservasDeWorktreeAppService>(provider));
        // Roteiro de grupo (#308): snapshot, progressão fixa e gate humano persistido.
        services.TryAddScoped<Dante.Application.Execucoes.IRoteirosAppService>(provider =>
            ActivatorUtilities.CreateInstance<Dante.Application.Execucoes.RoteirosAppService>(provider));
        // Delegação rastreada (#309, AD-70): solicitação serializada por raiz, ciclo da subordinada e retomada da origem.
        services.TryAddScoped<Dante.Application.Execucoes.IDelegacoesAppService>(provider =>
            ActivatorUtilities.CreateInstance<Dante.Application.Execucoes.DelegacoesAppService>(provider));
    }
}
