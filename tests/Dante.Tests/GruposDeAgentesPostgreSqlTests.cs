using Dante.Application;
using Dante.Application.Agentes;
using Dante.Application.Comum;
using Dante.Application.Execucoes;
using Dante.Application.Grupos;
using Dante.Domain.Grupos;
using Dante.Infrastructure;
using Dante.Infrastructure.Banco;
using Dante.Infrastructure.Data.Orquestracao;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Dante.Tests;

// #308 (AD-69): cadastro de grupos contra PostgreSQL real e descartável (DANTE_TEST_POSTGRES), no schema do ledger.
// Cada teste cria e remove um banco aleatório; sem a variável, os testes são pulados explicitamente.
[Collection("AdministracaoConsole")]
public sealed class GruposDeAgentesPostgreSqlTests
{
    [PostgreSqlFact]
    public async Task VersoesEParticipantesPersistemImutaveisComHashIntegroDepoisDeRecarregar()
    {
        await using var banco = await CadastroAsync();
        await using var provider = Provider(banco.ConnectionString);
        string hashV1;
        await using (var escopo = provider.CreateAsyncScope())
        {
            await CadastroDeGruposAppServiceTests.CriarAgentesAsync(Agentes(escopo));
            hashV1 = (await Grupos(escopo).CriarAsync(new NovoGrupoDto("eng",
                CadastroDeGruposAppServiceTests.Configuracao() with { DuracaoMaximaEmMinutos = 90 }, 41, "Time")))
                .VersaoAtual!.HashDaConfiguracao;
        }
        await using (var escopo = provider.CreateAsyncScope())
        {
            await Grupos(escopo).AtivarAsync("eng", 1, hashV1[..12], 42);
            await Grupos(escopo).EditarAsync("eng", new AlteracaoDoGrupoDto
            {
                Roteiro = [CadastroDeGruposAppServiceTests.Roteiro[0], CadastroDeGruposAppServiceTests.Roteiro[1],
                    CadastroDeGruposAppServiceTests.Roteiro[3]]
            }, 43);
        }
        await using (var escopo = provider.CreateAsyncScope())
        {
            var lido = (await Grupos(escopo).ObterAsync("eng"))!;
            Assert.Equal((2, (int?)1, "Time"), (lido.NumeroDaVersaoAtual, lido.NumeroDaVersaoAtiva, lido.Descricao));
            var (v2, v1) = (lido.Versoes[0], lido.Versoes[1]);
            Assert.Equal(hashV1, v1.HashDaConfiguracao);
            Assert.True(v1.Integra && v2.Integra);
            Assert.Equal(v1.HashDaConfiguracao, v1.Configuracao!.Hash());
            Assert.Equal(CadastroDeGruposAppServiceTests.Roteiro, v1.Configuracao.Roteiro);
            Assert.Equal((2, (int?)90), (v1.Configuracao.Politica.RodadasDeRevisaoMaximas,
                v1.Configuracao.Politica.DuracaoMaximaEmMinutos));
            Assert.Equal(["@dante"], v1.Configuracao.Politica.RepositoriosPermitidos!);
            Assert.Equal([("dev", "codex-dev", true, "Implementa a tarefa."), ("rev", "codex-rev", false, "Revisa o diff.")],
                v1.Participantes.Select(p => (p.Papel, p.Handle, p.PodeEscrever, p.Responsabilidades)));
            Assert.False(v2.Configuracao!.TemCorrecao);
            Assert.Equal((41L, 43L), (v1.IdDoAutor, v2.IdDoAutor));
            var decisao = Assert.Single(lido.Ativacoes);
            Assert.Equal((1, hashV1, 42L), (decisao.NumeroDaVersao, decisao.HashAprovado, decisao.IdDoAutor));
            var resolvido = await Grupos(escopo).ResolverParaExecucaoAsync("eng");
            Assert.True(resolvido.Resolvido, resolvido.Erro);
            Assert.Equal(v1.Id, resolvido.Versao!.Id);
        }
    }

    [PostgreSqlFact]
    public async Task BancoRecusaAlterarOuRemoverVersaoParticipanteDecisaoENomeOuRemoverGrupo()
    {
        await using var banco = await GrupoAtivadoAsync();
        foreach (var sql in new[]
                 {
                     "UPDATE orquestracao.versoes_de_grupo SET rodadas_de_revisao_maximas = 1",
                     "DELETE FROM orquestracao.versoes_de_grupo",
                     "UPDATE orquestracao.participantes_de_grupo SET pode_escrever = NOT pode_escrever",
                     "DELETE FROM orquestracao.participantes_de_grupo",
                     "UPDATE orquestracao.ativacoes_de_grupo SET id_do_autor = 1",
                     "DELETE FROM orquestracao.ativacoes_de_grupo",
                     "UPDATE orquestracao.grupos_de_agentes SET nome = 'outro'",
                     "DELETE FROM orquestracao.grupos_de_agentes"
                 })
            Assert.Equal(PostgresErrorCodes.RestrictViolation,
                (await Assert.ThrowsAsync<PostgresException>(() => Executar(banco, sql))).SqlState);
        // Paralelismo acima de 1 e papel fora do formato são recusados pelo próprio esquema.
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => Executar(banco,
            """
            INSERT INTO orquestracao.versoes_de_grupo (id, id_grupo, numero, modo, roteiro, rodadas_de_revisao_maximas,
                paralelismo_maximo, repositorios_permitidos, hash_da_configuracao, id_do_autor, criada_em)
            SELECT gen_random_uuid(), id, 9, 0, '{}', 0, 2, '{}', repeat('a', 64), 1, now() FROM orquestracao.grupos_de_agentes
            """))).SqlState);
    }

    [PostgreSqlFact]
    public async Task VersaoAtivaSoComDecisaoGravadaComOHashDaquelaVersao()
    {
        await using var banco = await GrupoAtivadoAsync(editarDepois: true);
        // Apontar a ativa para a v2 sem decisão: recusado no commit pela constraint trigger adiada.
        Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() => Executar(banco,
            """
            UPDATE orquestracao.grupos_de_agentes g SET id_versao_ativa = v.id, numero_da_versao_ativa = v.numero
            FROM orquestracao.versoes_de_grupo v WHERE v.id_grupo = g.id AND v.numero = 2
            """))).SqlState);
        // Decisão com hash de outra versão (ou inventado): recusada na inserção.
        Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() => Executar(banco,
            """
            INSERT INTO orquestracao.ativacoes_de_grupo (id, id_grupo, id_versao_do_grupo, numero_da_versao, hash_aprovado, id_do_autor, em)
            SELECT gen_random_uuid(), v.id_grupo, v.id, v.numero, repeat('b', 64), 1, now()
            FROM orquestracao.versoes_de_grupo v WHERE v.numero = 2
            """))).SqlState);
        // Versão ativa sem número correspondente: o check recusa.
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => Executar(banco,
            "UPDATE orquestracao.grupos_de_agentes SET numero_da_versao_ativa = NULL"))).SqlState);
        Assert.Equal(1L, await Escalar(banco, "SELECT numero_da_versao_ativa FROM orquestracao.grupos_de_agentes"));
    }

    [PostgreSqlFact]
    public async Task EdicoesEAtivacoesSimultaneasGravamUmaSo()
    {
        await using var banco = await GrupoAtivadoAsync();
        await using var provider = Provider(banco.ConnectionString);
        // Dois escopos leem o mesmo grupo (v1) antes de qualquer gravação e criam a v2: índice único (grupo, número) e
        // xmin do grupo, só o primeiro grava. Leituras e gravações em ordem fixa: o resultado não depende de agendamento.
        await using var a = provider.CreateAsyncScope();
        await using var b = provider.CreateAsyncScope();
        var lidos = new List<(AsyncServiceScope Escopo, GrupoDeAgentes Grupo, VersaoDoGrupo Versao)>();
        foreach (var (escopo, rodadas) in new[] { (a, 3), (b, 4) })
        {
            var grupo = (await escopo.ServiceProvider.GetRequiredService<IGrupoDeAgentesRepository>().ObterPorNomeAsync("eng"))!;
            var atual = (await escopo.ServiceProvider.GetRequiredService<IVersaoDoGrupoRepository>()
                .ObterComParticipantesAsync(grupo.IdVersaoAtual!.Value))!.Configuracao!;
            var versao = grupo.NovaVersao(new ConfiguracaoDoGrupo(atual.Modo, atual.Participantes, atual.Roteiro,
                atual.Politica with { RodadasDeRevisaoMaximas = rodadas }), 1, DateTimeOffset.UtcNow);
            lidos.Add((escopo, grupo, versao));
        }
        foreach (var (escopo, grupo, versao) in lidos)
        {
            await escopo.ServiceProvider.GetRequiredService<IVersaoDoGrupoRepository>().AdicionarAsync(versao);
            escopo.ServiceProvider.GetRequiredService<IGrupoDeAgentesRepository>().Atualizar(grupo);
        }
        await a.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        await Assert.ThrowsAsync<ConflitoDeConcorrenciaException>(() =>
            b.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync());
        Assert.Equal(2L, await Escalar(banco, "SELECT count(*) FROM orquestracao.versoes_de_grupo"));
        Assert.Equal(3L, await Escalar(banco, "SELECT rodadas_de_revisao_maximas FROM orquestracao.versoes_de_grupo WHERE numero = 2"));
        Assert.Equal(2L, await Escalar(banco, "SELECT numero_da_versao_atual FROM orquestracao.grupos_de_agentes"));

        // Duas ativações da v2 a partir da mesma leitura: uma decisão só (xmin do grupo).
        await using var c = provider.CreateAsyncScope();
        await using var d = provider.CreateAsyncScope();
        var ativacoes = new List<(AsyncServiceScope Escopo, GrupoDeAgentes Grupo, AtivacaoDeVersaoDoGrupo Decisao)>();
        foreach (var escopo in new[] { c, d })
        {
            var grupo = (await escopo.ServiceProvider.GetRequiredService<IGrupoDeAgentesRepository>().ObterPorNomeAsync("eng"))!;
            var v2 = (await escopo.ServiceProvider.GetRequiredService<IVersaoDoGrupoRepository>()
                .ObterComParticipantesAsync(grupo.IdVersaoAtual!.Value))!;
            ativacoes.Add((escopo, grupo, grupo.Ativar(v2, v2.HashDaConfiguracao, 7, DateTimeOffset.UtcNow)));
        }
        foreach (var (escopo, grupo, decisao) in ativacoes)
        {
            await escopo.ServiceProvider.GetRequiredService<IAtivacaoDeVersaoDoGrupoRepository>().AdicionarAsync(decisao);
            escopo.ServiceProvider.GetRequiredService<IGrupoDeAgentesRepository>().Atualizar(grupo);
        }
        await c.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        await Assert.ThrowsAsync<ConflitoDeConcorrenciaException>(() =>
            d.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync());
        Assert.Equal(2L, await Escalar(banco, "SELECT count(*) FROM orquestracao.ativacoes_de_grupo"));
        Assert.Equal(2L, await Escalar(banco, "SELECT numero_da_versao_ativa FROM orquestracao.grupos_de_agentes"));
    }

    [PostgreSqlFact]
    public async Task NomeUnicoNoBancoViraConflitoNaCorrida()
    {
        await using var banco = await CadastroAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using (var escopo = provider.CreateAsyncScope())
            await CadastroDeGruposAppServiceTests.CriarAgentesAsync(Agentes(escopo));
        await using var a = provider.CreateAsyncScope();
        await using var b = provider.CreateAsyncScope();
        var resultados = await Task.WhenAll(new[] { a, b }.Select(async escopo =>
        {
            try
            {
                await Grupos(escopo).CriarAsync(new NovoGrupoDto("eng", CadastroDeGruposAppServiceTests.Configuracao(), 1));
                return true;
            }
            catch (Exception exception) when (exception is ConflitoDeConcorrenciaException or InvalidOperationException)
            {
                return false;
            }
        }));
        Assert.Equal(1, resultados.Count(ok => ok));
        Assert.Equal(1L, await Escalar(banco, "SELECT count(*) FROM orquestracao.grupos_de_agentes"));
        Assert.Equal(1L, await Escalar(banco, "SELECT count(*) FROM orquestracao.versoes_de_grupo"));
        Assert.Equal(2L, await Escalar(banco, "SELECT count(*) FROM orquestracao.participantes_de_grupo"));
    }

    [PostgreSqlFact]
    public async Task MigrationDosGruposEhReversivelSemDeixarFuncoes()
    {
        await using var banco = await CadastroAsync();
        await using (var contexto = new OrquestracaoDbContext(OrquestracaoDbContextFactory.Opcoes(banco.ConnectionString)))
        {
            var funcoesAntes = await Escalar(banco,
                "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'orquestracao'");
            await contexto.GetService<IMigrator>().MigrateAsync("20261117183118_ReservasDeWorktree");
            // As 4 funções dos grupos, as 3 do gate, a dos vereditos (#308) e as 3 das delegações (#309): migrations
            // seguintes, desfeitas junto.
            Assert.Equal(funcoesAntes - 11, await Escalar(banco,
                "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'orquestracao'"));
            Assert.Equal(0L, await Escalar(banco,
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'orquestracao' AND table_name LIKE '%grupo%'"));
            await contexto.Database.MigrateAsync();
            Assert.Empty(await contexto.Database.GetPendingMigrationsAsync());
        }
        Assert.Equal(6L, await Escalar(banco,
            "SELECT count(*) FROM pg_trigger WHERE tgname IN ('trg_versoes_de_grupo_imutaveis', " +
            "'trg_participantes_de_grupo_imutaveis', 'trg_ativacoes_de_grupo_imutaveis', 'trg_ativacoes_de_grupo_conferidas', " +
            "'trg_grupos_de_agentes_identidade', 'trg_grupos_de_agentes_versao_ativa')"));
    }

    private static async Task<Banco> GrupoAtivadoAsync(bool editarDepois = false)
    {
        var banco = await CadastroAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using var escopo = provider.CreateAsyncScope();
        await CadastroDeGruposAppServiceTests.CriarAgentesAsync(Agentes(escopo));
        var v1 = (await Grupos(escopo).CriarAsync(new NovoGrupoDto("eng", CadastroDeGruposAppServiceTests.Configuracao(), 1)))
            .VersaoAtual!;
        await Grupos(escopo).AtivarAsync("eng", 1, v1.HashDaConfiguracao, 2);
        if (editarDepois)
            await Grupos(escopo).EditarAsync("eng", new AlteracaoDoGrupoDto { RodadasDeRevisaoMaximas = 1 }, 1);
        return banco;
    }

    private static ICadastroDeGruposAppService Grupos(AsyncServiceScope escopo) =>
        escopo.ServiceProvider.GetRequiredService<ICadastroDeGruposAppService>();

    private static ICadastroDeAgentesAppService Agentes(AsyncServiceScope escopo) =>
        escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>();

    private static ServiceProvider Provider(string connectionString)
    {
        var services = new ServiceCollection().AddLogging().AddApplication().AddInfrastructure(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Dante"] = connectionString }).Build());
        services.Replace(ServiceDescriptor.Singleton<IAgentModelCatalog>(new CatalogoDeModelosFixo()));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task<Banco> CadastroAsync()
    {
        var banco = await Banco.CriarAsync(migrar: false);
        await using var contexto = new OrquestracaoDbContext(OrquestracaoDbContextFactory.Opcoes(banco.ConnectionString));
        await using var brain = banco.Contexto();
        await new AdministracaoDoBanco(brain, contexto).MigrarOrquestracaoAsync();
        return banco;
    }

    private static async Task Executar(Banco banco, string sql)
    {
        await using var conexao = new NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<long> Escalar(Banco banco, string sql)
    {
        await using var conexao = new NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        return Convert.ToInt64(await comando.ExecuteScalarAsync());
    }
}
