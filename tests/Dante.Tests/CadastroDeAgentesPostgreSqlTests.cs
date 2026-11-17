using Dante.Application;
using Dante.Infrastructure;
using Microsoft.Extensions.Configuration;
using Dante.Application.Agentes;
using Dante.Application.Comum;
using Dante.Application.Execucoes;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Banco;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Worker.Execucoes;
using Dante.Worker.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Dante.Tests;

// #301 (AD-68): cadastro de agentes contra PostgreSQL real e descartável (DANTE_TEST_POSTGRES), no schema do ledger.
// Cada teste cria e remove um banco aleatório; sem a variável, os testes são pulados explicitamente.
[Collection("AdministracaoConsole")]
public sealed class CadastroDeAgentesPostgreSqlTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    public async Task VersoesPersistemImutaveisComHashIntegroDepoisDeRecarregar()
    {
        await using var banco = await CadastroAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using (var escopo = provider.CreateAsyncScope())
        {
            var cadastro = escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>();
            await cadastro.CriarAsync(new NovoAgenteDto("revisor", new ConfiguracaoDoAgenteDto
            {
                Driver = AgentKind.Claude, Modelo = "opus", Esforco = "high", Instrucoes = "Revise.\nAponte riscos.",
                Capacidades = [CapacidadeDoAgente.Revisar, CapacidadeDoAgente.Pesquisar],
                Ferramentas = [FerramentaDoDante.Planilhas, FerramentaDoDante.Brain], Repositorios = ["@dante", "@b"],
                PerfilMaximo = AgentPermissionProfile.Manual, PodeEscrever = true,
                Limites = new LimitesDoAgente(2, 5000, 30, 60)
            }, 1001, "Revisor"));
        }
        await using (var escopo = provider.CreateAsyncScope())
            await escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>()
                .EditarAsync("revisor", new AlteracaoDoAgenteDto { Instrucoes = "Só o essencial." }, 1002);

        await using (var escopo = provider.CreateAsyncScope())
        {
            var lido = (await escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>().ObterAsync("revisor"))!;
            Assert.Equal((2, EstadoDoAgente.Ativo, "Revisor"), (lido.NumeroDaVersaoAtual, lido.Estado, lido.Nome));
            var (v2, v1) = (lido.Versoes[0], lido.Versoes[1]);
            Assert.Equal(lido.VersaoAtual!.Id, v2.Id);
            Assert.Equal(("Revise.\nAponte riscos.", "Só o essencial."), (v1.Configuracao.Instrucoes, v2.Configuracao.Instrucoes));
            Assert.Equal([CapacidadeDoAgente.Revisar, CapacidadeDoAgente.Pesquisar], v1.Configuracao.Capacidades);
            Assert.Equal([FerramentaDoDante.Brain, FerramentaDoDante.Planilhas], v1.Configuracao.Ferramentas);
            Assert.Equal(["@b", "@dante"], v1.Configuracao.Repositorios);
            Assert.Equal(new LimitesDoAgente(2, 5000, 30, 60), v1.Configuracao.Limites);
            Assert.Equal((1001L, 1002L), (v1.IdDoAutor, v2.IdDoAutor));
            // O hash recalculado do que voltou do banco confere com o gravado.
            Assert.Equal(v1.HashDaConfiguracao, v1.Configuracao.Hash());
            Assert.Equal(v2.HashDaConfiguracao, v2.Configuracao.Hash());
            Assert.NotEqual(v1.HashDaConfiguracao, v2.HashDaConfiguracao);
            var versoes = await escopo.ServiceProvider.GetRequiredService<IVersaoDoAgenteRepository>().ListarDoAgenteAsync(lido.Id);
            Assert.All(versoes, versao => Assert.True(versao.Integra));
        }
    }

    [PostgreSqlFact]
    public async Task BancoRecusaAlterarOuRemoverVersaoTrocarHandleERemoverAgente()
    {
        await using var banco = await CadastroAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using (var escopo = provider.CreateAsyncScope())
            await escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>().CriarAsync(
                new NovoAgenteDto("revisor", new ConfiguracaoDoAgenteDto { Driver = AgentKind.Codex }, 1));
        foreach (var sql in new[]
                 {
                     "UPDATE orquestracao.versoes_de_agente SET instrucoes = 'adulterada'",
                     "DELETE FROM orquestracao.versoes_de_agente",
                     "UPDATE orquestracao.agentes SET handle = 'outro'",
                     "DELETE FROM orquestracao.agentes"
                 })
            Assert.Equal(PostgresErrorCodes.RestrictViolation,
                (await Assert.ThrowsAsync<PostgresException>(() => Executar(banco, sql))).SqlState);
        // Handle fora do formato também é recusado pelo banco.
        await Assert.ThrowsAsync<PostgresException>(() => Executar(banco,
            "UPDATE orquestracao.agentes SET handle = 'Com@' WHERE false OR true"));
        // Estado e apresentação mudam normalmente.
        await using (var escopo = provider.CreateAsyncScope())
        {
            var cadastro = escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>();
            await cadastro.DesativarAsync("revisor");
            await cadastro.ApresentarAsync("revisor", "Revisor", null);
        }
        await using (var escopo = provider.CreateAsyncScope())
            Assert.Equal(EstadoDoAgente.Desativado, (await escopo.ServiceProvider
                .GetRequiredService<ICadastroDeAgentesAppService>().ObterAsync("revisor"))!.Estado);
    }

    [PostgreSqlFact]
    public async Task HandleUnicoNoBancoViraConflitoNaCorrida()
    {
        await using var banco = await CadastroAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using var a = provider.CreateAsyncScope();
        await using var b = provider.CreateAsyncScope();
        // Os dois passaram pela consulta de handle livre; só um grava.
        var configuracao = new ConfiguracaoDoAgente(AgentKind.Claude, AgentPermissionProfile.Plan, AgentPermissionProfile.Plan, false);
        foreach (var escopo in new[] { a, b })
        {
            var agente = new Agente("dev", null, null, 1, Agora);
            await escopo.ServiceProvider.GetRequiredService<IAgenteRepository>().AdicionarAsync(agente);
            await escopo.ServiceProvider.GetRequiredService<IVersaoDoAgenteRepository>()
                .AdicionarAsync(agente.NovaVersao(configuracao, 1, Agora));
        }
        await a.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        await Assert.ThrowsAsync<ConflitoDeConcorrenciaException>(() =>
            b.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync());
        await using var leitura = provider.CreateAsyncScope();
        Assert.Single(await leitura.ServiceProvider.GetRequiredService<IAgenteRepository>().ListarAsync(true));
        Assert.Equal(1L, await Escalar(banco, "SELECT count(*) FROM orquestracao.versoes_de_agente"));
    }

    [PostgreSqlFact]
    public async Task SessaoRegistraAVersaoExataEOHashEEditarDepoisNaoAAltera()
    {
        await using var banco = await CadastroAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using var espelho = new EspelhoDoLedger(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EspelhoDoLedger>.Instance);
        VersaoDoAgenteDto v1;
        await using (var escopo = provider.CreateAsyncScope())
        {
            var cadastro = escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>();
            await cadastro.CriarAsync(new NovoAgenteDto("revisor", new ConfiguracaoDoAgenteDto { Driver = AgentKind.Claude }, 1));
            v1 = (await cadastro.ResolverAsync("revisor")).Versao!;
        }
        var drivers = new FakeSessionDriverFactory();
        string registrada, embutida;
        await using (var sessoes = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, ledger: espelho))
        {
            var contexto = JobExecutionContext.General("/general");
            registrada = (await sessoes.StartAsync(new SessionStartRequest(1001, AgentKind.Claude, contexto,
                Profile: AgentPermissionProfile.Plan, RegisteredAgent: new AgenteDaSessao("revisor", v1)))).Session!.Id;
            embutida = (await sessoes.StartAsync(new SessionStartRequest(1001, AgentKind.Codex, contexto,
                Profile: AgentPermissionProfile.Auto, ModelSelection: new AgentModelSelection("fake-mini", "low")))).Session!.Id;
            await espelho.DescarregarAsync();

            await using (var escopo = provider.CreateAsyncScope())
            {
                var cadastro = escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>();
                await cadastro.EditarAsync("revisor", new AlteracaoDoAgenteDto { Instrucoes = "v2" }, 1);
                await cadastro.DesativarAsync("revisor");
                // Sessão não terminal no ledger com a versão do agente: arquivar espera.
                Assert.Contains("ainda tem 1", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    cadastro.ArquivarAsync("revisor"))).Message);
            }
        }
        await espelho.DescarregarAsync();

        await using (var escopo = provider.CreateAsyncScope())
        {
            var ledger = escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>();
            var sessao = (await ledger.ObterSessaoAsync(long.Parse(registrada[1..])))!;
            Assert.Equal(("revisor", v1.HashDaConfiguracao, v1.Id), (sessao.HandleDoAgente, sessao.HashDaConfiguracaoDoAgente,
                sessao.IdVersaoDoAgente));
            var comum = (await ledger.ObterSessaoAsync(long.Parse(embutida[1..])))!;
            Assert.Equal(("codex", AgentesEmbutidos.Materializar(AgentKind.Codex, new AgentModelSelection("fake-mini", "low"),
                AgentPermissionProfile.Auto).Hash(), (Guid?)null), (comum.HandleDoAgente, comum.HashDaConfiguracaoDoAgente,
                comum.IdVersaoDoAgente));
            // Com as sessões encerradas, arquivar passa.
            await escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>().ArquivarAsync("revisor");
        }
    }

    [PostgreSqlFact]
    public async Task TarefaPersisteComAVersaoEEtapaSoDeLeituraEOBancoExigeAVersao()
    {
        await using var banco = await CadastroAsync();
        await using var provider = Provider(banco.ConnectionString);
        VersaoDoAgenteDto versao;
        await using (var escopo = provider.CreateAsyncScope())
        {
            var cadastro = escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>();
            await cadastro.CriarAsync(new NovoAgenteDto("dev", new ConfiguracaoDoAgenteDto
            {
                Driver = AgentKind.Codex, PerfilPadrao = AgentPermissionProfile.Auto, PerfilMaximo = AgentPermissionProfile.Auto,
                PodeEscrever = true, Capacidades = [CapacidadeDoAgente.Implementar]
            }, 1));
            versao = (await cadastro.ResolverAsync("dev")).Versao!;
        }
        var plano = MontagemDaTarefa.Montar("dev", versao, JobExecutionContext.General("/general"), "leia o README");
        long numero;
        await using (var escopo = provider.CreateAsyncScope())
        {
            var s = escopo.ServiceProvider;
            numero = await s.GetRequiredService<ILedgerDeExecucoesAppService>().ReservarNumeroDeExecucaoAsync();
            var (execucao, etapas) = MontagemDaTarefa.CriarExecucao(plano, numero, "telegram", 1001, Agora);
            await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(execucao);
            foreach (var etapa in etapas) await s.GetRequiredService<IEtapaDaExecucaoRepository>().AdicionarAsync(etapa);
            await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        }
        await using (var escopo = provider.CreateAsyncScope())
        {
            var lida = (await escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>().ObterExecucaoAsync(numero))!;
            Assert.Equal((TipoDeExecucao.Tarefa, versao.Id, $"E{numero}", "leia o README"),
                (lida.Tipo, lida.IdVersaoDoAgente, lida.Codigo, lida.Objetivo));
            var etapa = Assert.Single(lida.Etapas);
            Assert.False(etapa.Escrita);
            Assert.Equal(AgentKind.Codex, etapa.Agente);
        }
        // Tarefa sem versão é recusada pelo banco, mesmo sem passar pelo domínio.
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => Executar(banco,
            $"INSERT INTO orquestracao.execucoes (id, numero, tipo, estado, canal, id_do_usuario_no_canal, contexto, criada_em) " +
            $"VALUES (gen_random_uuid(), 999, {(int)TipoDeExecucao.Tarefa}, 0, 'telegram', 1, '{{}}', now())"))).SqlState);
    }

    [PostgreSqlFact]
    public async Task MigrationDoCadastroEhReversivelSemDeixarFuncoes()
    {
        await using var banco = await CadastroAsync();
        await using (var contexto = new OrquestracaoDbContext(OrquestracaoDbContextFactory.Opcoes(banco.ConnectionString)))
        {
            await contexto.GetService<IMigrator>().MigrateAsync("20261116181501_LedgerDeExecucoes");
            Assert.Equal(0L, await Escalar(banco,
                "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'orquestracao'"));
            Assert.Equal(0L, await Escalar(banco,
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'orquestracao' AND table_name LIKE '%agente%' AND table_name <> 'sessoes_de_agente'"));
            await contexto.Database.MigrateAsync();
            Assert.Empty(await contexto.Database.GetPendingMigrationsAsync());
        }
        Assert.Equal(2L, await Escalar(banco,
            "SELECT count(*) FROM pg_trigger WHERE tgname IN ('trg_versoes_de_agente_imutaveis', 'trg_agentes_identidade')"));
    }

    private static ServiceProvider Provider(string connectionString)
    {
        var services = new ServiceCollection().AddLogging().AddApplication().AddInfrastructure(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Dante"] = connectionString }).Build());
        // Catálogo fixo: o real consultaria as CLIs instaladas.
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
