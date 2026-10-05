using Dante.Application.Comum;
using Dante.Application.Conhecimentos;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Dante.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace Dante.Tests;

// Identificadores SQL nestes testes são prefixo fixo + Guid N, nunca entrada externa.
#pragma warning disable EF1002
public sealed class BrainEfTests
{
    [PostgreSqlFact]
    public async Task HistoricoProvenienciaEscopoEConsultaSobrevivemAoRestart()
    {
        await using var banco = await Banco.CriarAsync();
        var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "Trabalho");
        var outro = new EspacoDeConhecimento(Guid.NewGuid(), "Pessoal");
        var projeto = new Projeto(espaco.Id, "Dante");
        var conhecimento = Novo(espaco.Id, projeto.Id);
        conhecimento.Confirmar(1, Origem(), DateTimeOffset.UtcNow);
        await using (var c = banco.Contexto())
        {
            c.AddRange(espaco, outro, projeto, conhecimento, Novo(outro.Id));
            await new UnitOfWork(c).SalvarAlteracoesAsync();
        }
        await using (var c = banco.Contexto())
        {
            var repo = new ConhecimentoRepository(c);
            var salvo = (await repo.ObterPorIdAsync(conhecimento.Id))!;
            Assert.Equal(conhecimento.IdAutor, salvo.IdAutor);
            Assert.Equal(2, salvo.Revisao);
            Assert.Equal("fonte", salvo.Historico[0].Proveniencia.ReferenciaDaFonte);
            Assert.Equal(StatusDoConhecimento.Confirmado, salvo.Status);
            Assert.Equal(new[] { "teste" }, salvo.Tags);
            Assert.Single(await repo.ListarDoEspacoAsync(new() { IdEspacoDeConhecimento = espaco.Id, Tag = "teste", ValidoEm = DateTimeOffset.UtcNow }));
            Assert.Empty(await repo.ListarDoEspacoAsync(new() { IdEspacoDeConhecimento = espaco.Id, SomenteSemProjeto = true }));
            salvo.Corrigir(2, TipoDeConhecimento.Fato, "corrigido", null, null, Sensibilidade.Pessoal, null, null, [], Origem(), DateTimeOffset.UtcNow);
            repo.Atualizar(salvo);
            await new UnitOfWork(c).SalvarAlteracoesAsync();
        }
        await using (var c = banco.Contexto())
        {
            var salvo = (await new ConhecimentoRepository(c).ObterPorIdAsync(conhecimento.Id))!;
            Assert.Equal(3, salvo.Revisao);
            Assert.Equal("conteúdo", salvo.Historico[0].Conteudo);
            Assert.Equal("corrigido", salvo.Conteudo);
        }
    }

    [PostgreSqlFact]
    public async Task ForeignKeysImpedemOrfaosEProjetoDeOutroEspacoComRollback()
    {
        await using var banco = await Banco.CriarAsync();
        var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "A");
        var outro = new EspacoDeConhecimento(Guid.NewGuid(), "B");
        var projeto = new Projeto(outro.Id, "Projeto");
        await using (var c = banco.Contexto()) { c.AddRange(espaco, outro, projeto); await c.SaveChangesAsync(); }
        await using (var c = banco.Contexto())
        {
            c.AddRange(Novo(espaco.Id), Novo(espaco.Id, projeto.Id));
            await Assert.ThrowsAsync<DbUpdateException>(() => new UnitOfWork(c).SalvarAlteracoesAsync());
        }
        await using (var c = banco.Contexto())
        {
            Assert.Empty(await c.Conhecimentos.ToListAsync());
            c.Add(Novo(Guid.NewGuid()));
            await Assert.ThrowsAsync<DbUpdateException>(() => c.SaveChangesAsync());
        }
    }

    [PostgreSqlFact]
    public async Task ConcorrenciaPreservaRevisoesETipoDeErroDaApplication()
    {
        await using var banco = await Banco.CriarAsync();
        var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "A");
        var conhecimento = Novo(espaco.Id);
        await using (var c = banco.Contexto()) { c.AddRange(espaco, conhecimento); await c.SaveChangesAsync(); }
        await using var primeiro = banco.Contexto();
        await using var segundo = banco.Contexto();
        var a = (await primeiro.Conhecimentos.FindAsync(conhecimento.Id))!;
        var b = (await segundo.Conhecimentos.FindAsync(conhecimento.Id))!;
        a.Confirmar(1, Origem(), DateTimeOffset.UtcNow);
        await new UnitOfWork(primeiro).SalvarAlteracoesAsync();
        b.Invalidar(1, Origem(), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<ConflitoDeConcorrenciaException>(() => new UnitOfWork(segundo).SalvarAlteracoesAsync());
        await using var verificar = banco.Contexto();
        Assert.Equal(StatusDoConhecimento.Confirmado, (await verificar.Conhecimentos.FindAsync(conhecimento.Id))!.Status);
    }

    [PostgreSqlFact]
    public async Task BackupRestoreExigeDestinoVazioEPreservaCanonico()
    {
        await using var banco = await Banco.CriarAsync();
        await using var destino = await Banco.CriarAsync(migrar: false);
        var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "A");
        var conhecimento = Novo(espaco.Id);
        var caminho = Path.Combine(Path.GetTempPath(), "brain-backup-" + Guid.NewGuid().ToString("N") + ".dump");
        try
        {
            await using var origem = banco.Contexto();
            origem.AddRange(espaco, conhecimento); await origem.SaveChangesAsync();
            var admin = new AdministracaoDoBanco(origem);
            await admin.BackupAsync(caminho);
            await Assert.ThrowsAsync<InvalidOperationException>(() => admin.BackupAsync(caminho));
            await Assert.ThrowsAsync<InvalidOperationException>(() => admin.RestaurarAsync(caminho));
            await using var restaurado = destino.Contexto();
            await new AdministracaoDoBanco(restaurado).RestaurarAsync(caminho);
            var salvo = (await restaurado.Conhecimentos.FindAsync(conhecimento.Id))!;
            Assert.Equal("conteúdo", salvo.Conteudo);
            Assert.Single(salvo.Historico);
            Assert.Empty(await restaurado.Database.GetPendingMigrationsAsync());
        }
        finally { File.Delete(caminho); }
    }

    [PostgreSqlFact]
    public async Task HealthRecusaAdminEAceitaRuntimeComGrantsMinimos()
    {
        await using var banco = await Banco.CriarAsync();
        await using var c = banco.Contexto();
        Assert.False(await new AdministracaoDoBanco(c).VerificarSaudeAsync());
        var role = "runtime_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(Environment.GetEnvironmentVariable("DANTE_TEST_POSTGRES"));
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE ROLE {role} LOGIN", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            await c.Database.ExecuteSqlRawAsync($"GRANT USAGE ON SCHEMA brain_data, brain_meta TO {role}; GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA brain_data TO {role}; GRANT SELECT ON ALL TABLES IN SCHEMA brain_meta TO {role}");
            var config = new NpgsqlConnectionStringBuilder(banco.ConnectionString) { Username = role };
            await using var runtime = Banco.Contexto(config.ConnectionString);
            Assert.True(await new AdministracaoDoBanco(runtime).VerificarSaudeAsync());
            runtime.Add(new EspacoDeConhecimento(Guid.NewGuid(), "mínimo")); await runtime.SaveChangesAsync();
            await Assert.ThrowsAsync<PostgresException>(() => runtime.Database.ExecuteSqlRawAsync("CREATE TABLE brain_data.proibida(id int)"));
        }
        finally
        {
            await c.Database.ExecuteSqlRawAsync($"DROP OWNED BY {role}");
            await using var drop = new NpgsqlCommand($"DROP ROLE {role}", admin); await drop.ExecuteNonQueryAsync();
        }
    }

    internal static ProvenienciaDoConhecimento Origem() => new(Guid.NewGuid(), "usuário", "fonte", "v1", "evidência");
    internal static Conhecimento Novo(Guid espaco, Guid? projeto = null) => new(espaco, projeto, TipoDeConhecimento.Fato,
        "conteúdo", null, StatusDoConhecimento.Inferido, null, Sensibilidade.Pessoal, null, null, ["teste"], Origem(), DateTimeOffset.UtcNow);
}

internal sealed class Banco : IAsyncDisposable
{
    private readonly string nome = "brain_test_" + Guid.NewGuid().ToString("N");
    public string ConnectionString { get; private set; } = "";
    public static async Task<Banco> CriarAsync(bool migrar = true)
    {
        var banco = new Banco();
        await using var admin = new NpgsqlConnection(Environment.GetEnvironmentVariable("DANTE_TEST_POSTGRES"));
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE {banco.nome}", admin); await create.ExecuteNonQueryAsync();
        banco.ConnectionString = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = banco.nome, Pooling = false }.ConnectionString;
        if (migrar) { await using var c = banco.Contexto(); await c.Database.MigrateAsync(); }
        return banco;
    }
    public DanteDbContext Contexto() => Contexto(ConnectionString);
    public static DanteDbContext Contexto(string connectionString) => new(new DbContextOptionsBuilder<DanteDbContext>().UseNpgsql(connectionString,
        pg => pg.MigrationsHistoryTable("__EFMigrationsHistory", "brain_meta")).Options);
    public async ValueTask DisposeAsync()
    {
        await using var admin = new NpgsqlConnection(Environment.GetEnvironmentVariable("DANTE_TEST_POSTGRES")); await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE {nome} WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync();
    }
}
