using Dante.Application.Comum;
using Dante.Infrastructure.Banco;
using Dante.Infrastructure.Data;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Infrastructure.Composicao;

internal static class DataServiceCollectionExtensions
{
    internal static void AddBanco(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<DanteDbContext>(options => options.UseNpgsql(connectionString,
            provider => provider.MigrationsHistoryTable("__EFMigrationsHistory", "brain_meta")));
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<AdministracaoDoBanco>();
    }
}
