using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dante.Infrastructure.Data.Orquestracao;

public sealed class OrquestracaoDbContextFactory : IDesignTimeDbContextFactory<OrquestracaoDbContext>
{
    public OrquestracaoDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Dante");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Configure ConnectionStrings__Dante fora do repositório.");
        return new OrquestracaoDbContext(Opcoes(connectionString));
    }

    // Única definição das opções do ledger: DI, design-time e testes usam o mesmo histórico de migrations.
    internal static DbContextOptions<OrquestracaoDbContext> Opcoes(string connectionString) =>
        new DbContextOptionsBuilder<OrquestracaoDbContext>().UseNpgsql(connectionString, provider => provider.MigrationsHistoryTable(
            OrquestracaoDbContext.TabelaDeHistorico, OrquestracaoDbContext.Schema)).Options;
}
