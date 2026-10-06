using Dante.Application.SegurancaDoBrain;
using Microsoft.Extensions.DependencyInjection;
namespace Dante.Tests;

internal static class EscoposBrainDeTeste
{
    public static IServiceScope Criar(IServiceProvider provider, Guid usuario, Guid espaco, Guid? projeto = null)
    {
        var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AutorizacaoDoBrain>().Estabelecer(
            new(AutorizacaoDoBrain.TenantLocal, usuario), new(usuario, espaco, projeto, true, true));
        return scope;
    }
}
