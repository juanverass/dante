using System.Collections.Concurrent;
using System.Text.Json;
using Dante.Application.Agentes;
using Dante.Application.Execucoes;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Worker.Execucoes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Dante.Tests.FilaDuravelPostgreSqlTests;

namespace Dante.Tests;

// #305: acompanhamento e cancelamento pelo proprietário contra PostgreSQL real e descartável (DANTE_TEST_POSTGRES).
// Os casos de uso são os da Application, sem Telegram: consultas sempre filtradas pelo dono, cancelamento direto só
// sem posse (linhas travadas), pedido append-only quando há posse, e quem tem a posse grava Cancelada sob fencing.
[Collection("FilaDuravel")]
public sealed class AcompanhamentoDeExecucoesPostgreSqlTests(Xunit.Abstractions.ITestOutputHelper saida)
{
    private const long Dono = 1001;
    private const long Outro = 2002;
    private static readonly TimeSpan LeaseCurto = TimeSpan.FromSeconds(60);

    [PostgreSqlFact]
    public async Task TarefaPendenteECanceladaSemProcessoComAutorEMotivoEUmaSoTransicao()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var versao = await VersaoAsync(provider);
        var (execucao, _) = await TarefaAsync(provider, versao, "telegram:1:1");

        var cancelamento = await CancelarAsync(provider, Dono, execucao.Codigo, "não preciso\nmais");
        Assert.Equal((ResultadoDoCancelamento.Cancelada, EstadoDaExecucao.Cancelada), (cancelamento.Resultado, cancelamento.Estado));
        var lida = await ExecucaoAsync(provider, execucao.Numero);
        Assert.Equal((EstadoDaExecucao.Cancelada, EstadoDaEtapa.Cancelada), (lida.Estado, lida.Etapas[0].Estado));
        Assert.Equal("Cancelada pelo dono: não preciso mais", lida.Motivo);
        Assert.Equal(EstadoDoTrabalho.Descartado, (await TrabalhoAsync(provider, "telegram:1:1")).Estado);
        var pedido = Assert.Single(lida.Eventos, e => e.Tipo == TipoDeEventoDeOrquestracao.CancelamentoSolicitado);
        var dados = JsonDocument.Parse(pedido.Dados!).RootElement;
        Assert.Equal(("telegram:1001", "não preciso mais"),
            (dados.GetProperty("autor").GetString(), dados.GetProperty("motivo").GetString()));

        // Repetir não grava nada: nem evento, nem transição.
        var eventos = lida.Eventos.Count;
        Assert.Equal(ResultadoDoCancelamento.JaEncerrada, (await CancelarAsync(provider, Dono, execucao.Codigo, null)).Resultado);
        Assert.Equal(eventos, (await ExecucaoAsync(provider, execucao.Numero)).Eventos.Count);

        // Nada sobrou para um processador executar.
        var executor = new ExecutorDeTarefaDeTeste((_, controle, token) => controle.IniciarAsync(token));
        await using var processador = new ProcessadorEmTeste(provider, executor);
        await processador.IniciarAsync();
        await Task.Delay(500);
        Assert.Empty(executor.Execucoes);
        Assert.Empty(executor.Interrupcoes);
    }

    // Corrida real entre a reivindicação (FOR UPDATE SKIP LOCKED) e o cancelamento (FOR UPDATE): em cada rodada há um
    // único vencedor. Ou a tarefa foi cancelada e ninguém a reivindicou, ou foi reivindicada e o cancelamento virou só
    // pedido, sem tocar o estado.
    [PostgreSqlFact]
    public async Task CancelamentoConcorrenteComReivindicacaoTemExatamenteUmVencedor()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var versao = await VersaoAsync(provider);
        var (canceladas, reivindicadas) = (0, 0);
        // A reivindicação é um único comando e o cancelamento faz uma leitura antes de travar: sem variação, a
        // reivindicação vence sempre. Um atraso aleatório pequeno em cada lado faz as duas ordens (e a disputa pela
        // linha travada) acontecerem.
        var sorteio = new Random(305);
        const int Rodadas = 40;
        for (var rodada = 0; rodada < Rodadas; rodada++)
        {
            var chave = $"telegram:1:{rodada}";
            var (execucao, _) = await TarefaAsync(provider, versao, chave);
            var (atrasoDaReivindicacao, atrasoDoCancelamento) = (sorteio.Next(0, 25), sorteio.Next(0, 10));
            using var largada = new ManualResetEventSlim();
            var reivindicar = Task.Run(async () =>
            {
                largada.Wait();
                await Task.Delay(atrasoDaReivindicacao);
                return await ReivindicarAsync(provider, Processador());
            });
            var cancelar = Task.Run(async () =>
            {
                largada.Wait();
                await Task.Delay(atrasoDoCancelamento);
                return await CancelarAsync(provider, Dono, execucao.Codigo, null);
            });
            largada.Set();
            var (trabalho, cancelamento) = (await reivindicar, await cancelar);
            var lida = await ExecucaoAsync(provider, execucao.Numero);
            var pedidos = lida.Eventos.Count(e => e.Tipo == TipoDeEventoDeOrquestracao.CancelamentoSolicitado);
            if (cancelamento.Resultado == ResultadoDoCancelamento.Cancelada)
            {
                canceladas++;
                Assert.Null(trabalho);
                Assert.Equal(EstadoDaExecucao.Cancelada, lida.Estado);
                Assert.Equal(EstadoDoTrabalho.Descartado, (await TrabalhoAsync(provider, chave)).Estado);
            }
            else
            {
                reivindicadas++;
                Assert.Equal(ResultadoDoCancelamento.Solicitada, cancelamento.Resultado);
                Assert.NotNull(trabalho);
                // Quem pediu não tinha a posse: estado intocado, só o pedido.
                Assert.Equal((EstadoDaExecucao.Pendente, EstadoDaEtapa.Pendente), (lida.Estado, lida.Etapas[0].Estado));
                Assert.Equal(EstadoDoTrabalho.Reivindicado, (await TrabalhoAsync(provider, chave)).Estado);
                // O dono honra o pedido: avalia Cancelar e grava sob posse.
                var avaliacao = await AppAsync(provider, app => app.AvaliarAsync(trabalho!));
                Assert.Equal(AcaoDoTrabalho.Cancelar, avaliacao.Acao);
                var cancelada = await AppAsync(provider, app => app.CancelarSolicitadaAsync(trabalho!.Posse, avaliacao.Alvo!));
                Assert.True(cancelada.PorCancelamento);
                Assert.Equal(EstadoDaExecucao.Cancelada, (await ExecucaoAsync(provider, execucao.Numero)).Estado);
            }
            Assert.Equal(1, pedidos);
        }
        saida.WriteLine($"Corrida cancelamento × reivindicação: {canceladas} cancelada(s) direto, {reivindicadas} reivindicada(s) com pedido.");
        Assert.Equal(Rodadas, canceladas + reivindicadas);
        Assert.True(canceladas > 0 && reivindicadas > 0, $"Só uma ordem ocorreu: {canceladas}/{reivindicadas}.");
    }

    [PostgreSqlFact]
    public async Task DoisCancelamentosSimultaneosGeramUmaSoTransicaoEUmSoPedido()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var versao = await VersaoAsync(provider);
        for (var rodada = 0; rodada < 15; rodada++)
        {
            // Pendente: um cancela, o outro encontra encerrada.
            var (livre, _) = await TarefaAsync(provider, versao, $"telegram:1:livre-{rodada}");
            var resultados = await Simultaneos(() => CancelarAsync(provider, Dono, livre.Codigo, "a"),
                () => CancelarAsync(provider, Dono, livre.Codigo, "b"));
            Assert.Equal(new[] { ResultadoDoCancelamento.Cancelada, ResultadoDoCancelamento.JaEncerrada }.Order(),
                resultados.Select(r => r.Resultado).Order());
            var lida = await ExecucaoAsync(provider, livre.Numero);
            Assert.Single(lida.Eventos, e => e.Tipo == TipoDeEventoDeOrquestracao.CancelamentoSolicitado);
            Assert.Single(lida.Eventos, e => e.Tipo == TipoDeEventoDeOrquestracao.ExecucaoTransitou &&
                                             JsonDocument.Parse(e.Dados!).RootElement.GetProperty("para").GetString() == "Cancelada");

            // Reivindicada: um registra o pedido, o outro encontra o pedido já feito.
            var (ocupada, _) = await TarefaAsync(provider, versao, $"telegram:1:ocupada-{rodada}");
            Assert.NotNull(await ReivindicarAsync(provider, Processador()));
            resultados = await Simultaneos(() => CancelarAsync(provider, Dono, ocupada.Codigo, "a"),
                () => CancelarAsync(provider, Dono, ocupada.Codigo, "b"));
            Assert.Equal(new[] { ResultadoDoCancelamento.Solicitada, ResultadoDoCancelamento.JaSolicitada }.Order(),
                resultados.Select(r => r.Resultado).Order());
            lida = await ExecucaoAsync(provider, ocupada.Numero);
            Assert.Single(lida.Eventos, e => e.Tipo == TipoDeEventoDeOrquestracao.CancelamentoSolicitado);
            Assert.Equal(EstadoDaExecucao.Pendente, lida.Estado);
        }
    }

    // Em execução sob a posse de outro processo: o pedido é só o evento; nenhum estado muda até o dono agir, e o pedido
    // não conflita com o xmin de quem tem a posse. O dono descarta o resultado e grava Cancelada.
    [PostgreSqlFact]
    public async Task PedidoComTrabalhoEmExecucaoSoGravaOEventoEODonoGravaCancelada()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var versao = await VersaoAsync(provider);
        var (execucao, etapa) = await TarefaAsync(provider, versao, "telegram:1:1");
        var trabalho = (await ReivindicarAsync(provider, Processador()))!;
        await AppAsync(provider, app => app.IniciarEtapaAsync(trabalho.Posse, etapa.Id));
        var antes = await ExecucaoAsync(provider, execucao.Numero);
        var geracao = (await TrabalhoAsync(provider, "telegram:1:1")).Geracao;

        Assert.Equal(ResultadoDoCancelamento.Solicitada, (await CancelarAsync(provider, Dono, execucao.Codigo, "chega")).Resultado);
        var depois = await ExecucaoAsync(provider, execucao.Numero);
        Assert.Equal((EstadoDaExecucao.EmExecucao, EstadoDaEtapa.EmExecucao), (depois.Estado, depois.Etapas[0].Estado));
        Assert.Equal(antes.Eventos.Count + 1, depois.Eventos.Count);
        var linha = await TrabalhoAsync(provider, "telegram:1:1");
        Assert.Equal((EstadoDoTrabalho.Reivindicado, geracao), (linha.Estado, linha.Geracao));
        Assert.True(await AppAsync(provider, app => app.CancelamentoSolicitadoAsync(execucao.Id)));

        // A conclusão pedida (Concluida) vira Cancelada: o resultado é descartado.
        var final = await AppAsync(provider, app => app.ConcluirEtapaAsync(trabalho.Posse, etapa.Id,
            new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida, 0)));
        Assert.Equal(EstadoDaEtapa.Cancelada, final);
        var lida = await ExecucaoAsync(provider, execucao.Numero);
        Assert.Equal((EstadoDaExecucao.Cancelada, "Cancelada pelo dono: chega"), (lida.Estado, lida.Motivo));
        Assert.Equal(EstadoDoTrabalho.Concluido, (await TrabalhoAsync(provider, "telegram:1:1")).Estado);
    }

    // O pedido gravado antes do início impede o processo: IniciarEtapaAsync recusa sob posse, sem gravar nada.
    [PostgreSqlFact]
    public async Task PedidoAntesDoInicioImpedeIniciarAEtapa()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var versao = await VersaoAsync(provider);
        var (execucao, etapa) = await TarefaAsync(provider, versao, "telegram:1:1");
        var trabalho = (await ReivindicarAsync(provider, Processador()))!;
        Assert.Equal(ResultadoDoCancelamento.Solicitada, (await CancelarAsync(provider, Dono, execucao.Codigo, null)).Resultado);
        await Assert.ThrowsAsync<CancelamentoSolicitadoException>(() =>
            AppAsync(provider, app => app.IniciarEtapaAsync(trabalho.Posse, etapa.Id)));
        Assert.Equal((EstadoDaExecucao.Pendente, EstadoDaEtapa.Pendente), await EstadosAsync(provider, execucao.Numero));
    }

    // Fencing: o processo cujo lease venceu não grava o cancelamento; quem reivindicou depois avalia Cancelar (em vez
    // de interromper e retomar a leitura) e grava sob a posse nova.
    [PostgreSqlFact]
    public async Task LeaseVencidoHonraOPedidoEPosseAntigaNaoGravaNada()
    {
        await using var banco = await FilaAsync();
        var relogio = new RelogioManual(DateTimeOffset.UtcNow);
        await using var provider = Provider(banco.ConnectionString, relogio);
        var versao = await VersaoAsync(provider);
        var (execucao, etapa) = await TarefaAsync(provider, versao, "telegram:1:1");
        var antigo = (await ReivindicarAsync(provider, Processador("a"), LeaseCurto))!;
        await AppAsync(provider, app => app.IniciarEtapaAsync(antigo.Posse, etapa.Id));
        Assert.Equal(ResultadoDoCancelamento.Solicitada, (await CancelarAsync(provider, Dono, execucao.Codigo, "pare")).Resultado);

        relogio.Avancar(LeaseCurto + TimeSpan.FromSeconds(1));
        var novo = (await ReivindicarAsync(provider, Processador("b"), LeaseCurto))!;
        Assert.Equal(antigo.Geracao + 1, novo.Geracao);
        var avaliacao = await AppAsync(provider, app => app.AvaliarAsync(novo));
        Assert.Equal(AcaoDoTrabalho.Cancelar, avaliacao.Acao);

        // A posse antiga não grava nem o cancelamento nem uma conclusão.
        await Assert.ThrowsAsync<PosseDoTrabalhoPerdidaException>(() =>
            AppAsync(provider, app => app.CancelarSolicitadaAsync(antigo.Posse, avaliacao.Alvo!)));
        await Assert.ThrowsAsync<PosseDoTrabalhoPerdidaException>(() => AppAsync(provider, app =>
            app.ConcluirEtapaAsync(antigo.Posse, etapa.Id, new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida))));
        Assert.Equal((EstadoDaExecucao.EmExecucao, EstadoDaEtapa.EmExecucao), await EstadosAsync(provider, execucao.Numero));

        var cancelada = await AppAsync(provider, app => app.CancelarSolicitadaAsync(novo.Posse, avaliacao.Alvo!));
        Assert.Equal((EstadoDaExecucao.Cancelada, true), (cancelada.NovoEstado, cancelada.PorCancelamento));
        Assert.Equal((EstadoDaExecucao.Cancelada, EstadoDaEtapa.Cancelada), await EstadosAsync(provider, execucao.Numero));
        Assert.Equal(EstadoDoTrabalho.Descartado, (await TrabalhoAsync(provider, "telegram:1:1")).Estado);
    }

    // Processador real: a etapa em execução é cancelada pelo dono da posse, sem reexecução e com um único aviso, pelo
    // sinal em memória ou, sem ele, pelo pedido no ledger conferido no heartbeat.
    [PostgreSqlTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EtapaEmExecucaoECanceladaPeloDonoUmaVezSemReexecucao(bool comSinal)
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var versao = await VersaoAsync(provider);
        var (execucao, _) = await TarefaAsync(provider, versao, "telegram:1:1");
        var iniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new ExecutorDeTarefaDeTeste(async (_, controle, token) =>
        {
            await controle.IniciarAsync(token);
            iniciada.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        // Sem sinal: o processador tem o próprio canal, que o caso de uso nunca alcança.
        await using var processador = new ProcessadorEmTeste(provider, executor, comSinal ? null : new CanalDeCancelamento());
        await processador.IniciarAsync();
        await iniciada.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(ResultadoDoCancelamento.Solicitada, (await CancelarAsync(provider, Dono, execucao.Codigo, "pare")).Resultado);
        await Eventually(async () => (await EstadosAsync(provider, execucao.Numero)).Item1 == EstadoDaExecucao.Cancelada);
        await Task.Delay(1500);
        Assert.Equal(1, executor.Execucoes[execucao.Numero]);
        var aviso = Assert.Single(executor.Interrupcoes);
        Assert.Equal((EstadoDaExecucao.Cancelada, true, "Cancelada pelo dono: pare"),
            (aviso.NovoEstado, aviso.PorCancelamento, aviso.Motivo));
        Assert.Equal(EstadoDoTrabalho.Descartado, (await TrabalhoAsync(provider, "telegram:1:1")).Estado);
        // Repetir o pedido depois do fim não gera transição nem aviso.
        Assert.Equal(ResultadoDoCancelamento.JaEncerrada, (await CancelarAsync(provider, Dono, execucao.Codigo, null)).Resultado);
        Assert.Single(executor.Interrupcoes);
    }

    [PostgreSqlFact]
    public async Task OutroProprietarioNaoConsultaNemCancelaENadaEGravado()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var versao = await VersaoAsync(provider);
        var (execucao, _) = await TarefaAsync(provider, versao, "telegram:1:1");
        var eventos = (await ExecucaoAsync(provider, execucao.Numero)).Eventos.Count;

        await using (var escopo = provider.CreateAsyncScope())
        {
            var consultas = escopo.ServiceProvider.GetRequiredService<IAcompanhamentoDeExecucoesAppService>();
            var outro = new ProprietarioDaExecucao("telegram", Outro);
            Assert.Empty(await consultas.ListarAsync(outro, true));
            Assert.Empty(await consultas.ListarAguardandoAsync(outro));
            Assert.Equal(new PainelDeExecucoesDto(0, 0), await consultas.ResumirAsync(outro));
            Assert.Null(await consultas.ObterAsync(outro, execucao.Codigo));
            // Mesmo usuário em outro canal também não é o dono.
            Assert.Null(await consultas.ObterAsync(new ProprietarioDaExecucao("web", Dono), execucao.Codigo));
            // Prefixo do outro tipo também não encontra.
            Assert.Null(await consultas.ObterAsync(new ProprietarioDaExecucao("telegram", Dono), $"J{execucao.Numero:D6}"));
            Assert.NotNull(await consultas.ObterAsync(new ProprietarioDaExecucao("telegram", Dono), execucao.Codigo.ToLowerInvariant()));
        }
        var deOutro = await CancelarAsync(provider, Outro, execucao.Codigo, "não é meu");
        var inexistente = await CancelarAsync(provider, Outro, "E999999", "não existe");
        Assert.Equal(inexistente, deOutro);
        Assert.Equal(new CancelamentoDeExecucaoDto(ResultadoDoCancelamento.NaoEncontrada), deOutro);
        var lida = await ExecucaoAsync(provider, execucao.Numero);
        Assert.Equal((EstadoDaExecucao.Pendente, eventos), (lida.Estado, lida.Eventos.Count));
    }

    [PostgreSqlFact]
    public async Task ConsultasClassificamAEsperaENaoExpoemDiretorioNemSegredo()
    {
        await using var banco = await FilaAsync();
        var relogio = new RelogioManual(DateTimeOffset.UtcNow);
        await using var provider = Provider(banco.ConnectionString, relogio);
        var versao = await VersaoAsync(provider);
        var dono = new ProprietarioDaExecucao("telegram", Dono);

        // Cada execução é criada e levada ao seu estado antes da próxima existir: a reivindicação pega a disponível.
        // Aguardando nova tentativa: reivindicada e devolvida com backoff.
        var (emBackoff, _) = await TarefaAsync(provider, versao, "telegram:1:2");
        var reivindicado = (await ReivindicarAsync(provider, Processador()))!;
        Assert.Equal(emBackoff.Id, reivindicado.IdExecucao);
        Assert.True(await FilaAsync(provider, f => f.DevolverAsync(reivindicado.Posse, relogio.GetUtcNow().AddMinutes(5), "x", true)));
        // Interrompida: job legado (ComConfirmacao) que estava rodando quando a posse se perdeu.
        var job = await EnfileirarJobAsync(provider, "telegram:1:3");
        var primeira = (await ReivindicarAsync(provider, Processador(), LeaseCurto))!;
        Assert.Equal(job.IdExecucao, primeira.IdExecucao);
        var etapaDoJob = (await ExecucaoAsync(provider, job.Numero)).Etapas[0].Id;
        await AppAsync(provider, app => app.IniciarEtapaAsync(primeira.Posse, etapaDoJob));
        var avaliacao = await AppAsync(provider, app => app.AvaliarAsync(primeira));
        await AppAsync(provider, app => app.InterromperAsync(primeira.Posse, avaliacao.Alvo!, "reinício"));
        // Falhou com um segredo no erro.
        var (falha, etapaDaFalha) = await TarefaAsync(provider, versao, "telegram:1:4");
        var dela = (await ReivindicarAsync(provider, Processador()))!;
        Assert.Equal(falha.Id, dela.IdExecucao);
        await AppAsync(provider, app => app.ConcluirEtapaAsync(dela.Posse, etapaDaFalha.Id,
            new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou, 1, "falhou com token=ghp_abcdefghijklmnopqrstuvwxyz0123")));
        // Na fila.
        var (naFila, _) = await TarefaAsync(provider, versao, "telegram:1:1");

        await using var escopo = provider.CreateAsyncScope();
        var consultas = escopo.ServiceProvider.GetRequiredService<IAcompanhamentoDeExecucoesAppService>();
        var lista = await consultas.ListarAsync(dono, false);
        Assert.Equal(4, lista.Count);
        var porCodigo = lista.ToDictionary(r => r.Codigo);
        Assert.Equal(MotivoDaEspera.NaFila, porCodigo[naFila.Codigo].Espera);
        Assert.Equal("revisor v1", porCodigo[naFila.Codigo].Agente);
        Assert.Equal((1, 1), (porCodigo[naFila.Codigo].EtapaAtual, porCodigo[naFila.Codigo].TotalDeEtapas));
        Assert.Equal(MotivoDaEspera.AguardandoNovaTentativa, porCodigo[emBackoff.Codigo].Espera);
        Assert.Equal((MotivoDaEspera.Interrompida, "codex"), (porCodigo[$"J{job.Numero:D6}"].Espera, porCodigo[$"J{job.Numero:D6}"].Agente));
        Assert.Null(porCodigo[falha.Codigo].Espera);
        Assert.True(porCodigo[falha.Codigo].Terminal);

        var aguardando = await consultas.ListarAguardandoAsync(dono);
        Assert.Equal(new[] { naFila.Codigo, emBackoff.Codigo, $"J{job.Numero:D6}" }.Order(), aguardando.Select(r => r.Codigo).Order());
        Assert.Equal(new PainelDeExecucoesDto(0, 3), await consultas.ResumirAsync(dono));

        var detalhe = (await consultas.ObterAsync(dono, falha.Codigo))!;
        Assert.Equal("[conteúdo protegido]", detalhe.Erro);
        Assert.Equal("[conteúdo protegido]", Assert.Single(detalhe.Etapas).Erro);
        var doJob = (await consultas.ObterAsync(dono, $"J{job.Numero:D6}"))!;
        Assert.Equal(("Repository", "@dante"), (doJob.ModoDoContexto, doJob.AliasDoContexto));
        // Nenhum diretório gravado no contexto sai pela consulta.
        Assert.DoesNotContain("/repos/dante", JsonSerializer.Serialize(doJob));
        Assert.DoesNotContain("ghp_", JsonSerializer.Serialize(detalhe));
    }

    // Interrompida sem trabalho ativo: cancelada direto (protegida por xmin) e não pode mais ser retomada. Job legado
    // ainda com posse pertence ao registro em memória: nada é gravado aqui.
    [PostgreSqlFact]
    public async Task InterrompidaECanceladaDiretoEJobComPosseNaoEAlterado()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var job = await EnfileirarJobAsync(provider, "telegram:1:1");
        var trabalho = (await ReivindicarAsync(provider, Processador()))!;
        var etapa = (await ExecucaoAsync(provider, job.Numero)).Etapas[0].Id;
        await AppAsync(provider, app => app.IniciarEtapaAsync(trabalho.Posse, etapa));
        var codigo = $"J{job.Numero:D6}";
        var eventos = (await ExecucaoAsync(provider, job.Numero)).Eventos.Count;
        Assert.Equal(ResultadoDoCancelamento.JobEmAndamento, (await CancelarAsync(provider, Dono, codigo, null)).Resultado);
        Assert.Equal(eventos, (await ExecucaoAsync(provider, job.Numero)).Eventos.Count);

        var avaliacao = await AppAsync(provider, app => app.AvaliarAsync(trabalho));
        Assert.Equal(EstadoDaExecucao.Interrompida,
            (await AppAsync(provider, app => app.InterromperAsync(trabalho.Posse, avaliacao.Alvo!, "reinício"))).NovoEstado);
        Assert.Equal(ResultadoDoCancelamento.Cancelada, (await CancelarAsync(provider, Dono, codigo, "descartar")).Resultado);
        Assert.Equal((EstadoDaExecucao.Cancelada, EstadoDaEtapa.Cancelada), await EstadosAsync(provider, job.Numero));
        Assert.False(await AppAsync(provider, app => app.ConfirmarRetomadaAsync(job.Numero)));
    }

    // Vínculo etapa → sessão (#305): gravado sob posse e exibido pelo detalhe como o código S… real. Sessão de outro
    // dono ou inexistente é recusada sem gravar nada.
    [PostgreSqlFact]
    public async Task SessaoDaEtapaGravadaSobPosseEExibidaNoDetalhe()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var versao = await VersaoAsync(provider);
        var (execucao, etapa) = await TarefaAsync(provider, versao, "telegram:1:1");
        var trabalho = (await ReivindicarAsync(provider, Processador()))!;
        var sessao = await SessaoDoLedgerAsync(provider, Dono);
        var alheia = await SessaoDoLedgerAsync(provider, Outro);

        // Antes do início, a etapa não recebe sessão.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppAsync(provider, app => app.VincularSessaoAsync(trabalho.Posse, etapa.Id, sessao)));
        await AppAsync(provider, app => app.IniciarEtapaAsync(trabalho.Posse, etapa.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppAsync(provider, app => app.VincularSessaoAsync(trabalho.Posse, etapa.Id, alheia)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppAsync(provider, app => app.VincularSessaoAsync(trabalho.Posse, etapa.Id, sessao + 1000)));
        Assert.Null((await ExecucaoAsync(provider, execucao.Numero)).Etapas[0].IdSessaoDeAgente);

        await AppAsync(provider, app => app.VincularSessaoAsync(trabalho.Posse, etapa.Id, sessao));
        Assert.Equal(EstadoDoTrabalho.Reivindicado, (await TrabalhoAsync(provider, "telegram:1:1")).Estado);
        Assert.Equal(EstadoDaEtapa.Concluida, await AppAsync(provider, app =>
            app.ConcluirEtapaAsync(trabalho.Posse, etapa.Id, new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida, 0))));

        // O vínculo sobrevive à conclusão e chega ao detalhe do dono como o código S… da sessão.
        await using var escopo = provider.CreateAsyncScope();
        var detalhe = await escopo.ServiceProvider.GetRequiredService<IAcompanhamentoDeExecucoesAppService>()
            .ObterAsync(new ProprietarioDaExecucao("telegram", Dono), execucao.Codigo);
        Assert.Equal($"S{sessao:D6}", Assert.Single(detalhe!.Etapas).Sessao);
    }

    // Fencing do vínculo: o processo cujo lease venceu não grava a sessão; quem reivindicou depois não herda nada dela.
    [PostgreSqlFact]
    public async Task PosseAntigaNaoVinculaSessao()
    {
        await using var banco = await FilaAsync();
        var relogio = new RelogioManual(DateTimeOffset.UtcNow);
        await using var provider = Provider(banco.ConnectionString, relogio);
        var versao = await VersaoAsync(provider);
        var (execucao, etapa) = await TarefaAsync(provider, versao, "telegram:1:1");
        var antigo = (await ReivindicarAsync(provider, Processador("a"), LeaseCurto))!;
        await AppAsync(provider, app => app.IniciarEtapaAsync(antigo.Posse, etapa.Id));
        var sessao = await SessaoDoLedgerAsync(provider, Dono);

        // Lease vencido, ainda sem nova reivindicação: já recusado.
        relogio.Avancar(LeaseCurto + TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<PosseDoTrabalhoPerdidaException>(() =>
            AppAsync(provider, app => app.VincularSessaoAsync(antigo.Posse, etapa.Id, sessao)));
        // Nova geração reivindicada: a posse antiga continua recusada.
        var novo = (await ReivindicarAsync(provider, Processador("b"), LeaseCurto))!;
        Assert.Equal(antigo.Geracao + 1, novo.Geracao);
        await Assert.ThrowsAsync<PosseDoTrabalhoPerdidaException>(() =>
            AppAsync(provider, app => app.VincularSessaoAsync(antigo.Posse, etapa.Id, sessao)));

        var lida = await ExecucaoAsync(provider, execucao.Numero);
        Assert.Null(lida.Etapas[0].IdSessaoDeAgente);
        Assert.Equal((EstadoDaExecucao.EmExecucao, EstadoDaEtapa.EmExecucao), (lida.Estado, lida.Etapas[0].Estado));
    }

    // Credencial que começa antes do limite de 200 e seria partida pelo corte (#305): nem o evento gravado nem o
    // detalhe guardam ou mostram qualquer fragmento dela.
    [PostgreSqlFact]
    public async Task MotivoComCredencialCortadaNaoGuardaNemExibeFragmento()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var versao = await VersaoAsync(provider);
        var (execucao, _) = await TarefaAsync(provider, versao, "telegram:1:1");
        const string credencial = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var motivo = new string('m', 179) + "\n" + credencial + " fim";

        await CancelarAsync(provider, Dono, execucao.Codigo, motivo);

        var lida = await ExecucaoAsync(provider, execucao.Numero);
        var pedido = Assert.Single(lida.Eventos, e => e.Tipo == TipoDeEventoDeOrquestracao.CancelamentoSolicitado);
        await using var escopo = provider.CreateAsyncScope();
        var detalhe = (await escopo.ServiceProvider.GetRequiredService<IAcompanhamentoDeExecucoesAppService>()
            .ObterAsync(new ProprietarioDaExecucao("telegram", Dono), execucao.Codigo))!;
        foreach (var texto in new[] { pedido.Dados, lida.Motivo, lida.Etapas[0].Motivo, detalhe.MotivoDoCancelamento,
                     detalhe.Motivo, detalhe.Etapas[0].Motivo })
        {
            Assert.DoesNotContain("ghp_", texto ?? "");
            Assert.DoesNotContain("ABCDEFGH", texto ?? "");
        }
        Assert.Equal("[conteúdo protegido]", detalhe.MotivoDoCancelamento);
        Assert.Equal("Cancelada pelo dono: [conteúdo protegido]", lida.Motivo);
    }

    // ---- apoio ----

    private static async Task<CancelamentoDeExecucaoDto> CancelarAsync(IServiceProvider provider, long usuario, string codigo,
        string? motivo)
    {
        await using var escopo = provider.CreateAsyncScope();
        return await escopo.ServiceProvider.GetRequiredService<IControleDeExecucoesAppService>()
            .SolicitarCancelamentoAsync(new ProprietarioDaExecucao("telegram", usuario), codigo, motivo);
    }

    private static async Task<T[]> Simultaneos<T>(params Func<Task<T>>[] acoes)
    {
        using var largada = new ManualResetEventSlim();
        var tarefas = acoes.Select(acao => Task.Run(async () =>
        {
            largada.Wait();
            return await acao();
        })).ToArray();
        largada.Set();
        return await Task.WhenAll(tarefas);
    }

    private static async Task<long> SessaoDoLedgerAsync(IServiceProvider provider, long dono)
    {
        await using var escopo = provider.CreateAsyncScope();
        var ledger = escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>();
        var numero = await ledger.ReservarNumeroDeSessaoAsync();
        await ledger.RegistrarSessaoCriadaAsync(new SessaoRegistradaDto(numero, AgentKind.Claude, "telegram", dono,
            Dante.Application.Contextos.JobExecutionContext.General("/g"), AgentPermissionProfile.Plan, null, null,
            DateTimeOffset.UtcNow));
        return numero;
    }

    private static async Task<Guid> VersaoAsync(IServiceProvider provider)
    {
        await using var escopo = provider.CreateAsyncScope();
        var s = escopo.ServiceProvider;
        var agente = new Agente("revisor", null, null, Dono, DateTimeOffset.UtcNow);
        var versao = agente.NovaVersao(new ConfiguracaoDoAgente(AgentKind.Claude, AgentPermissionProfile.Plan,
            AgentPermissionProfile.Plan, false), Dono, DateTimeOffset.UtcNow);
        await s.GetRequiredService<IAgenteRepository>().AdicionarAsync(agente);
        await s.GetRequiredService<IVersaoDoAgenteRepository>().AdicionarAsync(versao);
        await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        return versao.Id;
    }

    // Tarefa somente leitura do dono pelo caminho de domínio, com o seu trabalho (o contrato da /tarefa, AD-68).
    private static async Task<(Execucao Execucao, EtapaDaExecucao Etapa)> TarefaAsync(IServiceProvider provider, Guid versao,
        string chave)
    {
        await using var escopo = provider.CreateAsyncScope();
        var s = escopo.ServiceProvider;
        var fila = s.GetRequiredService<IFilaDeTrabalho>();
        var numero = await s.GetRequiredService<IExecucaoRepository>().ReservarNumeroAsync();
        var execucao = new Execucao(numero, TipoDeExecucao.Tarefa, "telegram", Dono,
            """{"modo":"Repository","alias":"@dante","diretorio":"/repos/dante","vinculados":[]}""", "leia o README",
            fila.Agora, versao);
        var etapa = new EtapaDaExecucao(execucao, 1, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica,
            fila.Agora, AgentKind.Claude);
        await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(execucao);
        await s.GetRequiredService<IEtapaDaExecucaoRepository>().AdicionarAsync(etapa);
        await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(execucao, etapa, chave, fila.Agora));
        await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        return (execucao, etapa);
    }

    internal sealed class ExecutorDeTarefaDeTeste(Func<AlvoDoTrabalhoDto, ExecucaoSobPosse, CancellationToken, Task> executar)
        : IExecutorDeEtapa
    {
        public ConcurrentDictionary<long, int> Execucoes { get; } = new();
        public ConcurrentQueue<ExecucaoInterrompidaDto> Interrupcoes { get; } = new();
        public TipoDeExecucao Tipo => TipoDeExecucao.Tarefa;
        public bool PodeExecutar(AlvoDoTrabalhoDto alvo) => true;

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
    }

    private sealed class ProcessadorEmTeste(IServiceProvider provider, IExecutorDeEtapa executor,
        CanalDeCancelamento? canal = null) : IAsyncDisposable
    {
        private readonly ProcessadorDaFila processador = new(provider.GetRequiredService<IServiceScopeFactory>(), Processador(),
            Rapidas, new SinalDaFila(), [executor], new MetricasDaFila(), NullLogger<ProcessadorDaFila>.Instance, canal: canal);

        public async Task IniciarAsync()
        {
            await processador.StartAsync(CancellationToken.None);
            await processador.Recuperacao.WaitAsync(TimeSpan.FromSeconds(15));
        }

        public async ValueTask DisposeAsync() => await processador.StopAsync(CancellationToken.None);
    }
}
