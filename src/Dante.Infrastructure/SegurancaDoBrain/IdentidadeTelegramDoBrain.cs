using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dante.Application.SegurancaDoBrain;
using Microsoft.Extensions.Configuration;
namespace Dante.Infrastructure.SegurancaDoBrain;

public sealed class IdentidadeTelegramDoBrain(IConfiguration configuration)
{
    public Guid IdTenantConfigurado
    {
        get
        {
            var valor=configuration["DANTE_BRAIN_TENANT"];
            if(valor is null)return AutorizacaoDoBrain.TenantLocal;
            if(!Guid.TryParse(valor,out var tenant)||tenant==Guid.Empty)throw new UnauthorizedAccessException("Tenant inválido.");
            return tenant;
        }
    }
    public IdentidadeDoBrain Resolver(long idTelegram)
    {
        var entradas = configuration["Telegram:AllowedUserIds"]?.Split(',') ?? [];
        var ids = new List<long>();
        foreach (var entrada in entradas)
        {
            if (!long.TryParse(entrada.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new UnauthorizedAccessException("Identidade Telegram não autorizada.");
            ids.Add(id);
        }
        if (!ids.Contains(idTelegram)) throw new UnauthorizedAccessException("Identidade Telegram não autorizada.");
        var tenant = IdTenantConfigurado;
        // Identidade estável após reinício, independente de nome/display name e do agente escolhido.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"dante:telegram:{tenant:D}:{idTelegram.ToString(CultureInfo.InvariantCulture)}"));
        return new(tenant, new Guid(hash.AsSpan(0, 16)));
    }
}
