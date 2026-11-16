using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dante.Application;

// Ledger de execuções (AD-66). Como no Brain, o caso de uso é sempre registrado; as portas do ledger só existem
// com banco configurado, e o host decide usá-lo pela chave Orquestracao:Habilitada.
internal static class ServicosDeExecucoes
{
    internal static void Registrar(IServiceCollection services) =>
        services.TryAddScoped<Dante.Application.Execucoes.ILedgerDeExecucoesAppService>(provider =>
            ActivatorUtilities.CreateInstance<Dante.Application.Execucoes.LedgerDeExecucoesAppService>(provider));
}
