using Dante.Application;
using Dante.Application.Agentes;
using Dante.Application.Comum;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure;
using Dante.Infrastructure.Banco;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Modulos.Execucoes;
using Dante.Worker.Execucoes;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Dante.Tests;

// #299: ledger de execuções (AD-66) contra PostgreSQL real e descartável (DANTE_TEST_POSTGRES). Cada teste cria e
// remove um banco aleatório; nada toca o banco canônico. Sem a variável, os testes são pulados explicitamente.
[Collection("AdministracaoConsole")]
public sealed class LedgerDeExecucoesPostgreSqlTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [PostgreSqlFact]
    public async Task ComandoExplicitoMigraSoOSchemaDoLedgerDeFormaIdempotenteEReversivel()
    {
        await using var banco = await Banco.CriarAsync(migrar: false);
        using var provider = Provider(banco.ConnectionString);
        Assert.Equal(1, await ComandosDoBanco.ExecutarAsync(provider, ["--orquestracao", "desconhecido"]));
        Assert.Empty(await Tabelas(banco, "orquestracao"));

        Assert.Equal(0, await ComandosDoBanco.ExecutarAsync(provider, ["--orquestracao", "migrate"]));
        Assert.Equal(0, await ComandosDoBanco.ExecutarAsync(provider, ["--orquestracao", "migrate"]));
        Assert.Equal(["__historico_de_migrations", "agentes", "dependencias_de_etapa", "etapas_da_execucao", "eventos", "execucoes",
            "sessoes_de_agente", "versoes_de_agente"], await Tabelas(banco, "orquestracao"));
        // O ledger não cria nem altera nada do Brain, e o histórico do Brain continua vazio.
        Assert.Empty(await Tabelas(banco, "brain_data"));
        Assert.Empty(await Tabelas(banco, "brain_meta"));

        await using (var contexto = new OrquestracaoDbContext(OrquestracaoDbContextFactory.Opcoes(banco.ConnectionString)))
        {
            Assert.Empty(await contexto.Database.GetPendingMigrationsAsync());
            await contexto.GetService<IMigrator>().MigrateAsync("0");
            Assert.Empty(await contexto.Database.GetAppliedMigrationsAsync());
        }
        Assert.Equal(["__historico_de_migrations"], await Tabelas(banco, "orquestracao"));

        // E migrar o Brain não cria o ledger.
        await using (var brain = banco.Contexto()) await brain.Database.MigrateAsync();
        Assert.Equal(["__historico_de_migrations"], await Tabelas(banco, "orquestracao"));
        Assert.NotEmpty(await Tabelas(banco, "brain_data"));
    }

    [PostgreSqlFact]
    public async Task ExecucaoEtapasDependenciasEventosESessaoSaoPersistidosEConsultaveis()
    {
        await using var banco = await LedgerAsync();
        using var provider = Provider(banco.ConnectionString);
        var sessao = ExecucoesDominioTests.Sessao(await ReservarSessaoAsync(provider));
        var execucao = ExecucoesDominioTests.Nova(await ReservarExecucaoAsync(provider));
        var primeira = ExecucoesDominioTests.Etapa(execucao, 1);
        var segunda = new EtapaDaExecucao(execucao, 2, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica,
            Agora, Dante.Domain.Agentes.AgentKind.Claude);
        segunda.VincularSessao(sessao, "T000009");
        var dependencia = new DependenciaDeEtapa(segunda, primeira);
        await using (var escopo = provider.CreateAsyncScope())
        {
            var s = escopo.ServiceProvider;
            await s.GetRequiredService<ISessaoDeAgenteRepository>().AdicionarAsync(sessao);
            await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(execucao);
            await s.GetRequiredService<IEtapaDaExecucaoRepository>().AdicionarAsync(primeira);
            await s.GetRequiredService<IEtapaDaExecucaoRepository>().AdicionarAsync(segunda);
            await s.GetRequiredService<IDependenciaDeEtapaRepository>().AdicionarAsync(dependencia);
            var eventos = s.GetRequiredService<IEventoDeOrquestracaoRepository>();
            await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao, TipoDeEventoDeOrquestracao.ExecucaoCriada, "{\"a\":\"1\"}", Agora));
            await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao, TipoDeEventoDeOrquestracao.EtapaTransitou, null, Agora, segunda));
            await eventos.AdicionarAsync(EventoDeOrquestracao.DaSessao(sessao, TipoDeEventoDeOrquestracao.TurnoIniciado, "T000009", null, Agora));
            await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        }

        await using (var escopo = provider.CreateAsyncScope())
        {
            var ledger = escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>();
            var lida = (await ledger.ObterExecucaoAsync(execucao.Numero))!;
            Assert.Equal((execucao.Codigo, EstadoDaExecucao.Pendente, "telegram", 1001L), (lida.Codigo, lida.Estado, lida.Canal, lida.IdDoUsuarioNoCanal));
            Assert.Equal([1, 2], lida.Etapas.Select(e => e.Numero));
            Assert.Equal((sessao.Id, "T000009"), (lida.Etapas[1].IdSessaoDeAgente, lida.Etapas[1].IdTurno));
            Assert.Equal(("gpt", "high", Dante.Domain.Agentes.AgentKind.Codex), (lida.Etapas[0].Modelo, lida.Etapas[0].Esforco, lida.Etapas[0].Agente));
            Assert.Equal(new DependenciaDeEtapaDto(segunda.Id, primeira.Id), Assert.Single(lida.Dependencias));
            Assert.Equal([TipoDeEventoDeOrquestracao.ExecucaoCriada, TipoDeEventoDeOrquestracao.EtapaTransitou], lida.Eventos.Select(e => e.Tipo));
            Assert.True(lida.Eventos[0].Sequencia > 0 && lida.Eventos[1].Sequencia > lida.Eventos[0].Sequencia);
            Assert.Equal((sessao.Id, "T000009"), (lida.Eventos[1].IdSessaoDeAgente, lida.Eventos[1].IdTurno));
            var lidaSessao = (await ledger.ObterSessaoAsync(sessao.Numero))!;
            Assert.Equal((sessao.Codigo, "opus", EstadoDaSessaoDeAgente.Iniciando), (lidaSessao.Codigo, lidaSessao.Modelo, lidaSessao.Estado));
            Assert.Equal([TipoDeEventoDeOrquestracao.EtapaTransitou, TipoDeEventoDeOrquestracao.TurnoIniciado],
                lidaSessao.Eventos.Select(e => e.Tipo));
            Assert.Null(await ledger.ObterExecucaoAsync(999_999));
        }
    }

    [PostgreSqlFact]
    public async Task BancoRecusaRelacoesInconsistentesMesmoSemPassarPeloDominio()
    {
        await using var banco = await LedgerAsync();
        using var provider = Provider(banco.ConnectionString);
        var a = ExecucoesDominioTests.Nova(await ReservarExecucaoAsync(provider));
        var b = ExecucoesDominioTests.Nova(await ReservarExecucaoAsync(provider));
        var etapaA = ExecucoesDominioTests.Etapa(a, 1);
        var etapaB = ExecucoesDominioTests.Etapa(b, 1);
        await using (var escopo = provider.CreateAsyncScope())
        {
            var s = escopo.ServiceProvider;
            await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(a);
            await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(b);
            await s.GetRequiredService<IEtapaDaExecucaoRepository>().AdicionarAsync(etapaA);
            await s.GetRequiredService<IEtapaDaExecucaoRepository>().AdicionarAsync(etapaB);
            await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        }
        await using var conexao = new NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        async Task<string?> Recusa(string sql)
        {
            await using var comando = new NpgsqlCommand(sql, conexao);
            try { await comando.ExecuteNonQueryAsync(); return null; }
            catch (PostgresException exception) { return exception.SqlState; }
        }
        // Dependência entre etapas de execuções diferentes: FK composta (etapa, execução).
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await Recusa(
            $"INSERT INTO orquestracao.dependencias_de_etapa (id, id_execucao, id_etapa, id_etapa_requerida) VALUES ('{Guid.NewGuid()}', '{a.Id}', '{etapaA.Id}', '{etapaB.Id}')"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await Recusa(
            $"INSERT INTO orquestracao.dependencias_de_etapa (id, id_execucao, id_etapa, id_etapa_requerida) VALUES ('{Guid.NewGuid()}', '{a.Id}', '{etapaA.Id}', '{etapaA.Id}')"));
        // Evento sem execução nem sessão, e evento com etapa de outra execução.
        Assert.Equal(PostgresErrorCodes.CheckViolation, await Recusa(
            $"INSERT INTO orquestracao.eventos (id, tipo, ocorrido_em) VALUES ('{Guid.NewGuid()}', 0, now())"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await Recusa(
            $"INSERT INTO orquestracao.eventos (id, tipo, id_execucao, id_etapa, ocorrido_em) VALUES ('{Guid.NewGuid()}', 2, '{a.Id}', '{etapaB.Id}', now())"));
        // Número repetido, etapa repetida na execução, chave de idempotência repetida.
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await Recusa(
            $"INSERT INTO orquestracao.execucoes (id, numero, tipo, estado, canal, id_do_usuario_no_canal, contexto, criada_em) VALUES ('{Guid.NewGuid()}', {a.Numero}, 0, 0, 'telegram', 1, '{{}}', now())"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await Recusa(
            $"INSERT INTO orquestracao.etapas_da_execucao (id, id_execucao, numero, tipo, estado, escrita, politica_de_retomada, tentativa, chave_de_idempotencia, criada_em) VALUES ('{Guid.NewGuid()}', '{a.Id}', 1, 0, 0, false, 0, 1, 'outra', now())"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await Recusa(
            $"INSERT INTO orquestracao.etapas_da_execucao (id, id_execucao, numero, tipo, estado, escrita, politica_de_retomada, tentativa, chave_de_idempotencia, criada_em) VALUES ('{Guid.NewGuid()}', '{a.Id}', 2, 0, 0, false, 0, 1, '{etapaA.ChaveDeIdempotencia}', now())"));
        // Execução com etapas não é apagada.
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await Recusa($"DELETE FROM orquestracao.execucoes WHERE id = '{a.Id}'"));
    }

    [PostgreSqlFact]
    public async Task XminImpedeSobrescritaSilenciosaEntreDuasGravacoesConcorrentes()
    {
        await using var banco = await LedgerAsync();
        using var provider = Provider(banco.ConnectionString);
        var numero = await ReservarExecucaoAsync(provider);
        await using (var escopo = provider.CreateAsyncScope())
        {
            await escopo.ServiceProvider.GetRequiredService<IExecucaoRepository>().AdicionarAsync(ExecucoesDominioTests.Nova(numero));
            await escopo.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        }
        await using var primeiro = provider.CreateAsyncScope();
        await using var segundo = provider.CreateAsyncScope();
        var a = (await primeiro.ServiceProvider.GetRequiredService<IExecucaoRepository>().ObterPorNumeroAsync(numero))!;
        var b = (await segundo.ServiceProvider.GetRequiredService<IExecucaoRepository>().ObterPorNumeroAsync(numero))!;
        a.Iniciar(Agora);
        b.Cancelar("concorrente", Agora);
        await primeiro.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        await Assert.ThrowsAsync<ConflitoDeConcorrenciaException>(() =>
            segundo.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync());

        await using var leitura = provider.CreateAsyncScope();
        var gravada = (await leitura.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>().ObterExecucaoAsync(numero))!;
        Assert.Equal((EstadoDaExecucao.EmExecucao, null), (gravada.Estado, gravada.Motivo));
    }

    [PostgreSqlFact]
    public async Task SequenciasNaoRepetemCodigosDepoisDeReiniciarOWorker()
    {
        await using var banco = await LedgerAsync();
        var vistos = new HashSet<string>();
        // Duas "vidas" do Worker: provider, espelho e registries novos, contadores locais zerados.
        for (var vida = 0; vida < 2; vida++)
        {
            await using var provider = Provider(banco.ConnectionString);
            await using var espelho = Espelho(provider);
            var jobs = new JobRegistry(espelho);
            for (var i = 0; i < 3; i++)
            {
                var (job, _) = await jobs.CreateAsync("Codex", JobExecutionContext.General("/general"), 1001, CancellationToken.None);
                Assert.True(vistos.Add(job.Id), job.Id);
                jobs.Complete(job.Id, AgentProcessStatus.Cancelled);
            }
            var drivers = new FakeSessionDriverFactory();
            await using (var sessoes = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, ledger: espelho))
            {
                var iniciada = await sessoes.StartAsync(new SessionStartRequest(1001, Dante.Domain.Agentes.AgentKind.Claude,
                    JobExecutionContext.General("/general")));
                Assert.True(vistos.Add(iniciada.Session!.Id), iniciada.Session.Id);
            }
            await espelho.DescarregarAsync();
        }
        Assert.Equal(6, vistos.Count(id => id.StartsWith('J')));
        await using var final = Provider(banco.ConnectionString);
        await using var escopo = final.CreateAsyncScope();
        var ledger = escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>();
        foreach (var id in vistos.Where(id => id.StartsWith('J')))
            Assert.Equal(EstadoDaExecucao.Cancelada, (await ledger.ObterExecucaoAsync(long.Parse(id[1..])))!.Estado);
    }

    [PostgreSqlFact]
    public async Task JobsEspelhadosSeguemOMesmoFluxoLegadoComEstadoEventosNoLedger()
    {
        await using var banco = await LedgerAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using var espelho = Espelho(provider);
        var jobs = new JobRegistry(espelho);
        var contexto = JobExecutionContext.Repository("@dante", "/repos/dante");

        var (ok, _) = await jobs.CreateAsync("Claude", contexto, 1001, CancellationToken.None, new AgentModelSelection("opus", "high"));
        Assert.True(jobs.TryStart(ok.Id));
        var concluido = jobs.Complete(ok.Id, AgentProcessStatus.Succeeded, 0);
        // Falha antes do início (anexos indisponíveis): Pendente → EmExecucao → Falhou no mesmo instante.
        var (antes, _) = await jobs.CreateAsync("Codex", contexto, 1001, CancellationToken.None);
        jobs.Complete(antes.Id, AgentProcessStatus.Failed, errorMessage: "Imagens indisponíveis.");
        var (cancelado, _) = await jobs.CreateAsync("Codex", contexto, 1001, CancellationToken.None);
        Assert.True(jobs.TryStart(cancelado.Id));
        Assert.True(jobs.TryCancel(cancelado.Id, out _));
        jobs.Complete(cancelado.Id, AgentProcessStatus.Failed, 137, "morto");
        await espelho.DescarregarAsync();

        // O contrato legado não mudou.
        Assert.Equal(JobStatus.Succeeded, concluido.Status);
        Assert.Equal(3, jobs.GetVisible().Count);
        await using var escopo = provider.CreateAsyncScope();
        var ledger = escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>();
        var e1 = (await ledger.ObterExecucaoAsync(long.Parse(ok.Id[1..])))!;
        Assert.Equal((ok.Id, TipoDeExecucao.Avulsa, EstadoDaExecucao.Concluida), (e1.Codigo, e1.Tipo, e1.Estado));
        Assert.Equal("@dante", Json(e1.Contexto)["alias"]);
        Assert.Null(e1.Objetivo);
        var etapa = Assert.Single(e1.Etapas);
        Assert.Equal((EstadoDaEtapa.Concluida, 0, true, PoliticaDeRetomada.ComConfirmacao, "opus", "high"),
            (etapa.Estado, etapa.CodigoDeSaida, etapa.Escrita, etapa.PoliticaDeRetomada, etapa.Modelo, etapa.Esforco));
        Assert.Equal([TipoDeEventoDeOrquestracao.ExecucaoCriada, TipoDeEventoDeOrquestracao.ExecucaoTransitou,
            TipoDeEventoDeOrquestracao.EtapaTransitou, TipoDeEventoDeOrquestracao.EtapaTransitou,
            TipoDeEventoDeOrquestracao.ExecucaoTransitou], e1.Eventos.Select(e => e.Tipo));
        Assert.Equal(("EmExecucao", "Concluida"), (Json(e1.Eventos[^1].Dados!)["de"], Json(e1.Eventos[^1].Dados!)["para"]));
        var e2 = (await ledger.ObterExecucaoAsync(long.Parse(antes.Id[1..])))!;
        Assert.Equal((EstadoDaExecucao.Falhou, "Imagens indisponíveis."), (e2.Estado, e2.Erro));
        var e3 = (await ledger.ObterExecucaoAsync(long.Parse(cancelado.Id[1..])))!;
        Assert.Equal((EstadoDaExecucao.Cancelada, 137), (e3.Estado, e3.Etapas[0].CodigoDeSaida));
    }

    [PostgreSqlFact]
    public async Task SessoesEspelhamMetadadosEEventosChaveSemDeltasNemTexto()
    {
        await using var banco = await LedgerAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using var espelho = Espelho(provider);
        var drivers = new FakeSessionDriverFactory();
        var contexto = JobExecutionContext.Repository("@dante", "/repos/dante");
        string encerradaId, interrompidaId;
        await using (var sessoes = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, ledger: espelho))
        {
            var iniciada = await sessoes.StartAsync(new SessionStartRequest(1001, Dante.Domain.Agentes.AgentKind.Claude, contexto,
                Profile: Dante.Domain.Agentes.AgentPermissionProfile.Plan, ModelSelection: new AgentModelSelection("opus")));
            encerradaId = iniciada.Session!.Id;
            var driver = drivers.Created.Single();
            var turno = await sessoes.SubmitAsync(1001, null, "segredo do usuário");
            driver.Emit(new MessageDeltaEvent("m1", "texto parcial secreto"));
            driver.Emit(new ToolStartedEvent("i1", AgentToolKind.Command, "cat ~/.ssh/id_rsa") { Server = "s", ToolName = "t" });
            driver.Emit(new ToolCompletedEvent("i1", AgentToolKind.Command, true, "saída secreta"));
            driver.Emit(new MessageCompletedEvent("m1", "resposta secreta"));
            driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed, Usage: new AgentTokenUsage(10, 20, 5)));
            await Eventually(() => sessoes.GetActive(1001)!.State == AgentSessionState.Idle);
            Assert.True((await sessoes.CloseAsync(1001, encerradaId)).Accepted);
            interrompidaId = (await sessoes.StartAsync(new SessionStartRequest(1001, Dante.Domain.Agentes.AgentKind.Codex, contexto))).Session!.Id;
            Assert.Equal("T000001", turno.TurnId);
        }
        // O encerramento do host interrompe a segunda sessão; o espelho grava antes de terminar.
        await espelho.DescarregarAsync();

        await using var escopo = provider.CreateAsyncScope();
        var ledger = escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>();
        var encerrada = (await ledger.ObterSessaoAsync(long.Parse(encerradaId[1..])))!;
        Assert.Equal((EstadoDaSessaoDeAgente.Encerrada, Dante.Domain.Agentes.AgentPermissionProfile.Plan, "opus", 1001L),
            (encerrada.Estado, encerrada.Perfil, encerrada.Modelo, encerrada.IdDoDono));
        Assert.NotNull(encerrada.IdUpstream);
        Assert.Equal([TipoDeEventoDeOrquestracao.SessaoCriada, TipoDeEventoDeOrquestracao.SessaoTransitou,
            TipoDeEventoDeOrquestracao.FerramentaIniciada, TipoDeEventoDeOrquestracao.FerramentaConcluida,
            TipoDeEventoDeOrquestracao.TurnoConcluido, TipoDeEventoDeOrquestracao.SessaoTransitou],
            encerrada.Eventos.Select(e => e.Tipo));
        var concluido = encerrada.Eventos.Single(e => e.Tipo == TipoDeEventoDeOrquestracao.TurnoConcluido);
        Assert.Equal("T000001", concluido.IdTurno);
        Assert.Equal(("Completed", "10", "20", "5"), (Json(concluido.Dados!)["resultado"], Json(concluido.Dados!)["tokensDeEntrada"],
            Json(concluido.Dados!)["tokensDeSaida"], Json(concluido.Dados!)["tokensEmCache"]));
        var todos = string.Join("|", encerrada.Eventos.Select(e => e.Dados));
        Assert.DoesNotContain("secret", todos);
        Assert.DoesNotContain("ssh", todos);
        var interrompida = (await ledger.ObterSessaoAsync(long.Parse(interrompidaId[1..])))!;
        Assert.Equal(EstadoDaSessaoDeAgente.Interrompida, interrompida.Estado);
        Assert.StartsWith("O Worker foi encerrado.", interrompida.MotivoDoFim);
    }

    // Review Codex do PR: no Host.Run o provider já está descartado quando os singletons são descartados; as sessões
    // interrompidas pelo encerramento precisam ser gravadas no StopAsync, pelo ciclo real do host.
    [PostgreSqlFact]
    public async Task EncerramentoDoHostGravaAsSessoesInterrompidasAntesDeDescartarOProvider()
    {
        await using var banco = await LedgerAsync();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Dante"] = banco.ConnectionString });
        Dante.Worker.WorkerComposition.AddLedgerDeExecucoes(builder.Services);
        builder.Services.AddApplication().AddInfrastructure(builder.Configuration);
        var drivers = new FakeSessionDriverFactory();
        builder.Services.AddSingleton(provider => new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance,
            ledger: provider.GetRequiredService<EspelhoDoLedger>()));
        string id;
        using (var host = builder.Build())
        {
            await host.StartAsync();
            var sessoes = host.Services.GetRequiredService<SessionRegistry>();
            id = (await sessoes.StartAsync(new SessionStartRequest(1001, Dante.Domain.Agentes.AgentKind.Claude,
                JobExecutionContext.General("/general")))).Session!.Id;
            await host.StopAsync();
        }

        await using var leitura = Provider(banco.ConnectionString);
        await using var escopo = leitura.CreateAsyncScope();
        var sessao = (await escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>()
            .ObterSessaoAsync(long.Parse(id[1..])))!;
        Assert.Equal(EstadoDaSessaoDeAgente.Interrompida, sessao.Estado);
        Assert.StartsWith("O Worker foi encerrado.", sessao.MotivoDoFim);
    }

    // Review Codex do PR: depois de o contador local passar à frente da sequência (banco fora, ou reservas
    // concluídas fora de ordem), a próxima reserva avança a sequência e o espelho volta a valer.
    [PostgreSqlFact]
    public async Task EspelhoVoltaAValerDepoisDeOContadorLocalPassarAFrenteDaSequencia()
    {
        await using var banco = await LedgerAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using var espelho = Espelho(provider);
        var jobs = new JobRegistry(espelho);
        var contexto = JobExecutionContext.General("/general");
        var (primeiro, _) = await jobs.CreateAsync("Codex", contexto, 1001, CancellationToken.None);
        // Contador local à frente da sequência, como durante uma indisponibilidade.
        var locais = Enumerable.Range(0, 3).Select(_ => jobs.Create("Codex", contexto, CancellationToken.None).Snapshot.Id).ToArray();
        var (depois, _) = await jobs.CreateAsync("Codex", contexto, 1001, CancellationToken.None);
        var (seguinte, _) = await jobs.CreateAsync("Codex", contexto, 1001, CancellationToken.None);
        await espelho.DescarregarAsync();
        var ids = new[] { primeiro.Id }.Concat(locais).Append(depois.Id).Append(seguinte.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.True(long.Parse(depois.Id[1..]) > long.Parse(locais[^1][1..]));

        var geradores = new SessionIdGenerator();
        for (var i = 0; i < 5; i++) geradores.NextSessionId();
        await using (var sessoes = new SessionRegistry(new FakeSessionDriverFactory(), NullLogger<SessionRegistry>.Instance,
            ids: geradores, ledger: espelho))
        {
            var sessao = (await sessoes.StartAsync(new SessionStartRequest(1001, Dante.Domain.Agentes.AgentKind.Claude, contexto))).Session!;
            Assert.Equal("S000006", sessao.Id);
        }
        await espelho.DescarregarAsync();

        await using var escopo = provider.CreateAsyncScope();
        var ledger = escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>();
        foreach (var id in new[] { primeiro.Id, depois.Id, seguinte.Id })
            Assert.NotNull(await ledger.ObterExecucaoAsync(long.Parse(id[1..])));
        foreach (var id in locais) Assert.Null(await ledger.ObterExecucaoAsync(long.Parse(id[1..])));
        Assert.NotNull(await ledger.ObterSessaoAsync(6));
        // A reconciliação nunca recua a sequência: uma reserva com piso menor continua acima da última.
        var alta = await ledger.ReservarNumeroDeExecucaoAsync(500);
        Assert.Equal(501, alta);
        Assert.Equal(502, await ledger.ReservarNumeroDeExecucaoAsync(10));
    }

    // Segunda rodada do Codex: nextval e setval não são atômicos juntos. A reserva toma um advisory lock de transação:
    // com o lock ocupado por outra conexão, ela espera; reservas concorrentes nunca repetem nem recuam a sequência.
    [PostgreSqlFact]
    public async Task ReservaComReconciliacaoESerializadaEntreConexoes()
    {
        await using var banco = await LedgerAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using (var bloqueio = new NpgsqlConnection(banco.ConnectionString))
        {
            await bloqueio.OpenAsync();
            await using var transacao = await bloqueio.BeginTransactionAsync();
            await using (var lockar = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtext('orquestracao.seq_numero_da_execucao'))", bloqueio, transacao))
                await lockar.ExecuteNonQueryAsync();
            var reserva = Task.Run(async () =>
            {
                await using var escopo = provider.CreateAsyncScope();
                return await escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>().ReservarNumeroDeExecucaoAsync(500);
            });
            await Task.Delay(500);
            Assert.False(reserva.IsCompleted);
            await transacao.CommitAsync();
            Assert.Equal(501, await reserva);
        }

        var pisos = Enumerable.Range(0, 40).Select(i => i % 3 == 0 ? 0L : 600 + i * 5L).ToArray();
        var numeros = await Task.WhenAll(pisos.Select(piso => Task.Run(async () =>
        {
            await using var escopo = provider.CreateAsyncScope();
            return (Piso: piso, Numero: await escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>()
                .ReservarNumeroDeExecucaoAsync(piso));
        })));
        Assert.Equal(numeros.Length, numeros.Select(n => n.Numero).Distinct().Count());
        Assert.All(numeros, n => Assert.True(n.Numero > n.Piso && n.Numero > 501, $"{n.Numero} <= {n.Piso}"));
    }

    [Fact]
    public async Task BancoIndisponivelMantemJobsESessoesNoComportamentoLegado()
    {
        // Porta sem servidor: a reserva falha rápido, o contador local assume e nada é espelhado.
        await using var provider = Provider("Host=127.0.0.1;Port=1;Database=inexistente;Timeout=1");
        await using var espelho = Espelho(provider);
        var jobs = new JobRegistry(espelho);
        var (job, _) = await jobs.CreateAsync("Codex", JobExecutionContext.General("/general"), 1001, CancellationToken.None);
        Assert.Equal("J000001", job.Id);
        Assert.True(jobs.TryStart(job.Id));
        Assert.Equal(JobStatus.Succeeded, jobs.Complete(job.Id, AgentProcessStatus.Succeeded).Status);
        var drivers = new FakeSessionDriverFactory();
        await using var sessoes = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, ledger: espelho);
        var sessao = await sessoes.StartAsync(new SessionStartRequest(1001, Dante.Domain.Agentes.AgentKind.Claude,
            JobExecutionContext.General("/general")));
        Assert.Equal(("S000001", true), (sessao.Session!.Id, sessao.Accepted));
        await espelho.DescarregarAsync();
    }

    internal static ServiceProvider Provider(string connectionString) => new ServiceCollection().AddLogging()
        .AddApplication().AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Dante"] = connectionString }).Build())
        .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    private static EspelhoDoLedger Espelho(IServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<EspelhoDoLedger>.Instance);

    private static async Task<Banco> LedgerAsync()
    {
        var banco = await Banco.CriarAsync(migrar: false);
        await using var contexto = new OrquestracaoDbContext(OrquestracaoDbContextFactory.Opcoes(banco.ConnectionString));
        await using var brain = banco.Contexto();
        await new AdministracaoDoBanco(brain, contexto).MigrarOrquestracaoAsync();
        return banco;
    }

    private static async Task<long> ReservarExecucaoAsync(IServiceProvider provider)
    {
        await using var escopo = provider.CreateAsyncScope();
        return await escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>().ReservarNumeroDeExecucaoAsync();
    }

    private static async Task<long> ReservarSessaoAsync(IServiceProvider provider)
    {
        await using var escopo = provider.CreateAsyncScope();
        return await escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>().ReservarNumeroDeSessaoAsync();
    }

    private static async Task<string[]> Tabelas(Banco banco, string schema)
    {
        await using var conexao = new NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema ORDER BY table_name", conexao);
        comando.Parameters.AddWithValue("schema", schema);
        var nomes = new List<string>();
        await using var leitor = await comando.ExecuteReaderAsync();
        while (await leitor.ReadAsync()) nomes.Add(leitor.GetString(0));
        return [.. nomes];
    }

    private static Dictionary<string, string?> Json(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement
        .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == System.Text.Json.JsonValueKind.String
            ? p.Value.GetString() : p.Value.GetRawText());

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "A condição esperada não ocorreu a tempo.");
            await Task.Delay(10);
        }
    }
}
