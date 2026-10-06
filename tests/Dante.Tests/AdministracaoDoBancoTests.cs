using Dante.Infrastructure.Banco;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

[Collection("AdministracaoConsole")]
public sealed class AdministracaoDoBancoTests
{
    [PostgreSqlFact]
    public async Task MigracaoExplicitaEHealthDetectamMigrationPendenteEHistoricoDesconhecido()
    {
        await using var banco = await Banco.CriarAsync(migrar: false);
        await using var contexto = banco.Contexto();
        var admin = new AdministracaoDoBanco(contexto);
        Assert.Empty(await contexto.Database.GetAppliedMigrationsAsync());
        await admin.MigrarAsync();
        var migrations = contexto.Database.GetMigrations().ToArray();
        Assert.Equal(migrations, await contexto.Database.GetAppliedMigrationsAsync());
        await admin.MigrarAsync();
        Assert.Equal(migrations, await contexto.Database.GetAppliedMigrationsAsync());

        // Health precisa de conta runtime; a conta administrativa é recusada mesmo com schema completo.
        var role = "health_" + Guid.NewGuid().ToString("N");
        await contexto.Database.ExecuteSqlRawAsync($"CREATE ROLE {role} LOGIN; GRANT USAGE ON SCHEMA brain_meta TO {role}; GRANT SELECT ON ALL TABLES IN SCHEMA brain_meta TO {role}");
        try
        {
            var conexao = new Npgsql.NpgsqlConnectionStringBuilder(banco.ConnectionString) { Username = role };
            await using var runtime = Banco.Contexto(conexao.ConnectionString);
            var health = new AdministracaoDoBanco(runtime);
            Assert.True(await health.VerificarSaudeAsync());
            await contexto.GetService<IMigrator>().MigrateAsync(migrations[^2]);
            Assert.False(await health.VerificarSaudeAsync());
            await admin.MigrarAsync();
            Assert.True(await health.VerificarSaudeAsync());
            await contexto.Database.ExecuteSqlRawAsync("INSERT INTO brain_meta.\"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('99999999999999_Desconhecida', '10.0.6')");
            Assert.False(await health.VerificarSaudeAsync());
        }
        finally
        {
            await contexto.Database.ExecuteSqlRawAsync($"DROP OWNED BY {role}; DROP ROLE {role}");
        }
    }

    [Fact]
    public async Task FalhaAdministrativaNaoImprimeExceptionComCredenciais()
    {
        const string segredo = "Password=credencial-ficticia-201";
        var writer = new StringWriter();
        using var provider = new ServiceCollection().AddScoped<AdministracaoDoBanco>(_ =>
            throw new InvalidOperationException(segredo)).BuildServiceProvider();
        var anterior = Console.Error;
        try
        {
            Console.SetError(writer);
            Assert.Equal(1, await ComandosDoBanco.ExecutarAsync(provider, ["--brain", "migrate"]));
        }
        finally { Console.SetError(anterior); }
        Assert.Contains("Operação de banco falhou", writer.ToString());
        Assert.DoesNotContain(segredo, writer.ToString());
    }
}

[CollectionDefinition("AdministracaoConsole", DisableParallelization = true)]
public sealed class AdministracaoConsoleCollection;
