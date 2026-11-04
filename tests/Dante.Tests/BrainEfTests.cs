using Dante.Domain.CapturaDeConhecimento;
using Dante.Application.Comum;
using Dante.Application.Conhecimentos;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Dante.Infrastructure.Banco;
using Dante.Infrastructure.Data;
using Dante.Infrastructure.Modulos.Conhecimentos;
using Dante.Infrastructure.Persistence;
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
            Assert.Single(await repo.ListarDoEspacoAsync(new() { IdEspacoDeConhecimento = espaco.Id, Tag = "TESTE", ValidoEm = DateTimeOffset.UtcNow }));
            Assert.Empty(await repo.ListarDoEspacoAsync(new() { IdEspacoDeConhecimento = espaco.Id, SomenteSemProjeto = true }));
            salvo.Corrigir(2, TipoDeConhecimento.Fato, "corrigido", null, null, Sensibilidade.Pessoal, null, null, [], Origem(), DateTimeOffset.UtcNow, NaturezaDoConteudo.DitoPeloUsuario);
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
        await using var role = await RoleDeTeste.CriarAsync(banco, "runtime");
        await c.Database.ExecuteSqlRawAsync($"GRANT USAGE ON SCHEMA brain_data, brain_meta TO {role.Nome}; GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA brain_data TO {role.Nome}; GRANT SELECT ON ALL TABLES IN SCHEMA brain_meta TO {role.Nome}");
        await using var runtime = Banco.Contexto(role.ConnectionString);
        Assert.True(await new AdministracaoDoBanco(runtime).VerificarSaudeAsync());
        runtime.Add(new EspacoDeConhecimento(Guid.NewGuid(), "mínimo")); await runtime.SaveChangesAsync();
        await Assert.ThrowsAsync<PostgresException>(() => runtime.Database.ExecuteSqlRawAsync("CREATE TABLE brain_data.proibida(id int)"));
    }

    [PostgreSqlFact]
    public async Task HostsExecutamMigrateBackupRestoreComCaminhoPosicional()
    {
        await using var origem = await Banco.CriarAsync(migrar: false);
        await using var destino = await Banco.CriarAsync(migrar: false);
        var caminho = Path.Combine(Path.GetTempPath(), "backup brain " + Guid.NewGuid().ToString("N") + ".dump");
        try
        {
            Assert.Equal(0, await ExecutarHostAsync(typeof(Dante.Worker.Worker).Assembly.Location, origem.ConnectionString, "--brain", "migrate"));
            var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "CLI");
            await using (var c = origem.Contexto()) { c.Add(espaco); await c.SaveChangesAsync(); }
            Assert.Equal(0, await ExecutarHostAsync(typeof(Dante.Worker.Worker).Assembly.Location, origem.ConnectionString, "--brain", "backup", caminho));
            Assert.Equal(0, await ExecutarHostAsync(typeof(Dante.WebApi.Program).Assembly.Location, destino.ConnectionString, "--brain", "restore", caminho));
            await using var verificar = destino.Contexto(); Assert.NotNull(await verificar.EspacosDeConhecimento.FindAsync(espaco.Id));
            Assert.Equal(1, await ExecutarHostAsync(typeof(Dante.WebApi.Program).Assembly.Location, destino.ConnectionString, "--brain", "health")); // Conta admin é recusada pelo health.
            Assert.Equal(1, await ExecutarHostAsync(typeof(Dante.Worker.Worker).Assembly.Location, origem.ConnectionString, "--brain", "invalido"));
        }
        finally { File.Delete(caminho); }
    }
    private static async Task<int> ExecutarHostAsync(string assembly, string connection, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        // Project references copiam o host, mas não seus assemblies de shared framework para o output do testhost.
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig"); start.ArgumentList.Add(Path.ChangeExtension(typeof(BrainEfTests).Assembly.Location, ".runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile"); start.ArgumentList.Add(Path.ChangeExtension(typeof(BrainEfTests).Assembly.Location, ".deps.json"));
        start.ArgumentList.Add(assembly); foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["ConnectionStrings__Dante"] = connection;
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(output, error); return process.ExitCode; }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    // #258: preparação vetorial repetível e pendências de indexação limitadas pela política de sensibilidade.
    [PostgreSqlFact]
    public async Task PrepararVetoresEIdempotenteEPreservaVetoresGravados()
    {
        await using var banco = await Banco.CriarAsync();
        var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "Vetores");
        var item = Novo(espaco.Id);
        await using var c = banco.Contexto();
        c.AddRange(espaco, item); await c.SaveChangesAsync();
        var indice = new Dante.Infrastructure.BuscaDoBrain.IndiceDeBuscaPostgreSql(c);
        Assert.False(await indice.VetoresDisponiveisAsync());
        await indice.PrepararVetoresAsync();
        var modelo = new Dante.Application.BuscaDoBrain.ModeloEmbedding("teste", "controlado", "1", 3);
        Assert.True(await indice.GravarAsync(item.Id, item.Revisao, modelo, [1, 0, 0]));
        await indice.PrepararVetoresAsync(); await indice.PrepararVetoresAsync();
        Assert.True(await indice.VetoresDisponiveisAsync());
        Assert.Equal(2, await c.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_attribute WHERE attname = 'vetor' AND NOT attisdropped AND attrelid IN ('brain_index.representacoes'::regclass, 'brain_index.partes_fontes'::regclass)").SingleAsync());
        Assert.Equal(1, await c.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM brain_index.representacoes WHERE vetor IS NOT NULL").SingleAsync());
        Assert.Equal(1, await c.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_extension WHERE extname = 'vector'").SingleAsync());
    }

    [PostgreSqlFact]
    public async Task ListarPendentesRespeitaSensibilidadeEscopoValidadeERevisao()
    {
        await using var banco = await Banco.CriarAsync();
        var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "Pendentes"); var outro = new EspacoDeConhecimento(espaco.IdUsuario, "Outro");
        Conhecimento Com(Sensibilidade s, Guid? e = null) => new(e ?? espaco.Id, null, TipoDeConhecimento.Fato, "conteúdo " + s, null, StatusDoConhecimento.Inferido,
            null, s, null, null, [], Origem(), DateTimeOffset.UtcNow, NaturezaDoConteudo.DitoPeloUsuario);
        var publico = Com(Sensibilidade.Publico); var pessoal = Com(Sensibilidade.Pessoal); var trabalho = Com(Sensibilidade.Trabalho);
        var confidencial = Com(Sensibilidade.Confidencial); var secreto = Com(Sensibilidade.Secreto); var deOutro = Com(Sensibilidade.Publico, outro.Id);
        var inativo = Com(Sensibilidade.Publico); inativo.Invalidar(1, Origem(), DateTimeOffset.UtcNow);
        await using var c = banco.Contexto();
        c.AddRange(espaco, outro, publico, pessoal, trabalho, confidencial, secreto, deOutro, inativo); await c.SaveChangesAsync();
        var indice = new Dante.Infrastructure.BuscaDoBrain.IndiceDeBuscaPostgreSql(c); await indice.PrepararVetoresAsync();
        var modelo = new Dante.Application.BuscaDoBrain.ModeloEmbedding("teste", "controlado", "1", 3);
        var acesso = new Dante.Application.SegurancaDoBrain.AcessoAoBrain(espaco.IdUsuario, espaco.Id, null);
        Assert.Equal(new[] { publico.Id, pessoal.Id, trabalho.Id }.Order(), (await indice.ListarPendentesAsync(acesso, modelo, 100)).Order());
        Assert.Equal(new[] { publico.Id, pessoal.Id, trabalho.Id, confidencial.Id }.Order(),
            (await indice.ListarPendentesAsync(acesso with { PermitirConfidencial = true, PermitirSecreto = true }, modelo, 100)).Order());
        // Vetor da revisão atual tira o item da fila; nova revisão o devolve.
        Assert.True(await indice.GravarAsync(publico.Id, publico.Revisao, modelo, [1, 0, 0]));
        Assert.DoesNotContain(publico.Id, await indice.ListarPendentesAsync(acesso, modelo, 100));
        Assert.Contains(publico.Id, await indice.ListarPendentesAsync(acesso, modelo with { Versao = "2" }, 100));
        publico.Corrigir(publico.Revisao, publico.Tipo, "conteúdo revisto", null, null, publico.Sensibilidade, null, null, [], Origem(), DateTimeOffset.UtcNow, NaturezaDoConteudo.DitoPeloUsuario);
        await c.SaveChangesAsync();
        Assert.Contains(publico.Id, await indice.ListarPendentesAsync(acesso, modelo, 100));
        Assert.Single(await indice.ListarPendentesAsync(acesso, modelo, 1));
    }

    internal static ProvenienciaDoConhecimento Origem() => new(Guid.NewGuid(), "usuário", "fonte", "v1", "evidência");
    internal static Conhecimento Novo(Guid espaco, Guid? projeto = null) => new(espaco, projeto, TipoDeConhecimento.Fato,
        "conteúdo", null, StatusDoConhecimento.Inferido, null, Sensibilidade.Pessoal, null, null, ["teste"], Origem(), DateTimeOffset.UtcNow, NaturezaDoConteudo.DitoPeloUsuario);
}

internal sealed class Banco : IAsyncDisposable
{
    private readonly string nome = "brain_test_" + Guid.NewGuid().ToString("N");
    public string Nome => nome;
    public string ConnectionString { get; private set; } = "";
    // Sempre a string original da variável: com Persist Security Info=false (padrão), NpgsqlConnection.ConnectionString
    // perde a senha depois de Open, e o banco temporário deixaria de autenticar sob SCRAM (#246).
    internal static string Administrativa => Environment.GetEnvironmentVariable("DANTE_TEST_POSTGRES")!;
    internal static string ParaBanco(string administrativa, string nome) =>
        new NpgsqlConnectionStringBuilder(administrativa) { Database = nome, Pooling = false }.ConnectionString;
    // migracao substitui MigrateAsync só para provar a limpeza quando a migration falha.
    public static async Task<Banco> CriarAsync(bool migrar = true, Func<DanteDbContext, Task>? migracao = null)
    {
        var banco = new Banco();
        await using (var admin = new NpgsqlConnection(Administrativa))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {banco.nome}", admin); await create.ExecuteNonQueryAsync();
        }
        try
        {
            banco.ConnectionString = ParaBanco(Administrativa, banco.nome);
            if (migrar) { await using var c = banco.Contexto(); await (migracao ?? (x => x.Database.MigrateAsync()))(c); }
            return banco;
        }
        catch
        {
            // O chamador nunca recebe o banco: sem esta limpeza ele ficaria abandonado no servidor.
            await banco.DisposeAsync();
            throw;
        }
    }
    public DanteDbContext Contexto() => Contexto(ConnectionString);
    public static DanteDbContext Contexto(string connectionString) => new(new DbContextOptionsBuilder<DanteDbContext>().UseNpgsql(connectionString,
        pg => pg.MigrationsHistoryTable("__EFMigrationsHistory", "brain_meta")).Options);
    public async ValueTask DisposeAsync()
    {
        // Só o banco desta instância, pelo nome aleatório; IF EXISTS torna a limpeza repetível.
        await using var admin = new NpgsqlConnection(Administrativa); await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {nome} WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync();
    }
}
