using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dante.Application;

// Cadastro de agentes (AD-68). Como o ledger, o caso de uso é sempre registrado; as portas só existem com banco, e o
// host decide usá-lo pela chave Orquestracao:Habilitada.
internal static class ServicosDeAgentes
{
    internal static void Registrar(IServiceCollection services) =>
        services.TryAddScoped<Dante.Application.Agentes.ICadastroDeAgentesAppService>(provider =>
            ActivatorUtilities.CreateInstance<Dante.Application.Agentes.CadastroDeAgentesAppService>(provider));
}
