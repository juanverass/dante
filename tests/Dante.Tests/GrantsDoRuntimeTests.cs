using Dante.Application;
using Dante.Application.BuscaDoBrain;
using Dante.Application.DocumentosFonte;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Infrastructure;
using Dante.Infrastructure.Banco;
using Dante.Infrastructure.BuscaDoBrain;
using Dante.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
namespace Dante.Tests;

// A conta runtime recebe exatamente os grants publicados em docs/brain/LOCAL_STORAGE.md (#244):
// documento e teste não podem divergir. Identificadores SQL são prefixo fixo + Guid N.
#pragma warning disable EF1002
public sealed class GrantsDoRuntimeTests
{
    private static readonly string[] Schemas = ["brain_data", "brain_meta", "brain_index", "public"];

    [PostgreSqlFact]
    public async Task RuntimeComGrantsDocumentadosOperaConhecimentosEIndicesDerivados()
    {
        await using var banco = await Banco.CriarAsync();
        // Habilitar vector é administrativo (search-enable-vector); o runtime só usa a coluna criada.
        await using (var c = banco.Contexto()) await new IndiceDeBuscaPostgreSql(c).PrepararVetoresAsync();
        var role = "runtime_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(Environment.GetEnvironmentVariable("DANTE_TEST_POSTGRES"));
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE ROLE {role} LOGIN", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            await using (var c = banco.Contexto()) await c.Database.ExecuteSqlRawAsync(GrantsDocumentados(role));
            var runtimeCs = new NpgsqlConnectionStringBuilder(banco.ConnectionString) { Username = role }.ConnectionString;
            var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "Runtime");
            var conhecimento = new Conhecimento(espaco.Id, null, TipoDeConhecimento.Fato, "relação runtime indexada", null,
                StatusDoConhecimento.Inferido, null, Sensibilidade.Trabalho, null, null, ["runtime"],
                new(Guid.NewGuid(), "teste", "fonte", null, "trecho"), DateTimeOffset.UtcNow);
            await using (var runtime = Banco.Contexto(runtimeCs))
            {
                Assert.True(await new AdministracaoDoBanco(runtime).VerificarSaudeAsync());
                // INSERT em brain_data dispara brain_index.atualizar_conhecimento() com os privilégios do runtime.
                runtime.AddRange(espaco, conhecimento); await runtime.SaveChangesAsync();
                Assert.Equal([conhecimento.Id], await IdsAsync(runtime, "brain_index.trabalhos"));
            }

            var acesso = new AcessoAoBrain(espaco.IdUsuario, espaco.Id, null, true, true);
            using (var provider = Provider(runtimeCs))
            using (var scope = EscoposBrainDeTeste.Criar(provider, espaco.IdUsuario, espaco.Id))
            {
                // INSERT em brain_data.documentos_fonte dispara brain_index.atualizar_partes_fonte().
                var fonte = await scope.ServiceProvider.GetRequiredService<DocumentoFonteAppService>()
                    .ImportarAsync(acesso, "nota:runtime", "texto", "fonte runtime indexada");
                var busca = scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>();
                // Embeddings: INSERT/UPDATE em representacoes e UPDATE em partes_fontes.
                Assert.Equal(2, await busca.ReindexarAsync(acesso));
                Assert.Equal(0, await busca.ReindexarAsync(acesso));
                // Reconstrução: DELETE em representacoes e regeneração das partes pelo trigger de fontes.
                Assert.Equal(2, await busca.ReindexarAsync(acesso, reconstruir: true));
                await scope.ServiceProvider.GetRequiredService<IndiceDeBuscaPostgreSql>().ReconstruirLexicalAsync();

                var resultados = (await busca.BuscarAsync(acesso, new() { Texto = "runtime indexada" })).Resultados;
                Assert.Contains(resultados, r => r.Fonte is null && r.Item.Id == conhecimento.Id);
                Assert.Contains(resultados, r => r.Fonte?.IdDocumento == fonte.Id);
                Assert.NotNull(await scope.ServiceProvider.GetRequiredService<IIndiceDeFontes>().ObterTrechoAsync(acesso, fonte.Id, 1, 0));
            }

            await using (var runtime = Banco.Contexto(runtimeCs))
            {
                Assert.Equal([conhecimento.Id], await IdsAsync(runtime, "brain_index.representacoes"));
                // Reclassificar como Secreto faz o trigger remover os derivados (DELETE em brain_index).
                var salvo = await runtime.Conhecimentos.SingleAsync(x => x.Id == conhecimento.Id);
                salvo.Corrigir(1, salvo.Tipo, salvo.Conteudo, null, null, Sensibilidade.Secreto, null, null, [],
                    salvo.Proveniencia, DateTimeOffset.UtcNow);
                await runtime.SaveChangesAsync();
                Assert.Empty(await IdsAsync(runtime, "brain_index.trabalhos"));
                Assert.Empty(await IdsAsync(runtime, "brain_index.representacoes"));

                foreach (var proibido in new[]
                {
                    "CREATE TABLE brain_index.proibida(id int)",
                    "CREATE TABLE brain_data.proibida(id int)",
                    "CREATE TABLE public.proibida(id int)",
                    "ALTER TABLE brain_index.trabalhos ADD COLUMN proibida int",
                    "CREATE OR REPLACE FUNCTION brain_index.atualizar_conhecimento() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$",
                    "INSERT INTO brain_meta.\"__EFMigrationsHistory\" VALUES ('99999999999999_Forjada', '0')",
                })
                    await Assert.ThrowsAsync<PostgresException>(() => runtime.Database.ExecuteSqlRawAsync(proibido));
            }

            // Sem atributos elevados, ownership, CREATE em schema ou membership; e nenhuma sequência exige grant.
            Assert.False(await EscalarAsync<bool>(admin, $"SELECT rolsuper OR rolcreatedb OR rolcreaterole OR rolbypassrls FROM pg_roles WHERE rolname = '{role}'"));
            await using (var noBanco = new NpgsqlConnection(banco.ConnectionString))
            {
                await noBanco.OpenAsync();
                Assert.Equal(0L, await EscalarAsync<long>(noBanco, $"""
                    SELECT (SELECT count(*) FROM pg_class WHERE relowner = '{role}'::regrole)
                         + (SELECT count(*) FROM pg_namespace WHERE nspowner = '{role}'::regrole)
                         + (SELECT count(*) FROM pg_proc WHERE proowner = '{role}'::regrole)
                         + (SELECT count(*) FROM pg_type WHERE typowner = '{role}'::regrole)
                         + (SELECT count(*) FROM pg_database WHERE datdba = '{role}'::regrole)
                         + (SELECT count(*) FROM pg_auth_members WHERE member = '{role}'::regrole)
                    """));
                foreach (var schema in Schemas)
                    Assert.False(await EscalarAsync<bool>(noBanco, $"SELECT has_schema_privilege('{role}', '{schema}', 'CREATE')"));
                Assert.Equal(0L, await EscalarAsync<long>(noBanco,
                    "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind = 'S' AND n.nspname LIKE 'brain\\_%'"));
            }
        }
        finally
        {
            await using (var c = banco.Contexto()) await c.Database.ExecuteSqlRawAsync($"DROP OWNED BY {role}");
            await using var drop = new NpgsqlCommand($"DROP ROLE {role}", admin); await drop.ExecuteNonQueryAsync();
        }
    }

    // Primeiro bloco ```sql com GRANT do guia, com a conta de exemplo substituída pela conta do teste.
    private static string GrantsDocumentados(string role)
    {
        var guia = File.ReadAllText(Path.Combine(RaizDoRepositorio(), "docs", "brain", "LOCAL_STORAGE.md"));
        var inicio = guia.IndexOf("```sql\nGRANT", StringComparison.Ordinal);
        Assert.True(inicio >= 0, "LOCAL_STORAGE.md deve publicar os grants do runtime num bloco sql.");
        inicio += "```sql\n".Length;
        var sql = guia[inicio..guia.IndexOf("```", inicio, StringComparison.Ordinal)];
        Assert.Contains("dante_runtime", sql);
        return sql.Replace("dante_runtime", role, StringComparison.Ordinal);
    }

    private static async Task<T> EscalarAsync<T>(NpgsqlConnection conexao, string sql)
    {
        await using var comando = new NpgsqlCommand(sql, conexao);
        return (T)(await comando.ExecuteScalarAsync())!;
    }

    private static Task<List<Guid>> IdsAsync(DanteDbContext c, string tabela) =>
        c.Database.SqlQueryRaw<Guid>($"SELECT id_conhecimento AS \"Value\" FROM {tabela} ORDER BY id_conhecimento").ToListAsync();

    private static ServiceProvider Provider(string connection)
    {
        var services = new ServiceCollection().AddApplication().AddInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Dante"] = connection }).Build());
        services.AddSingleton<IGeradorDeEmbedding>(new Gerador());
        return services.BuildServiceProvider();
    }

    private sealed class Gerador : IGeradorDeEmbedding
    {
        public ModeloEmbedding? Modelo => new("teste", "runtime", "1", 3);
        public bool Externo => false;
        public Task<float[]?> GerarAsync(string texto, CancellationToken cancellationToken = default) => Task.FromResult<float[]?>([1, 0, 0]);
    }

    private static string RaizDoRepositorio()
    {
        for (var diretorio = new DirectoryInfo(AppContext.BaseDirectory); diretorio is not null; diretorio = diretorio.Parent)
            if (File.Exists(Path.Combine(diretorio.FullName, "Dante.sln"))) return diretorio.FullName;
        throw new DirectoryNotFoundException("Raiz do repositório não encontrada.");
    }
}
