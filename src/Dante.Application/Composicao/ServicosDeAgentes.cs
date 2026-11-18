using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dante.Application;

// Cadastro de agentes (AD-68) e de grupos (AD-69). Como o ledger, os casos de uso são sempre registrados; as portas
// só existem com banco, e o host decide usá-los pela chave Orquestracao:Habilitada.
internal static class ServicosDeAgentes
{
    internal static void Registrar(IServiceCollection services)
    {
        services.TryAddScoped<Dante.Application.Agentes.ICadastroDeAgentesAppService>(provider =>
            ActivatorUtilities.CreateInstance<Dante.Application.Agentes.CadastroDeAgentesAppService>(provider));
        services.TryAddScoped<Dante.Application.Grupos.ICadastroDeGruposAppService>(provider =>
            ActivatorUtilities.CreateInstance<Dante.Application.Grupos.CadastroDeGruposAppService>(provider));
    }
}
