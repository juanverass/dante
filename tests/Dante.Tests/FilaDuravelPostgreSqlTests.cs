using System.Collections.Concurrent;
using Dante.Application;
using Dante.Application.Comum;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure;
using Dante.Infrastructure.Banco;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Fila;
using Dante.Worker.Execucoes;
using Dante.Worker.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Dante.Tests;

// #300: fila durável (AD-67) contra PostgreSQL real e descartável (DANTE_TEST_POSTGRES). Cada teste cria e remove um
// banco aleatório; nada toca o banco canônico, e nenhum teste lista ou para unidades systemd reais (IUnidadesDeContencao
// é sempre falsa aqui).
[Collection("FilaDuravel")]
public sealed class FilaDuravelPostgreSqlTests
{
    private static readonly DateTimeOffset Inicio = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    [PostgreSqlFact]
    public async Task OutboxGravaExecucaoEtapaETrabalhoNaMesmaTransacaoOuNada()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var job = await EnfileirarJobAsync(provider, "telegram:1:10");
        Assert.True(job.Novo);
        await using (var escopo = provider.CreateAsyncScope())
        {
            var trabalho = (await escopo.ServiceProvider.GetRequiredService<IFilaDeTrabalho>().ObterPorChaveAsync("telegram:1:10"))!;
            Assert.Equal((EstadoDoTrabalho.Pendente, job.IdExecucao, TipoDeTrabalho.AvancarExecucao, 0L),
                (trabalho.Estado, trabalho.IdExecucao, trabalho.Tipo, trabalho.Geracao));
            Assert.DoesNotContain("pedido", trabalho.Dados);
            var execucao = (await escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>().ObterExecucaoAsync(job.Numero))!;
            Assert.Equal((EstadoDaExecucao.Pendente, trabalho.IdEtapa), (execucao.Estado, execucao.Etapas.Single().Id));
            Assert.Equal(TipoDeEventoDeOrquestracao.ExecucaoCriada, Assert.Single(execucao.Eventos).Tipo);
        }

        // Chave repetida pelo caminho de domínio (o de um produtor como a /tarefa): o salvamento inteiro falha e a
        // execução nova não existe.
        long numero;
        await using (var escopo = provider.CreateAsyncScope())
        {
            var s = escopo.ServiceProvider;
            numero = await s.GetRequiredService<IExecucaoRepository>().ReservarNumeroAsync();
            var execucao = new Execucao(numero, TipoDeExecucao.Avulsa, "telegram", 1001, "{}", null, Inicio);
            var etapa = new EtapaDaExecucao(execucao, 1, TipoDeEtapa.ProcessoAvulso, false, PoliticaDeRetomada.Automatica, Inicio);
            await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(execucao);
            await s.GetRequiredService<IEtapaDaExecucaoRepository>().AdicionarAsync(etapa);
            await s.GetRequiredService<IFilaDeTrabalho>().EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(execucao, etapa, "telegram:1:10", Inicio));
            await Assert.ThrowsAsync<ConflitoDeConcorrenciaException>(() =>
                s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync());
        }
        await using (var escopo = provider.CreateAsyncScope())
            Assert.Null(await escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>().ObterExecucaoAsync(numero));
        Assert.Equal(1L, await Escalar(banco, "SELECT count(*) FROM orquestracao.trabalhos_da_fila"));
    }

    [PostgreSqlFact]
    public async Task MesmaChaveProduzNoMaximoUmaExecucaoMesmoComProdutoresConcorrentes()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var primeira = await EnfileirarJobAsync(provider, "telegram:1:20");
        var repetida = await EnfileirarJobAsync(provider, "telegram:1:20");
        Assert.Equal((primeira.Numero, primeira.IdExecucao, false), (repetida.Numero, repetida.IdExecucao, repetida.Novo));

        // Oito produtores com números diferentes e a mesma chave nova, ao mesmo tempo.
        var largada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tentativas = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await largada.Task;
            try { return (await EnfileirarJobAsync(provider, "telegram:1:21")).Numero; }
            catch (ConflitoDeConcorrenciaException) { return 0L; }
        })).ToArray();
        largada.SetResult();
        var numeros = await Task.WhenAll(tentativas);
        Assert.Equal(1L, await Escalar(banco, "SELECT count(*) FROM orquestracao.trabalhos_da_fila WHERE chave_de_deduplicacao = 'telegram:1:21'"));
        // Só a execução do trabalho que venceu existe; as das tentativas recusadas foram desfeitas com o trabalho.
        Assert.Equal(2L, await Escalar(banco, "SELECT count(*) FROM orquestracao.execucoes"));
        var vencedora = await Escalar(banco, "SELECT e.numero FROM orquestracao.execucoes e JOIN orquestracao.trabalhos_da_fila t ON t.id_execucao = e.id WHERE t.chave_de_deduplicacao = 'telegram:1:21'");
        Assert.All(numeros.Where(n => n != 0), n => Assert.Equal(vencedora, n));
    }

    [PostgreSqlFact]
    public async Task ProcessadoresConcorrentesNuncaReivindicamOMesmoTrabalho()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using (var escopo = provider.CreateAsyncScope())
        {
            var fila = escopo.ServiceProvider.GetRequiredService<IFilaDeTrabalho>();
            for (var i = 0; i < 80; i++)
                await fila.EnfileirarAsync(new TrabalhoDaFila(TipoDeTrabalho.AvancarExecucao, $"carga:{i}", fila.Agora));
            await escopo.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        }
        var largada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processadores = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            var eu = Processador();
            var meus = new List<(Guid Id, long Geracao)>();
            await largada.Task;
            while (await ReivindicarAsync(provider, eu) is { } trabalho) meus.Add((trabalho.Id, trabalho.Geracao));
            return meus;
        })).ToArray();
        largada.SetResult();
        var todos = (await Task.WhenAll(processadores)).SelectMany(m => m).ToArray();
        Assert.Equal(80, todos.Length);
        Assert.Equal(80, todos.Select(t => t.Id).Distinct().Count());
        Assert.All(todos, t => Assert.Equal(1, t.Geracao));
        Assert.Equal(80L, await Escalar(banco, "SELECT count(*) FROM orquestracao.trabalhos_da_fila WHERE estado = 1 AND geracao = 1 AND tentativas = 1"));
    }

    [PostgreSqlFact]
    public async Task HeartbeatRenovaNaJanelaELeaseVencidoVoltaComNovaGeracaoEFencing()
    {
        await using var banco = await FilaAsync();
        var relogio = new RelogioManual(Inicio);
        await using var provider = Provider(banco.ConnectionString, relogio);
        await CriarAsync(provider, "lease:1");
        var a = Processador();
        var b = Processador();
        var deA = (await ReivindicarAsync(provider, a))!;
        Assert.Equal((1L, Inicio + Lease), (deA.Geracao, deA.LeaseAte));

        relogio.Avancar(TimeSpan.FromSeconds(20));
        Assert.True(await FilaAsync(provider, f => f.RenovarAsync(deA.Posse, Lease)));
        relogio.Avancar(TimeSpan.FromSeconds(41));
        // T+61: o lease original venceria agora, mas o heartbeat o levou a T+80.
        Assert.Null(await ReivindicarAsync(provider, b));
        Assert.Equal(Inicio + TimeSpan.FromSeconds(80), await FilaAsync(provider, f => f.ProximaDisponibilidadeAsync()));

        relogio.Avancar(TimeSpan.FromSeconds(20));
        var deB = (await ReivindicarAsync(provider, b))!;
        Assert.Equal((deA.Id, 2L, 2), (deB.Id, deB.Geracao, deB.Tentativas));
        // O processador antigo perdeu a posse: não renova, não devolve e não grava nada.
        Assert.False(await FilaAsync(provider, f => f.RenovarAsync(deA.Posse, Lease)));
        Assert.False(await FilaAsync(provider, f => f.DevolverAsync(deA.Posse, relogio.GetUtcNow(), "x", true)));
        var gravou = false;
        await Assert.ThrowsAsync<PosseDoTrabalhoPerdidaException>(() => FilaAsync(provider, f => f.GravarComPosseAsync(deA.Posse,
            _ => { gravou = true; return Task.CompletedTask; }, EstadoDoTrabalho.Concluido)));
        Assert.False(gravou);
        Assert.True(await FilaAsync(provider, f => f.RenovarAsync(deB.Posse, Lease)));

        // Heartbeat que não chega a tempo: o lease vence e a posse se perde mesmo sem outro processador.
        relogio.Avancar(Lease + TimeSpan.FromSeconds(1));
        Assert.False(await FilaAsync(provider, f => f.RenovarAsync(deB.Posse, Lease)));
        await Assert.ThrowsAsync<PosseDoTrabalhoPerdidaException>(() => FilaAsync(provider, f => f.GravarComPosseAsync(deB.Posse,
            null, EstadoDoTrabalho.Concluido)));
    }

    [PostgreSqlFact]
    public async Task ProcessadorQuePerdeuOLeaseNaoPersisteResultadoNemConcluiEtapa()
    {
        await using var banco = await FilaAsync();
        var relogio = new RelogioManual(Inicio);
        await using var provider = Provider(banco.ConnectionString, relogio);
        var job = await EnfileirarJobAsync(provider, "telegram:1:30");
        var antigo = (await ReivindicarAsync(provider, Processador()))!;
        var alvo = (await AppAsync(provider, app => app.AvaliarAsync(antigo))).Alvo!;
        await AppAsync(provider, app => app.IniciarEtapaAsync(antigo.Posse, alvo.IdEtapa));

        relogio.Avancar(Lease + TimeSpan.FromSeconds(1));
        var novo = (await ReivindicarAsync(provider, Processador()))!;
        await Assert.ThrowsAsync<PosseDoTrabalhoPerdidaException>(() => AppAsync(provider, app =>
            app.ConcluirEtapaAsync(antigo.Posse, alvo.IdEtapa, new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida, 0))));
        var execucao = await ExecucaoAsync(provider, job.Numero);
        Assert.Equal((EstadoDaExecucao.EmExecucao, EstadoDaEtapa.EmExecucao), (execucao.Estado, execucao.Etapas[0].Estado));

        // Quem tem a posse vê a tentativa anterior interrompida: o job (escrita, ComConfirmacao) não é repetido.
        var avaliacao = await AppAsync(provider, app => app.AvaliarAsync(novo));
        Assert.Equal(AcaoDoTrabalho.Interromper, avaliacao.Acao);
        var interrupcao = await AppAsync(provider, app => app.InterromperAsync(novo.Posse, avaliacao.Alvo!, avaliacao.Motivo!));
        Assert.Equal(EstadoDaExecucao.Interrompida, interrupcao.NovoEstado);
        Assert.Equal(EstadoDoTrabalho.Descartado, (await TrabalhoAsync(provider, "telegram:1:30")).Estado);
        Assert.Null(await ReivindicarAsync(provider, Processador()));
    }

    [PostgreSqlFact]
    public async Task BackoffEDisponivelEmSaoRespeitados()
    {
        await using var banco = await FilaAsync();
        var relogio = new RelogioManual(Inicio);
        await using var provider = Provider(banco.ConnectionString, relogio);
        await CriarAsync(provider, "timer:1", disponivelEm: Inicio + TimeSpan.FromSeconds(30));
        Assert.Null(await ReivindicarAsync(provider, Processador()));
        Assert.Equal(Inicio + TimeSpan.FromSeconds(30), await FilaAsync(provider, f => f.ProximaDisponibilidadeAsync()));

        relogio.Avancar(TimeSpan.FromSeconds(30));
        var primeira = (await ReivindicarAsync(provider, Processador()))!;
        var backoff = TrabalhoDaFila.Backoff(primeira.Tentativas, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10));
        Assert.True(await FilaAsync(provider, f => f.DevolverAsync(primeira.Posse, relogio.GetUtcNow() + backoff, "Npgsql", true)));
        relogio.Avancar(backoff - TimeSpan.FromSeconds(1));
        Assert.Null(await ReivindicarAsync(provider, Processador()));
        relogio.Avancar(TimeSpan.FromSeconds(1));
        var segunda = (await ReivindicarAsync(provider, Processador()))!;
        Assert.Equal((2, 2L), (segunda.Tentativas, segunda.Geracao));
        Assert.Equal("Npgsql", (await TrabalhoAsync(provider, "timer:1")).UltimoErro);

        // Slot ocupado ou espera sem culpa do trabalho: devolvido sem contar tentativa.
        Assert.True(await FilaAsync(provider, f => f.DevolverAsync(segunda.Posse, relogio.GetUtcNow(), null, false)));
        Assert.Equal(2, (await ReivindicarAsync(provider, Processador()))!.Tentativas);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(10)],
            new[] { 1, 2, 3, 30 }.Select(t => TrabalhoDaFila.Backoff(t, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10))));
    }

    [PostgreSqlTheory]
    [InlineData(PoliticaDeRetomada.Automatica, false)]
    [InlineData(PoliticaDeRetomada.ComConfirmacao, true)]
    [InlineData(PoliticaDeRetomada.Nunca, false)]
    public async Task PoliticaDeRetomadaDecideOQueAconteceComAEtapaInterrompida(PoliticaDeRetomada politica, bool escrita)
    {
        await using var banco = await FilaAsync();
        var relogio = new RelogioManual(Inicio);
        await using var provider = Provider(banco.ConnectionString, relogio);
        var (execucao, etapa) = await CriarAsync(provider, "politica:1", politica, escrita);
        var primeira = (await ReivindicarAsync(provider, Processador()))!;
        await AppAsync(provider, app => app.IniciarEtapaAsync(primeira.Posse, etapa.Id));
        // O processo morre com a etapa em execução; o lease vence.
        relogio.Avancar(Lease + TimeSpan.FromSeconds(1));
        var segunda = (await ReivindicarAsync(provider, Processador()))!;
        var avaliacao = await AppAsync(provider, app => app.AvaliarAsync(segunda));
        Assert.Equal(AcaoDoTrabalho.Interromper, avaliacao.Acao);
        var interrupcao = await AppAsync(provider, app => app.InterromperAsync(segunda.Posse, avaliacao.Alvo!, avaliacao.Motivo!));
        var lida = await ExecucaoAsync(provider, execucao.Numero);
        var proxima = await ReivindicarAsync(provider, Processador());
        switch (politica)
        {
            case PoliticaDeRetomada.Automatica:
                // Leitura idempotente: nova tentativa, de volta à fila.
                Assert.Equal((EstadoDaExecucao.Pendente, EstadoDaEtapa.Pronta, 2), (lida.Estado, lida.Etapas[0].Estado, lida.Etapas[0].Tentativa));
                Assert.Equal(EstadoDaExecucao.Pendente, interrupcao.NovoEstado);
                Assert.NotNull(proxima);
                Assert.Equal(AcaoDoTrabalho.Executar, (await AppAsync(provider, app => app.AvaliarAsync(proxima!))).Acao);
                break;
            case PoliticaDeRetomada.ComConfirmacao:
                // Escrita: nada volta à fila sem confirmação explícita.
                Assert.Equal((EstadoDaExecucao.Interrompida, EstadoDaEtapa.Interrompida), (lida.Estado, lida.Etapas[0].Estado));
                Assert.Null(proxima);
                relogio.Avancar(TimeSpan.FromHours(1));
                Assert.Null(await ReivindicarAsync(provider, Processador()));
                Assert.True(await AppAsync(provider, app => app.ConfirmarRetomadaAsync(execucao.Numero)));
                Assert.False(await AppAsync(provider, app => app.ConfirmarRetomadaAsync(execucao.Numero)));
                var confirmada = (await ReivindicarAsync(provider, Processador()))!;
                Assert.Equal(AcaoDoTrabalho.Executar, (await AppAsync(provider, app => app.AvaliarAsync(confirmada))).Acao);
                Assert.Equal(2, (await ExecucaoAsync(provider, execucao.Numero)).Etapas[0].Tentativa);
                break;
            default:
                // Ação externa de resultado incerto: bloqueada, nunca repetida automaticamente.
                Assert.Equal((EstadoDaExecucao.Bloqueada, FilaDeExecucoesAppService.MotivoDeResultadoIncerto), (lida.Estado, lida.Motivo));
                Assert.Equal(EstadoDaEtapa.Interrompida, lida.Etapas[0].Estado);
                Assert.Null(proxima);
                relogio.Avancar(TimeSpan.FromDays(1));
                Assert.Null(await ReivindicarAsync(provider, Processador()));
                break;
        }
        // Auditável: cada transição virou evento no ledger.
        Assert.Contains(lida.Eventos, e => e.Tipo == TipoDeEventoDeOrquestracao.EtapaTransitou && e.Dados!.Contains("\"Interrompida\""));
    }

    [PostgreSqlFact]
    public async Task RecuperacaoParaSoUnidadesRegistradasDeBootAnteriorENuncaAsDesconhecidas()
    {
        await using var banco = await FilaAsync();
        var anterior = Processador();
        var atual = Processador() with { Host = anterior.Host };
        string U(int i) => $"dante-agent-{new Guid(i, 0, 0, new byte[8]):N}.scope";
        var unidades = new UnidadesFalsas(U(1), U(2), U(3), U(4), U(5), "dante-agent-x.scope");
        await using var provider = Provider(banco.ConnectionString, unidades: unidades);
        var viva = await SessaoAsync(provider, anterior.Boot, U(1));
        var doBootAtual = await SessaoAsync(provider, atual.Boot, U(2));
        var encerrada = await SessaoAsync(provider, anterior.Boot, U(3), encerrar: true);
        var semBoot = await SessaoAsync(provider, null, U(4));
        // Trabalho reivindicado pelo boot anterior deste host volta à fila; o de outro host continua com o dono.
        await CriarAsync(provider, "boot:1");
        await CriarAsync(provider, "boot:2");
        Assert.NotNull(await ReivindicarAsync(provider, anterior));
        Assert.NotNull(await ReivindicarAsync(provider, Processador("outro-host")));

        RelatorioDeRecuperacaoDto relatorio;
        await using (var escopo = provider.CreateAsyncScope())
            relatorio = await escopo.ServiceProvider.GetRequiredService<IRecuperacaoDeExecucoesAppService>().RecuperarAsync(atual);

        Assert.Equal([U(1), U(3)], unidades.Paradas);
        Assert.Equal([U(1), U(3)], relatorio.UnidadesParadas);
        Assert.Equal([U(2), U(4), U(5), "dante-agent-x.scope"], relatorio.UnidadesDesconhecidas);
        Assert.Equal((false, 2, 1), (relatorio.ListagemIndisponivel, relatorio.SessoesInterrompidas, relatorio.TrabalhosDevolvidos));
        await using (var escopo = provider.CreateAsyncScope())
        {
            var ledger = escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>();
            Assert.Equal(EstadoDaSessaoDeAgente.Interrompida, (await ledger.ObterSessaoAsync(viva))!.Estado);
            Assert.Equal(EstadoDaSessaoDeAgente.Interrompida, (await ledger.ObterSessaoAsync(semBoot))!.Estado);
            Assert.Equal(EstadoDaSessaoDeAgente.Ativa, (await ledger.ObterSessaoAsync(doBootAtual))!.Estado);
            Assert.Equal(EstadoDaSessaoDeAgente.Encerrada, (await ledger.ObterSessaoAsync(encerrada))!.Estado);
            Assert.Equal(RecuperacaoDeExecucoesAppService.MotivoDaSessaoInterrompida, (await ledger.ObterSessaoAsync(viva))!.MotivoDoFim);
        }
        Assert.Equal(1L, await Escalar(banco, "SELECT count(*) FROM orquestracao.trabalhos_da_fila WHERE estado = 0"));
        Assert.Equal(1L, await Escalar(banco, "SELECT count(*) FROM orquestracao.trabalhos_da_fila WHERE estado = 1 AND trabalhador LIKE 'outro-host:%'"));

        // Sem listagem do gerenciador, nada é parado; o resto da recuperação continua.
        var cegas = new UnidadesFalsas(U(1)) { ListagemFalha = true };
        await using var outro = Provider(banco.ConnectionString, unidades: cegas);
        await using (var escopo = outro.CreateAsyncScope())
            relatorio = await escopo.ServiceProvider.GetRequiredService<IRecuperacaoDeExecucoesAppService>().RecuperarAsync(Processador());
        Assert.Empty(cegas.Paradas);
        Assert.True(relatorio.ListagemIndisponivel);
    }

    [PostgreSqlFact]
    public async Task SessaoEspelhadaRegistraBootEUnidadeDeContencao()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var identidade = Processador();
        await using var espelho = new EspelhoDoLedger(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EspelhoDoLedger>.Instance, identidade: identidade);
        var drivers = new FakeSessionDriverFactory { Configure = d => d.UnidadeDeContencao = "dante-agent-0123456789abcdef0123456789abcdef.scope" };
        string id;
        await using (var sessoes = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, ledger: espelho))
        {
            id = (await sessoes.StartAsync(new SessionStartRequest(1001, AgentKind.Claude, JobExecutionContext.General("/g")))).Session!.Id;
            await espelho.DescarregarAsync();
            await using var escopo = provider.CreateAsyncScope();
            var sessao = (await escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>().ObterSessaoAsync(long.Parse(id[1..])))!;
            Assert.Equal((identidade.Boot, "dante-agent-0123456789abcdef0123456789abcdef.scope"), (sessao.BootDoWorker, sessao.UnidadeDeContencao));
        }
    }

    [PostgreSqlFact]
    public async Task VariosProcessadoresReaisExecutamCadaTrabalhoUmaUnicaVez()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var executor = ExecutorDeTeste.QueConclui(async () => await Task.Delay(Random.Shared.Next(1, 20)));
        // Hosts distintos: a recuperação de um nunca devolve o que outro reivindicou.
        var processadores = Enumerable.Range(0, 3)
            .Select(i => CriarProcessador(provider, Processador($"host-{i}"), Rapidas with { Slots = 2 }, executor)).ToArray();
        foreach (var p in processadores) await p.StartAsync(CancellationToken.None);
        try
        {
            foreach (var p in processadores) await p.Recuperacao.WaitAsync(TimeSpan.FromSeconds(10));
            var numeros = new List<long>();
            for (var i = 0; i < 30; i++) numeros.Add((await CriarAsync(provider, $"carga:{i}")).Execucao.Numero);
            await Eventually(async () => await Escalar(banco, "SELECT count(*) FROM orquestracao.execucoes WHERE estado = 7") == 30);
            Assert.Equal(numeros.Order(), executor.Execucoes.Keys.Order());
            Assert.All(executor.Execucoes.Values, n => Assert.Equal(1, n));
            Assert.Equal(30L, await Escalar(banco, "SELECT count(*) FROM orquestracao.trabalhos_da_fila WHERE estado = 2 AND geracao = 1"));
        }
        finally
        {
            foreach (var p in processadores) await p.StopAsync(CancellationToken.None);
        }
    }

    [PostgreSqlFact]
    public async Task NotificacaoPerdidaNaoDeixaTrabalhoParadoPorqueOPollingRecupera()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var executor = ExecutorDeTeste.QueConclui();
        // Sem LISTEN e sem sinal em memória: só o polling acorda o processador.
        var processador = CriarProcessador(provider, Processador(), Rapidas with { Polling = TimeSpan.FromMilliseconds(300) }, executor);
        await processador.StartAsync(CancellationToken.None);
        try
        {
            await processador.Recuperacao.WaitAsync(TimeSpan.FromSeconds(10));
            var numero = (await CriarAsync(provider, "sem-notificacao")).Execucao.Numero;
            await Eventually(async () => (await ExecucaoAsync(provider, numero)).Estado == EstadoDaExecucao.Concluida,
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            await processador.StopAsync(CancellationToken.None);
        }
    }

    [PostgreSqlFact]
    public async Task NotifyNoCommitAcordaOProcessadorSemEsperarOPolling()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var executor = ExecutorDeTeste.QueConclui();
        var ouvinte = new OuvinteDaFilaPostgreSql(banco.ConnectionString);
        var processador = CriarProcessador(provider, Processador(), Rapidas with { Polling = TimeSpan.FromMinutes(5) }, executor, ouvinte);
        await processador.StartAsync(CancellationToken.None);
        try
        {
            await processador.Recuperacao.WaitAsync(TimeSpan.FromSeconds(10));
            await Eventually(async () => await Escalar(banco,
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND query LIKE 'LISTEN dante_fila%'") == 1);
            var numero = (await CriarAsync(provider, "com-notificacao")).Execucao.Numero;
            await Eventually(async () => (await ExecucaoAsync(provider, numero)).Estado == EstadoDaExecucao.Concluida,
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            await processador.StopAsync(CancellationToken.None);
        }
    }

    [PostgreSqlFact]
    public async Task NenhumaTransacaoFicaAbertaEnquantoOProcessoExternoRoda()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var rodando = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new ExecutorDeTeste(async (_, controle, token) =>
        {
            await controle.IniciarAsync(token);
            rodando.SetResult();
            await liberar.Task.WaitAsync(token);
            await controle.ConcluirAsync(new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida, 0), token);
        });
        var processador = CriarProcessador(provider, Processador(), Rapidas with { Polling = TimeSpan.FromSeconds(2) }, executor,
            new OuvinteDaFilaPostgreSql(banco.ConnectionString));
        await processador.StartAsync(CancellationToken.None);
        try
        {
            var numero = (await CriarAsync(provider, "sem-transacao")).Execucao.Numero;
            await rodando.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Várias amostras enquanto o "processo" roda, atravessando heartbeats.
            for (var i = 0; i < 10; i++)
            {
                Assert.Equal(0L, await Escalar(banco,
                    "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() AND state LIKE 'idle in transaction%'"));
                await Task.Delay(100);
            }
            Assert.Equal(EstadoDaExecucao.EmExecucao, (await ExecucaoAsync(provider, numero)).Estado);
            liberar.SetResult();
            await Eventually(async () => (await ExecucaoAsync(provider, numero)).Estado == EstadoDaExecucao.Concluida);
        }
        finally
        {
            await processador.StopAsync(CancellationToken.None);
        }
    }

    [PostgreSqlFact]
    public async Task HeartbeatSustentaEtapaLongaEPerdaDaPosseCancelaSemGravar()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var opcoes = Rapidas with { Lease = TimeSpan.FromSeconds(1), Heartbeat = TimeSpan.FromMilliseconds(200) };
        var executor = new ExecutorDeTeste(async (_, controle, token) =>
        {
            await controle.IniciarAsync(token);
            await Task.Delay(TimeSpan.FromSeconds(2.5), token);
            await controle.ConcluirAsync(new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida, 0), token);
        });
        var processador = CriarProcessador(provider, Processador(), opcoes, executor);
        await processador.StartAsync(CancellationToken.None);
        var concorrente = Processador();
        long numero;
        try
        {
            numero = (await CriarAsync(provider, "longa")).Execucao.Numero;
            // Durante 2,5 s (mais que o lease de 1 s) o heartbeat mantém a posse: ninguém mais reivindica.
            await Eventually(async () => (await ExecucaoAsync(provider, numero)).Estado == EstadoDaExecucao.EmExecucao);
            var ate = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < ate)
            {
                Assert.Null(await ReivindicarAsync(provider, concorrente, TimeSpan.FromSeconds(1)));
                await Task.Delay(100);
            }
            await Eventually(async () => (await ExecucaoAsync(provider, numero)).Estado == EstadoDaExecucao.Concluida);
            Assert.Equal(1L, await Escalar(banco, "SELECT geracao FROM orquestracao.trabalhos_da_fila WHERE chave_de_deduplicacao = 'longa'"));
        }
        finally
        {
            await processador.StopAsync(CancellationToken.None);
        }

        // Agora outro processador assume a linha (nova geração): o heartbeat é recusado, a etapa é cancelada
        // localmente e o resultado do processador antigo não é gravado.
        var cancelada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? recusa = null;
        var rodando = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var perdedor = new ExecutorDeTeste(async (_, controle, token) =>
        {
            await controle.IniciarAsync(token);
            rodando.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelada.SetResult(); }
            try { await controle.ConcluirAsync(new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida, 0), CancellationToken.None); }
            catch (Exception exception) { recusa = exception; }
        });
        var antigo = CriarProcessador(provider, Processador(), opcoes, perdedor);
        await antigo.StartAsync(CancellationToken.None);
        try
        {
            numero = (await CriarAsync(provider, "roubada")).Execucao.Numero;
            await rodando.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Executar(banco, "UPDATE orquestracao.trabalhos_da_fila SET geracao = geracao + 1, trabalhador = 'outro:1' WHERE chave_de_deduplicacao = 'roubada'");
            await cancelada.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Eventually(() => Task.FromResult(recusa is not null));
            Assert.IsType<PosseDoTrabalhoPerdidaException>(recusa);
            var lida = await ExecucaoAsync(provider, numero);
            Assert.Equal((EstadoDaExecucao.EmExecucao, EstadoDaEtapa.EmExecucao), (lida.Estado, lida.Etapas[0].Estado));
        }
        finally
        {
            await antigo.StopAsync(CancellationToken.None);
        }
    }

    [PostgreSqlFact]
    public async Task ErroTransitorioVoltaComBackoffSemBusyLoopEEsgotadoFalhaAExecucao()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var executor = new ExecutorDeTeste((_, _, _) => throw new TimeoutException("banco lento"));
        var opcoes = Rapidas with { BackoffInicial = TimeSpan.FromMilliseconds(700), BackoffMaximo = TimeSpan.FromSeconds(1) };
        var processador = CriarProcessador(provider, Processador(), opcoes, executor);
        await processador.StartAsync(CancellationToken.None);
        try
        {
            var (execucao, _) = await CriarAsync(provider, "transitorio");
            await Eventually(() => Task.FromResult(executor.Execucoes.GetValueOrDefault(execucao.Numero) == 1));
            // Dentro do backoff o trabalho não é reivindicado de novo, mesmo com polling de 100 ms.
            await Task.Delay(400);
            Assert.Equal(1, executor.Execucoes[execucao.Numero]);
            var trabalho = await TrabalhoAsync(provider, "transitorio");
            Assert.Equal((EstadoDoTrabalho.Pendente, 1, "TimeoutException"), (trabalho.Estado, trabalho.Tentativas, trabalho.UltimoErro));
            // Esgotadas as tentativas (5), a execução falha em vez de girar para sempre.
            await Eventually(async () => (await ExecucaoAsync(provider, execucao.Numero)).Estado == EstadoDaExecucao.Falhou,
                TimeSpan.FromSeconds(15));
            Assert.Equal(TrabalhoDaFila.MaximoDeTentativasPadrao, executor.Execucoes[execucao.Numero]);
            Assert.StartsWith("Tentativas esgotadas", (await ExecucaoAsync(provider, execucao.Numero)).Erro);
        }
        finally
        {
            await processador.StopAsync(CancellationToken.None);
        }
    }

    [PostgreSqlFact]
    public async Task ProcessadorAplicaAPoliticaAoReivindicarTrabalhoDeBootAnteriorSemRepetirAEscrita()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var anterior = Processador();
        var (escrita, etapa) = await CriarAsync(provider, "boot-escrita", PoliticaDeRetomada.ComConfirmacao, escrita: true);
        var (naoIniciada, _) = await CriarAsync(provider, "boot-fila");
        var reivindicado = (await ReivindicarAsync(provider, anterior))!;
        await AppAsync(provider, app => app.IniciarEtapaAsync(reivindicado.Posse, etapa.Id));

        // Novo boot no mesmo host: a recuperação devolve o trabalho sem esperar o lease; o executor nunca é chamado
        // para a escrita interrompida, e a execução que não iniciou é cancelada porque o pedido não existe aqui.
        var executor = ExecutorDeTeste.QueConclui(podeExecutar: false);
        var processador = CriarProcessador(provider, Processador() with { Host = anterior.Host }, Rapidas, executor);
        await processador.StartAsync(CancellationToken.None);
        try
        {
            await Eventually(async () => (await ExecucaoAsync(provider, escrita.Numero)).Estado == EstadoDaExecucao.Interrompida &&
                                         (await ExecucaoAsync(provider, naoIniciada.Numero)).Estado == EstadoDaExecucao.Cancelada);
            Assert.Empty(executor.Execucoes);
            Assert.Equal(1, processador.UltimaRecuperacao!.TrabalhosDevolvidos);
            Assert.Equal([EstadoDaExecucao.Interrompida, EstadoDaExecucao.Cancelada],
                executor.Interrupcoes.Select(i => i.NovoEstado).Order());
            await Task.Delay(500);
            Assert.Empty(executor.Execucoes);
            Assert.Equal(EstadoDaEtapa.Interrompida, (await ExecucaoAsync(provider, escrita.Numero)).Etapas[0].Estado);
        }
        finally
        {
            await processador.StopAsync(CancellationToken.None);
        }
    }

    // ---- apoio ----

    internal static readonly OpcoesDaFila Rapidas = new()
    {
        Lease = TimeSpan.FromSeconds(5),
        Heartbeat = TimeSpan.FromSeconds(1),
        Polling = TimeSpan.FromMilliseconds(100),
        Slots = 1,
        BackoffInicial = TimeSpan.FromMilliseconds(200),
        BackoffMaximo = TimeSpan.FromSeconds(1)
    };

    internal static async Task<Banco> FilaAsync()
    {
        var banco = await Banco.CriarAsync(migrar: false);
        try
        {
            await using var contexto = new OrquestracaoDbContext(OrquestracaoDbContextFactory.Opcoes(banco.ConnectionString));
            await using var brain = banco.Contexto();
            await new AdministracaoDoBanco(brain, contexto).MigrarOrquestracaoAsync();
            return banco;
        }
        catch
        {
            await banco.DisposeAsync();
            throw;
        }
    }

    // Sempre com unidades falsas: nenhum teste lista ou para unidades systemd reais.
    internal static ServiceProvider Provider(string connectionString, TimeProvider? relogio = null,
        IUnidadesDeContencao? unidades = null)
    {
        var services = new ServiceCollection().AddLogging();
        if (relogio is not null) services.AddSingleton(relogio);
        services.AddApplication().AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Dante"] = connectionString }).Build());
        services.AddSingleton(unidades ?? new UnidadesFalsas());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    internal static IdentidadeDoProcessador Processador(string host = "host-de-teste") =>
        new(host, Random.Shared.Next(1, 1_000_000), Guid.NewGuid());

    internal static ProcessadorDaFila CriarProcessador(IServiceProvider provider, IdentidadeDoProcessador identidade,
        OpcoesDaFila opcoes, IExecutorDeEtapa executor, IOuvinteDaFila? ouvinte = null, SinalDaFila? sinal = null) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), identidade, opcoes, sinal ?? new SinalDaFila(), [executor],
            new MetricasDaFila(), NullLogger<ProcessadorDaFila>.Instance, ouvinte);

    internal static async Task<JobNaFilaDto> EnfileirarJobAsync(IServiceProvider provider, string chave)
    {
        await using var escopo = provider.CreateAsyncScope();
        var numero = await escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>().ReservarNumeroDeExecucaoAsync();
        return await escopo.ServiceProvider.GetRequiredService<IFilaDeExecucoesAppService>().EnfileirarJobAsync(
            new JobRegistradoDto(numero, AgentKind.Codex, "telegram", 1001, JobExecutionContext.Repository("@dante", "/repos/dante"),
                null, null, Inicio), chave);
    }

    // Produtor pelo caminho de domínio: execução, etapa e trabalho numa única transação (o contrato da /tarefa).
    internal static async Task<(Execucao Execucao, EtapaDaExecucao Etapa)> CriarAsync(IServiceProvider provider, string chave,
        PoliticaDeRetomada politica = PoliticaDeRetomada.ComConfirmacao, bool escrita = true, DateTimeOffset? disponivelEm = null)
    {
        await using var escopo = provider.CreateAsyncScope();
        var s = escopo.ServiceProvider;
        var fila = s.GetRequiredService<IFilaDeTrabalho>();
        var numero = await s.GetRequiredService<IExecucaoRepository>().ReservarNumeroAsync();
        var execucao = new Execucao(numero, TipoDeExecucao.Avulsa, "telegram", 1001, "{}", null, fila.Agora);
        var etapa = new EtapaDaExecucao(execucao, 1, TipoDeEtapa.ProcessoAvulso, escrita, politica, fila.Agora);
        await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(execucao);
        await s.GetRequiredService<IEtapaDaExecucaoRepository>().AdicionarAsync(etapa);
        await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(execucao, etapa, chave, fila.Agora, disponivelEm: disponivelEm));
        await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        return (execucao, etapa);
    }

    private static async Task<long> SessaoAsync(IServiceProvider provider, Guid? boot, string unidade, bool encerrar = false)
    {
        await using var escopo = provider.CreateAsyncScope();
        var ledger = escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>();
        var numero = await ledger.ReservarNumeroDeSessaoAsync();
        await ledger.RegistrarSessaoCriadaAsync(new SessaoRegistradaDto(numero, AgentKind.Claude, "telegram", 1001,
            JobExecutionContext.General("/g"), AgentPermissionProfile.Manual, null, null, Inicio, boot));
        await ledger.RegistrarSessaoIniciadaAsync(numero, "u", null, Inicio, unidadeDeContencao: unidade);
        if (encerrar)
            await ledger.RegistrarSessaoEncerradaAsync(new SessaoEncerradaDto(numero, EstadoDaSessaoDeAgente.Encerrada, null, null, Inicio));
        return numero;
    }

    internal static async Task<TrabalhoReivindicado?> ReivindicarAsync(IServiceProvider provider, IdentidadeDoProcessador eu,
        TimeSpan? lease = null) => await FilaAsync(provider, f => f.ReivindicarAsync(eu, lease ?? Lease));

    internal static async Task<T> FilaAsync<T>(IServiceProvider provider, Func<IFilaDeTrabalho, Task<T>> acao)
    {
        await using var escopo = provider.CreateAsyncScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<IFilaDeTrabalho>());
    }

    private static async Task FilaAsync(IServiceProvider provider, Func<IFilaDeTrabalho, Task> acao)
    {
        await using var escopo = provider.CreateAsyncScope();
        await acao(escopo.ServiceProvider.GetRequiredService<IFilaDeTrabalho>());
    }

    internal static async Task<T> AppAsync<T>(IServiceProvider provider, Func<IFilaDeExecucoesAppService, Task<T>> acao)
    {
        await using var escopo = provider.CreateAsyncScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<IFilaDeExecucoesAppService>());
    }

    internal static async Task AppAsync(IServiceProvider provider, Func<IFilaDeExecucoesAppService, Task> acao)
    {
        await using var escopo = provider.CreateAsyncScope();
        await acao(escopo.ServiceProvider.GetRequiredService<IFilaDeExecucoesAppService>());
    }

    internal static async Task<ExecucaoDto> ExecucaoAsync(IServiceProvider provider, long numero)
    {
        await using var escopo = provider.CreateAsyncScope();
        return (await escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>().ObterExecucaoAsync(numero))!;
    }

    internal static async Task<TrabalhoDaFila> TrabalhoAsync(IServiceProvider provider, string chave) =>
        (await FilaAsync(provider, f => f.ObterPorChaveAsync(chave)))!;

    internal static async Task<long> Escalar(Banco banco, string sql)
    {
        await using var conexao = new NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        return Convert.ToInt64(await comando.ExecuteScalarAsync());
    }

    internal static async Task Executar(Banco banco, string sql)
    {
        await using var conexao = new NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        await comando.ExecuteNonQueryAsync();
    }

    internal static async Task Eventually(Func<Task<bool>> condicao, TimeSpan? prazo = null)
    {
        var limite = DateTime.UtcNow + (prazo ?? TimeSpan.FromSeconds(15));
        while (!await condicao())
        {
            Assert.True(DateTime.UtcNow < limite, "A condição esperada não ocorreu a tempo.");
            await Task.Delay(50);
        }
    }

    internal sealed class RelogioManual(DateTimeOffset inicio) : TimeProvider
    {
        private long ticks = inicio.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Avancar(TimeSpan quanto) => Interlocked.Add(ref ticks, quanto.Ticks);
    }

    internal sealed class UnidadesFalsas(params string[] listadas) : IUnidadesDeContencao
    {
        private readonly ConcurrentQueue<string> paradas = new();
        public bool ListagemFalha { get; init; }
        public IReadOnlyList<string> Paradas => paradas.ToArray();

        public Task<IReadOnlyList<string>> ListarAsync(CancellationToken cancellationToken = default) => ListagemFalha
            ? Task.FromException<IReadOnlyList<string>>(new IOException("gerenciador indisponível"))
            : Task.FromResult<IReadOnlyList<string>>(listadas);

        public Task PararAsync(string unidade, CancellationToken cancellationToken = default)
        {
            paradas.Enqueue(unidade);
            return Task.CompletedTask;
        }
    }

    internal sealed class ExecutorDeTeste(Func<AlvoDoTrabalhoDto, ExecucaoSobPosse, CancellationToken, Task> executar,
        bool podeExecutar = true) : IExecutorDeEtapa
    {
        public ConcurrentDictionary<long, int> Execucoes { get; } = new();
        public ConcurrentQueue<ExecucaoInterrompidaDto> Interrupcoes { get; } = new();
        public TipoDeExecucao Tipo => TipoDeExecucao.Avulsa;
        public bool PodeExecutar(AlvoDoTrabalhoDto alvo) => podeExecutar;

        public async Task ExecutarAsync(AlvoDoTrabalhoDto alvo, ExecucaoSobPosse controle, CancellationToken cancellationToken)
        {
            Execucoes.AddOrUpdate(alvo.NumeroDaExecucao, 1, (_, n) => n + 1);
            await executar(alvo, controle, cancellationToken);
        }

        public Task AoInterromperAsync(ExecucaoInterrompidaDto interrupcao, CancellationToken cancellationToken)
        {
            Interrupcoes.Enqueue(interrupcao);
            return Task.CompletedTask;
        }

        public static ExecutorDeTeste QueConclui(Func<Task>? trabalho = null, bool podeExecutar = true) => new(async (_, controle, token) =>
        {
            await controle.IniciarAsync(token);
            if (trabalho is not null) await trabalho();
            await controle.ConcluirAsync(new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida, 0), token);
        }, podeExecutar);
    }
}

[CollectionDefinition("FilaDuravel", DisableParallelization = true)]
public sealed class FilaDuravelCollection;
