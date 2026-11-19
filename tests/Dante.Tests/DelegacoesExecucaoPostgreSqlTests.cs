using Dante.Application.Agentes;
using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Microsoft.Extensions.DependencyInjection;
using static Dante.Tests.DelegacoesDominioTests;
using static Dante.Tests.FilaDuravelPostgreSqlTests;

namespace Dante.Tests;

// #309 (AD-70), PR 2: casos de uso, fila e ciclo das delegações contra PostgreSQL descartável (DANTE_TEST_POSTGRES).
// A origem é uma Tarefa de verdade, criada e iniciada pela fila (posse do trabalho), e a subordinada é conduzida pelos
// mesmos métodos que o executor usa: reivindicação, início, sessão vinculada, resultado, falha, prazo e cancelamento,
// cada um numa transação sob posse. A concorrência é real: várias conexões disputando a mesma raiz.
[Collection("FilaDuravel")]
public sealed class DelegacoesExecucaoPostgreSqlTests
{
    private static readonly ProprietarioDaExecucao Dono = new("telegram", 1001);
    private static readonly TimeSpan LeaseLongo = TimeSpan.FromMinutes(10);
    private const string Resposta = """{"resposta":"Sim, pelo índice ix_estado.","confianca":"alta"}""";

    // ---- Application: regras e autorização ----

    [PostgreSqlFact]
    public async Task SolicitacaoValidaGravaSubordinadaDelegacaoAceitaEventosETrabalhoNumaTransacao()
    {
        await using var a = await Arvore.CriarAsync();
        var solicitada = await a.SolicitarAsync("Conferir o índice por estado");

        Assert.True(solicitada.Nova);
        var d = solicitada.Delegacao;
        Assert.Equal((EstadoDaDelegacao.Aceita, "revisor", "consulta.v1"), (d.Estado, d.Destinatario, d.Contrato));
        Assert.Equal((a.IdOrigem, a.IdOrigem, a.IdEtapa), (d.IdExecucaoRaiz, d.IdExecucaoOrigem, d.IdEtapaOrigem));
        Assert.NotNull(d.AceitaEm);
        // Subordinada Delegada, Pendente, filha da origem, com uma etapa de leitura retomável, e o trabalho dela na fila.
        Assert.Equal(1L, await a.Escalar(
            $"SELECT count(*) FROM orquestracao.execucoes WHERE id = '{d.IdExecucaoSubordinada}' AND tipo = {(int)TipoDeExecucao.Delegada} " +
            $"AND estado = {(int)EstadoDaExecucao.Pendente} AND id_execucao_pai = '{a.IdOrigem}' AND profundidade = 1"));
        Assert.Equal(1L, await a.Escalar(
            $"SELECT count(*) FROM orquestracao.etapas_da_execucao WHERE id_execucao = '{d.IdExecucaoSubordinada}' AND numero = 1 " +
            $"AND escrita = false AND politica_de_retomada = {(int)PoliticaDeRetomada.Automatica}"));
        var trabalho = await TrabalhoAsync(a.Provider, $"delegacao:{d.Id:N}");
        Assert.Equal((EstadoDoTrabalho.Pendente, d.IdExecucaoSubordinada), (trabalho.Estado, trabalho.IdExecucao!.Value));
        // Eventos na origem: solicitada e a transição para Aceita; só metadados.
        Assert.Equal(1L, await a.Escalar($"SELECT count(*) FROM orquestracao.eventos WHERE id_execucao = '{a.IdOrigem}' " +
            $"AND tipo = {(int)TipoDeEventoDeOrquestracao.DelegacaoSolicitada}"));
        Assert.Equal(1L, await a.Escalar($"SELECT count(*) FROM orquestracao.eventos WHERE id_execucao = '{a.IdOrigem}' " +
            $"AND tipo = {(int)TipoDeEventoDeOrquestracao.DelegacaoTransitou} AND dados::jsonb->>'para' = 'Aceita'"));
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.eventos WHERE dados::text LIKE '%índice%'"));
        // Consulta e listagem só para o proprietário.
        Assert.Equal(d.Id, (await a.DelegacoesAsync(s => s.ObterAsync(Dono, d.Id)))!.Id);
        Assert.Null(await a.DelegacoesAsync(s => s.ObterAsync(new ProprietarioDaExecucao("telegram", 2002), d.Id)));
        Assert.Single(await a.DelegacoesAsync(s => s.ListarDaRaizAsync(Dono, a.IdOrigem)));
        Assert.Empty(await a.DelegacoesAsync(s => s.ListarDaRaizAsync(new ProprietarioDaExecucao("telegram", 2002), a.IdOrigem)));
        Assert.Empty(await a.DelegacoesAsync(s => s.ListarDaRaizAsync(Dono, d.IdExecucaoSubordinada)));
    }

    [PostgreSqlFact]
    public async Task RecusasDaAplicacaoNaoGravamNada()
    {
        await using var a = await Arvore.CriarAsync();
        await a.SqlAsync("SET session_replication_role = replica; " +
            $"UPDATE orquestracao.versoes_de_agente SET instrucoes = 'adulterada' WHERE id_agente = '{a.Agentes["adulterado"]}'");
        var casos = new (string Caso, Func<Task> Pedido, MotivoDaRecusaDeDelegacao Motivo)[]
        {
            ("inexistente", () => a.SolicitarAsync("x", "ninguem"), MotivoDaRecusaDeDelegacao.DestinatarioIndisponivel),
            ("desativado", () => a.SolicitarAsync("x", "inativo"), MotivoDaRecusaDeDelegacao.DestinatarioIndisponivel),
            ("versão adulterada", () => a.SolicitarAsync("x", "adulterado"), MotivoDaRecusaDeDelegacao.DestinatarioIndisponivel),
            ("contrato inválido", () => a.SolicitarAsync("x", contrato: "livre.v1"), MotivoDaRecusaDeDelegacao.ContratoInvalido),
            ("revisão sem artefato verificável", () => a.SolicitarAsync("x", contrato: "revisao.v1",
                entrada: $$"""{"artefato":"{{new string('a', 64)}}"}"""), MotivoDaRecusaDeDelegacao.ContratoInvalido),
            ("entrada inválida", () => a.SolicitarAsync("x", entrada: """{"pergunta":"?","extra":1}"""),
                MotivoDaRecusaDeDelegacao.ContratoInvalido),
            ("entrada com credencial", () => a.SolicitarAsync("x",
                entrada: """{"pergunta":"use a chave ghp_abcdefghijklmnopqrstuvwxyz0123456789AB"}"""),
                MotivoDaRecusaDeDelegacao.ContratoInvalido),
            ("capacidade insuficiente", () => a.SolicitarAsync("x", "fraco"), MotivoDaRecusaDeDelegacao.CapacidadeInsuficiente),
            ("fora de PodeConsultar", () => a.SolicitarAsync("x", "estranho"), MotivoDaRecusaDeDelegacao.DestinatarioNaoAutorizado),
            ("autodelegação", () => a.SolicitarAsync("x", "pesquisador"), MotivoDaRecusaDeDelegacao.Autodelegacao),
            ("escrita", () => a.SolicitarAsync("x", escrita: true), MotivoDaRecusaDeDelegacao.EscritaNaoAutorizada),
            ("outro proprietário", () => a.SolicitarAsync("x", proprietario: new ProprietarioDaExecucao("telegram", 2002)),
                MotivoDaRecusaDeDelegacao.OrigemInvalida),
            ("outro canal", () => a.SolicitarAsync("x", proprietario: new ProprietarioDaExecucao("web", 1001)),
                MotivoDaRecusaDeDelegacao.OrigemInvalida),
            ("etapa de outra execução", () => a.SolicitarAsync("x", idEtapa: Guid.NewGuid()), MotivoDaRecusaDeDelegacao.OrigemInvalida),
        };
        foreach (var (caso, pedido, motivo) in casos)
            Assert.True(motivo == (await Assert.ThrowsAsync<DelegacaoRecusadaException>(pedido)).Motivo, caso);
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.delegacoes"));
        Assert.Equal(0L, await a.Escalar($"SELECT count(*) FROM orquestracao.execucoes WHERE tipo = {(int)TipoDeExecucao.Delegada}"));
        Assert.Equal(1L, await a.Escalar("SELECT count(*) FROM orquestracao.trabalhos_da_fila"));
        Assert.Equal(0L, await a.Escalar($"SELECT count(*) FROM orquestracao.eventos WHERE tipo = {(int)TipoDeEventoDeOrquestracao.DelegacaoSolicitada}"));
    }

    [PostgreSqlFact]
    public async Task MesmaUnidadeDevolveAOriginalEmQualquerEstadoEOutroDestinatarioERecusado()
    {
        await using var a = await Arvore.CriarAsync();
        var original = await a.SolicitarAsync("Conferir o índice por estado");
        // Mesma unidade (objetivo normalizado e entrada canônica iguais), mesmo destinatário: a original.
        var repetida = await a.SolicitarAsync("  CONFERIR o índice   por estado ");
        Assert.False(repetida.Nova);
        Assert.Equal(original.Delegacao.Id, repetida.Delegacao.Id);
        Assert.Equal(MotivoDaRecusaDeDelegacao.UnidadeJaAtribuida, (await Assert.ThrowsAsync<DelegacaoRecusadaException>(
            () => a.SolicitarAsync("Conferir o índice por estado", "outro"))).Motivo);
        // Terminal: continua a mesma, sem reabrir; o outro destinatário continua recusado.
        var filha = await a.ReivindicarAsync();
        await a.DelegacoesAsync(s => s.FalharAsync(filha.Posse, filha.IdEtapa!.Value, "falha de teste"));
        var depois = await a.SolicitarAsync("Conferir o índice por estado");
        Assert.Equal((original.Delegacao.Id, EstadoDaDelegacao.Falhou, false), (depois.Delegacao.Id, depois.Delegacao.Estado, depois.Nova));
        await Assert.ThrowsAsync<DelegacaoRecusadaException>(() => a.SolicitarAsync("Conferir o índice por estado", "outro"));
        Assert.Equal(1L, await a.Escalar("SELECT count(*) FROM orquestracao.delegacoes"));
        Assert.Equal(1L, await a.Escalar($"SELECT count(*) FROM orquestracao.execucoes WHERE tipo = {(int)TipoDeExecucao.Delegada}"));
    }

    // ---- PostgreSQL: serialização por raiz, deduplicação concorrente e rollback ----

    [PostgreSqlFact]
    public async Task ChavesDiferentesSimultaneasNuncaPassamDoOrcamentoDaRaiz()
    {
        await using var a = await Arvore.CriarAsync();
        // Três vagas já usadas; duas chaves diferentes disputam a última, ao mesmo tempo, em conexões separadas.
        for (var i = 0; i < 3; i++) await a.SolicitarAsync($"Pergunta anterior {i}");
        var disputa = await a.SimultaneasAsync(Enumerable.Range(0, 2).Select(i => (Objetivo: $"Disputa {i}", Destinatario: "revisor")));
        Assert.Equal(1, disputa.Count(r => r.Ok is { Nova: true }));
        Assert.All(disputa.Where(r => r.Ok is null), r => Assert.Equal(MotivoDaRecusaDeDelegacao.FanOutExcedido, r.Recusa));
        Assert.Equal(4L, await a.Escalar("SELECT count(*) FROM orquestracao.delegacoes"));

        // Raiz nova e vazia: oito chaves diferentes de uma vez; exatamente quatro entram.
        await using var b = await Arvore.CriarAsync();
        var oito = await b.SimultaneasAsync(Enumerable.Range(0, 8).Select(i => (Objetivo: $"Paralela {i}", Destinatario: i % 2 == 0 ? "revisor" : "outro")));
        Assert.Equal(4, oito.Count(r => r.Ok is { Nova: true }));
        Assert.Equal(4, oito.Count(r => r.Recusa == MotivoDaRecusaDeDelegacao.FanOutExcedido));
        Assert.All(oito, r => Assert.Null(r.Erro));
        Assert.Equal(4L, await b.Escalar("SELECT count(*) FROM orquestracao.delegacoes"));
        Assert.Equal(4L, await b.Escalar($"SELECT count(*) FROM orquestracao.execucoes WHERE tipo = {(int)TipoDeExecucao.Delegada}"));
        Assert.Equal(5L, await b.Escalar("SELECT count(*) FROM orquestracao.trabalhos_da_fila"));
    }

    [PostgreSqlFact]
    public async Task MesmaUnidadeSimultaneaCriaUmaSoParaOMesmoDestinatarioERecusaOOutro()
    {
        await using var a = await Arvore.CriarAsync();
        var mesmo = await a.SimultaneasAsync(Enumerable.Range(0, 6).Select(_ => (Objetivo: "Mesma unidade", Destinatario: "revisor")));
        Assert.Equal(1, mesmo.Count(r => r.Ok is { Nova: true }));
        Assert.All(mesmo, r => Assert.Equal(mesmo.First(x => x.Ok is { Nova: true }).Ok!.Delegacao.Id, r.Ok!.Delegacao.Id));

        // Destinatários diferentes para a mesma unidade, ao mesmo tempo: um vence, os do mesmo agente recebem a dele e
        // os do outro são recusados.
        var mista = await a.SimultaneasAsync(Enumerable.Range(0, 6).Select(i => (Objetivo: "Unidade disputada", Destinatario: i % 2 == 0 ? "revisor" : "outro")));
        var vencedora = Assert.Single(mista, r => r.Ok is { Nova: true }).Ok!.Delegacao;
        foreach (var r in mista)
            if (r.Destinatario == vencedora.Destinatario) Assert.Equal(vencedora.Id, r.Ok!.Delegacao.Id);
            else Assert.Equal(MotivoDaRecusaDeDelegacao.UnidadeJaAtribuida, r.Recusa);
        Assert.Equal(2L, await a.Escalar("SELECT count(*) FROM orquestracao.delegacoes"));
    }

    [PostgreSqlFact]
    public async Task FalhaAoGravarOTrabalhoDesfazASubordinadaEADelegacaoInteiras()
    {
        await using var a = await Arvore.CriarAsync();
        await a.SqlAsync("""
            CREATE FUNCTION orquestracao.recusar_trabalho_de_teste() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.chave_de_deduplicacao LIKE 'delegacao:%' THEN RAISE EXCEPTION 'falha injetada'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER trg_recusar_trabalho_de_teste BEFORE INSERT ON orquestracao.trabalhos_da_fila
                FOR EACH ROW EXECUTE FUNCTION orquestracao.recusar_trabalho_de_teste();
            """);
        await Assert.ThrowsAnyAsync<Exception>(() => a.SolicitarAsync("Conferir o índice por estado"));
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.delegacoes"));
        Assert.Equal(0L, await a.Escalar($"SELECT count(*) FROM orquestracao.execucoes WHERE tipo = {(int)TipoDeExecucao.Delegada}"));
        Assert.Equal(1L, await a.Escalar("SELECT count(*) FROM orquestracao.etapas_da_execucao"));
        Assert.Equal(0L, await a.Escalar($"SELECT count(*) FROM orquestracao.eventos WHERE tipo IN ({(int)TipoDeEventoDeOrquestracao.DelegacaoSolicitada}, {(int)TipoDeEventoDeOrquestracao.DelegacaoTransitou})"));
        await a.SqlAsync("DROP TRIGGER trg_recusar_trabalho_de_teste ON orquestracao.trabalhos_da_fila");
        // Sem a falha, o mesmo pedido é uma unidade nova: nada da tentativa desfeita ficou para trás.
        Assert.True((await a.SolicitarAsync("Conferir o índice por estado")).Nova);
        Assert.Equal(1L, await a.Escalar("SELECT count(*) FROM orquestracao.delegacoes"));
    }

    // ---- Ciclo: espera, execução sob posse, resultado e retomada ----

    [PostgreSqlFact]
    public async Task CicloCompletoEsperaRespondeRetomaAOrigemEOResultadoNaoMuda()
    {
        await using var a = await Arvore.CriarAsync();
        var d = (await a.SolicitarAsync("Conferir o índice por estado")).Delegacao;
        // O turno da origem termina com a delegação aberta: a origem espera, sem perder identidade nem etapa.
        Assert.Equal(EstadoDaEtapa.Concluida, await a.ConcluirOrigemAsync());
        Assert.Equal(EstadoDaExecucao.AguardandoDelegacoes, await a.EstadoAsync(a.IdOrigem));

        var filha = await a.ReivindicarAsync();
        Assert.Equal(d.IdExecucaoSubordinada, filha.IdExecucao);
        var conferencia = await a.DelegacoesAsync(s => s.ConferirAsync(d.IdExecucaoSubordinada));
        Assert.Null(conferencia.Recusa);
        Assert.Contains("\"resposta\"", conferencia.Execucao!.SchemaDaSaida);
        Assert.Contains("cobre a consulta por estado?", conferencia.Execucao.Entrada);
        await a.DelegacoesAsync(s => s.IniciarAsync(filha.Posse, filha.IdEtapa!.Value));
        Assert.Equal(EstadoDaDelegacao.EmAndamento, await a.EstadoDaDelegacaoAsync(d.Id));
        var sessao = await a.SessaoAsync();
        var outraSessao = await a.SessaoAsync();
        await AppAsync(a.Provider, f => f.VincularSessaoAsync(filha.Posse, filha.IdEtapa!.Value, sessao));
        await a.DelegacoesAsync(s => s.RegistrarProgressoAsync(filha.Posse, filha.IdEtapa!.Value, MarcoDaDelegacao.SessaoAberta));

        // Recusas sem gravar: outra sessão, campo fora do schema, opção inválida, evidência com "..", credencial.
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.ConcluirAsync(filha, Resposta, outraSessao));
        foreach (var invalido in new[]
                 {
                     """{"resposta":"Sim.","confianca":"alta","extra":"x"}""",
                     """{"resposta":"Sim.","confianca":"total"}""",
                     """{"resposta":"Sim.","confianca":"alta","evidencias":[{"tipo":"arquivo","referencia":"../etc/passwd"}]}""",
                     """{"resposta":"token ghp_abcdefghijklmnopqrstuvwxyz0123456789AB","confianca":"alta"}""",
                     "não é json"
                 })
            await Assert.ThrowsAsync<ArgumentException>(() => a.ConcluirAsync(filha, invalido, sessao));
        Assert.Equal(EstadoDaDelegacao.EmAndamento, await a.EstadoDaDelegacaoAsync(d.Id));

        var desfecho = await a.ConcluirAsync(filha, Resposta, sessao);
        Assert.Equal((EstadoDaDelegacao.Respondida, true), (desfecho.Estado, desfecho.Retomada));
        var gravada = (await a.DelegacoesAsync(s => s.ObterAsync(Dono, d.Id)))!;
        Assert.Equal(EstadoDaDelegacao.Respondida, gravada.Estado);
        Assert.Contains("ix_estado", gravada.Resultado);
        Assert.NotNull(gravada.IdSessaoDoResultado);
        Assert.Equal(EstadoDaExecucao.Concluida, await a.EstadoAsync(d.IdExecucaoSubordinada));
        Assert.Equal(EstadoDoTrabalho.Concluido, (await TrabalhoAsync(a.Provider, $"delegacao:{d.Id:N}")).Estado);
        // Origem retomada: Pendente, etapa 2 de leitura e um trabalho com a chave da retomada.
        Assert.Equal(EstadoDaExecucao.Pendente, await a.EstadoAsync(a.IdOrigem));
        Assert.Equal(1L, await a.Escalar($"SELECT count(*) FROM orquestracao.etapas_da_execucao WHERE id_execucao = '{a.IdOrigem}' AND numero = 2 AND escrita = false"));
        Assert.Equal(EstadoDoTrabalho.Pendente, (await TrabalhoAsync(a.Provider, $"retomar-origem:{a.IdOrigem:N}:1")).Estado);
        Assert.Equal(1L, await a.Escalar($"SELECT count(*) FROM orquestracao.eventos WHERE id_execucao = '{a.IdOrigem}' AND tipo = {(int)TipoDeEventoDeOrquestracao.OrigemRetomada}"));
        Assert.Equal(1L, await a.Escalar($"SELECT count(*) FROM orquestracao.eventos WHERE tipo = {(int)TipoDeEventoDeOrquestracao.DelegacaoProgrediu}"));
        var etapa2 = await a.Escalar<Guid>($"SELECT id FROM orquestracao.etapas_da_execucao WHERE id_execucao = '{a.IdOrigem}' AND numero = 2");
        var retomada = (await a.DelegacoesAsync(s => s.ObterRetomadaAsync(a.IdOrigem, etapa2)))!;
        var entregue = Assert.Single(retomada.Desfechos);
        Assert.Equal((EstadoDaDelegacao.Respondida, "revisor"), (entregue.Estado, entregue.Destinatario));
        Assert.Null(await a.DelegacoesAsync(s => s.ObterRetomadaAsync(a.IdOrigem, a.IdEtapa)));

        // Resultado depois de terminal: a posse acabou e o original não muda; nem por fora do domínio.
        await Assert.ThrowsAsync<PosseDoTrabalhoPerdidaException>(() =>
            a.ConcluirAsync(filha, """{"resposta":"Outra.","confianca":"baixa"}""", sessao));
        await Assert.ThrowsAnyAsync<Exception>(() => a.SqlAsync($"UPDATE orquestracao.delegacoes SET resultado = '{{}}' WHERE id = '{d.Id}'"));
        Assert.Contains("ix_estado", (await a.DelegacoesAsync(s => s.ObterAsync(Dono, d.Id)))!.Resultado);
        // A recuperação não acha nada a retomar: a retomada já saiu na transação do resultado.
        Assert.Equal(0, await a.DelegacoesAsync(s => s.RetomarOrigensProntasAsync()));
    }

    [PostgreSqlFact]
    public async Task OrigemQueConcluiDepoisDasFilhasERetomadaNaHoraSemEsperar()
    {
        await using var a = await Arvore.CriarAsync();
        var d = (await a.SolicitarAsync("Conferir o índice por estado")).Delegacao;
        var filha = await a.ReivindicarAsync();
        await a.DelegacoesAsync(s => s.FalharAsync(filha.Posse, filha.IdEtapa!.Value, "destinatário indisponível"));
        // A origem ainda estava no turno: nada a retomar até a etapa dela terminar.
        Assert.Equal(EstadoDaExecucao.EmExecucao, await a.EstadoAsync(a.IdOrigem));
        await a.ConcluirOrigemAsync();
        Assert.Equal(EstadoDaExecucao.Pendente, await a.EstadoAsync(a.IdOrigem));
        Assert.Equal(EstadoDaDelegacao.Falhou, await a.EstadoDaDelegacaoAsync(d.Id));
        Assert.Equal(EstadoDaExecucao.Falhou, await a.EstadoAsync(d.IdExecucaoSubordinada));
        Assert.Equal(EstadoDoTrabalho.Pendente, (await TrabalhoAsync(a.Provider, $"retomar-origem:{a.IdOrigem:N}:1")).Estado);
    }

    [PostgreSqlFact]
    public async Task PosseAntigaNaoConcluiEARetomadaDaSubordinadaNaoReabreADelegacao()
    {
        await using var a = await Arvore.CriarAsync();
        var d = (await a.SolicitarAsync("Conferir o índice por estado")).Delegacao;
        await a.ConcluirOrigemAsync();
        var antiga = await a.ReivindicarAsync(TimeSpan.FromSeconds(30));
        await a.DelegacoesAsync(s => s.IniciarAsync(antiga.Posse, antiga.IdEtapa!.Value));
        var sessao = await a.SessaoAsync();
        await AppAsync(a.Provider, f => f.VincularSessaoAsync(antiga.Posse, antiga.IdEtapa!.Value, sessao));
        // O lease vence (processo parado) e outro processo reivindica: geração nova.
        a.Relogio.Avancar(TimeSpan.FromMinutes(1));
        var nova = await a.ReivindicarAsync();
        Assert.Equal(antiga.Id, nova.Id);
        Assert.True(nova.Geracao > antiga.Geracao);
        // Fencing: nada da posse antiga é gravado (resultado, falha, prazo, progresso).
        await Assert.ThrowsAsync<PosseDoTrabalhoPerdidaException>(() => a.ConcluirAsync(antiga, Resposta, sessao));
        await Assert.ThrowsAsync<PosseDoTrabalhoPerdidaException>(() =>
            a.DelegacoesAsync(s => s.FalharAsync(antiga.Posse, antiga.IdEtapa!.Value, "posse antiga")));
        await Assert.ThrowsAsync<PosseDoTrabalhoPerdidaException>(() =>
            a.DelegacoesAsync(s => s.RegistrarProgressoAsync(antiga.Posse, antiga.IdEtapa!.Value, MarcoDaDelegacao.TurnoEnviado)));
        Assert.Equal(EstadoDaDelegacao.EmAndamento, await a.EstadoDaDelegacaoAsync(d.Id));
        // A nova posse acha a etapa em execução: a política automática (leitura) a retoma como nova tentativa.
        var avaliacao = await AppAsync(a.Provider, f => f.AvaliarAsync(nova));
        Assert.Equal(AcaoDoTrabalho.Interromper, avaliacao.Acao);
        await AppAsync(a.Provider, f => f.InterromperAsync(nova.Posse, avaliacao.Alvo!, avaliacao.Motivo!));
        Assert.Equal(EstadoDaDelegacao.EmAndamento, await a.EstadoDaDelegacaoAsync(d.Id));
        var terceira = await a.ReivindicarAsync();
        await a.DelegacoesAsync(s => s.IniciarAsync(terceira.Posse, terceira.IdEtapa!.Value));
        var outraSessao = await a.SessaoAsync();
        await AppAsync(a.Provider, f => f.VincularSessaoAsync(terceira.Posse, terceira.IdEtapa!.Value, outraSessao));
        // A sessão da tentativa anterior não vale mais: só a vinculada agora.
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.ConcluirAsync(terceira, Resposta, sessao));
        Assert.Equal(EstadoDaDelegacao.Respondida, (await a.ConcluirAsync(terceira, Resposta, outraSessao)).Estado);
        Assert.Equal(2L, await a.Escalar($"SELECT tentativa FROM orquestracao.etapas_da_execucao WHERE id_execucao = '{d.IdExecucaoSubordinada}'"));
        Assert.Equal(1L, await a.Escalar($"SELECT count(*) FROM orquestracao.eventos WHERE id_execucao = '{a.IdOrigem}' " +
            $"AND tipo = {(int)TipoDeEventoDeOrquestracao.DelegacaoTransitou} AND dados::jsonb->>'para' = 'EmAndamento'"));
    }

    [PostgreSqlFact]
    public async Task RecuperacaoRetomaAOrigemPeloLedgerSemExecutarAFilhaDeNovoEDeFormaIdempotente()
    {
        await using var a = await Arvore.CriarAsync();
        var d = (await a.SolicitarAsync("Conferir o índice por estado")).Delegacao;
        var filha = await a.ReivindicarAsync();
        await a.DelegacoesAsync(s => s.IniciarAsync(filha.Posse, filha.IdEtapa!.Value));
        var sessao = await a.SessaoAsync();
        await AppAsync(a.Provider, f => f.VincularSessaoAsync(filha.Posse, filha.IdEtapa!.Value, sessao));
        await a.ConcluirAsync(filha, Resposta, sessao);
        // Estado de um processo que morreu entre o fim da filha e a retomada: a origem espera, sem retomada gravada.
        await a.SqlAsync($"UPDATE orquestracao.execucoes SET estado = {(int)EstadoDaExecucao.AguardandoDelegacoes} WHERE id = '{a.IdOrigem}'");
        await a.SqlAsync($"UPDATE orquestracao.trabalhos_da_fila SET estado = {(int)EstadoDoTrabalho.Concluido}, lease_ate = NULL, " +
            $"encerrado_em = now() WHERE id_execucao = '{a.IdOrigem}'");
        await a.SqlAsync($"UPDATE orquestracao.etapas_da_execucao SET estado = {(int)EstadoDaEtapa.Concluida} WHERE id = '{a.IdEtapa}'");

        await using (var escopo = a.Provider.CreateAsyncScope())
        {
            var relatorio = await escopo.ServiceProvider.GetRequiredService<IRecuperacaoDeExecucoesAppService>()
                .RecuperarAsync(Processador());
            Assert.Equal(1, relatorio.OrigensRetomadas);
        }
        await using (var escopo = a.Provider.CreateAsyncScope())
            Assert.Equal(0, (await escopo.ServiceProvider.GetRequiredService<IRecuperacaoDeExecucoesAppService>()
                .RecuperarAsync(Processador())).OrigensRetomadas);
        Assert.Equal(EstadoDaExecucao.Pendente, await a.EstadoAsync(a.IdOrigem));
        Assert.Equal(1L, await a.Escalar($"SELECT count(*) FROM orquestracao.etapas_da_execucao WHERE id_execucao = '{a.IdOrigem}' AND numero = 2"));
        Assert.Equal(1L, await a.Escalar($"SELECT count(*) FROM orquestracao.trabalhos_da_fila WHERE chave_de_deduplicacao = 'retomar-origem:{a.IdOrigem:N}:1'"));
        // A filha não volta: nenhum trabalho novo dela e o resultado continua o mesmo.
        Assert.Equal(1L, await a.Escalar($"SELECT count(*) FROM orquestracao.trabalhos_da_fila WHERE id_execucao = '{d.IdExecucaoSubordinada}'"));
        Assert.Equal(1L, await a.Escalar($"SELECT tentativas FROM orquestracao.trabalhos_da_fila WHERE id_execucao = '{d.IdExecucaoSubordinada}'"));
        Assert.Equal(EstadoDaDelegacao.Respondida, await a.EstadoDaDelegacaoAsync(d.Id));
    }

    // ---- Falha, prazo e cancelamento ----

    [PostgreSqlFact]
    public async Task PrazoParaIniciarEDuracaoMaximaExpiramADelegacaoERetomamAOrigem()
    {
        await using var a = await Arvore.CriarAsync();
        var lenta = (await a.SolicitarAsync("Nunca começa")).Delegacao;
        var longa = (await a.SolicitarAsync("Demora demais", minutosDeDuracao: 5)).Delegacao;
        await a.ConcluirOrigemAsync();

        // A segunda começa dentro do prazo; a primeira fica na fila.
        var primeira = await a.ReivindicarAsync();
        Assert.Equal(lenta.IdExecucaoSubordinada, primeira.IdExecucao);
        await FilaAsync(a.Provider, f => f.DevolverAsync(primeira.Posse, f.Agora.AddHours(1), "slot", false));
        var segunda = await a.ReivindicarAsync();
        Assert.Equal(longa.IdExecucaoSubordinada, segunda.IdExecucao);
        var limite = await a.DelegacoesAsync(s => s.IniciarAsync(segunda.Posse, segunda.IdEtapa!.Value));
        var sessao = await a.SessaoAsync();
        await AppAsync(a.Provider, f => f.VincularSessaoAsync(segunda.Posse, segunda.IdEtapa!.Value, sessao));

        a.Relogio.Avancar(TimeSpan.FromMinutes(6));
        Assert.True(a.Relogio.GetUtcNow() > limite);
        // Resultado depois da duração: Expirada, não Respondida.
        var expirada = await a.ConcluirAsync(segunda, Resposta, sessao);
        Assert.Equal((EstadoDaDelegacao.Expirada, false), (expirada.Estado, expirada.Retomada));
        Assert.Equal(EstadoDaExecucao.Cancelada, await a.EstadoAsync(longa.IdExecucaoSubordinada));

        a.Relogio.Avancar(TimeSpan.FromMinutes(30));
        // Um segundo antes do relógio: o PostgreSQL arredonda o literal para microssegundos.
        await a.SqlAsync($"UPDATE orquestracao.trabalhos_da_fila SET disponivel_em = '{a.Relogio.GetUtcNow().AddSeconds(-1):O}' WHERE id = '{primeira.Id}'");
        var atrasada = await a.ReivindicarAsync();
        var conferencia = await a.DelegacoesAsync(s => s.ConferirAsync(lenta.IdExecucaoSubordinada));
        Assert.True(conferencia.Expirada);
        await Assert.ThrowsAsync<PrazoDaDelegacaoExcedidoException>(() =>
            a.DelegacoesAsync(s => s.IniciarAsync(atrasada.Posse, atrasada.IdEtapa!.Value)));
        Assert.Equal(EstadoDaDelegacao.Aceita, await a.EstadoDaDelegacaoAsync(lenta.Id));
        var desfecho = await a.DelegacoesAsync(s => s.ExpirarAsync(atrasada.Posse, atrasada.IdEtapa!.Value, conferencia.Recusa!));
        Assert.Equal((EstadoDaDelegacao.Expirada, true), (desfecho.Estado, desfecho.Retomada));
        Assert.Equal(EstadoDaExecucao.Cancelada, await a.EstadoAsync(lenta.IdExecucaoSubordinada));
        Assert.Equal(EstadoDaExecucao.Pendente, await a.EstadoAsync(a.IdOrigem));
        var etapa2 = await a.Escalar<Guid>($"SELECT id FROM orquestracao.etapas_da_execucao WHERE id_execucao = '{a.IdOrigem}' AND numero = 2");
        var retomada = (await a.DelegacoesAsync(s => s.ObterRetomadaAsync(a.IdOrigem, etapa2)))!;
        Assert.All(retomada.Desfechos, x => Assert.Equal(EstadoDaDelegacao.Expirada, x.Estado));
    }

    [PostgreSqlFact]
    public async Task CancelamentoDaDelegacaoPassaPeloLedgerEPelaFilaComESemPosse()
    {
        await using var a = await Arvore.CriarAsync();
        var parada = (await a.SolicitarAsync("Ainda na fila")).Delegacao;
        var rodando = (await a.SolicitarAsync("Já rodando")).Delegacao;
        await a.ConcluirOrigemAsync();
        var primeira = await a.ReivindicarAsync();
        await FilaAsync(a.Provider, f => f.DevolverAsync(primeira.Posse, f.Agora, "teste", false));
        await a.SqlAsync($"UPDATE orquestracao.trabalhos_da_fila SET disponivel_em = disponivel_em + interval '1 hour' WHERE id_execucao = '{parada.IdExecucaoSubordinada}'");
        var filha = await a.ReivindicarAsync();
        Assert.Equal(rodando.IdExecucaoSubordinada, filha.IdExecucao);
        await a.DelegacoesAsync(s => s.IniciarAsync(filha.Posse, filha.IdEtapa!.Value));

        // Sem dono (na fila): cancelada na hora, trabalho descartado, delegação Cancelada.
        Assert.Equal(ResultadoDoCancelamento.Cancelada, (await a.DelegacoesAsync(s => s.CancelarAsync(Dono, parada.Id, "não precisa"))).Resultado);
        Assert.Equal(EstadoDaDelegacao.Cancelada, await a.EstadoDaDelegacaoAsync(parada.Id));
        Assert.Equal(EstadoDoTrabalho.Descartado, (await TrabalhoAsync(a.Provider, $"delegacao:{parada.Id:N}")).Estado);
        Assert.Equal(EstadoDaExecucao.AguardandoDelegacoes, await a.EstadoAsync(a.IdOrigem));
        // Outro proprietário: indistinguível de inexistente.
        Assert.Equal(ResultadoDoCancelamento.NaoEncontrada,
            (await a.DelegacoesAsync(s => s.CancelarAsync(new ProprietarioDaExecucao("telegram", 2002), rodando.Id, null))).Resultado);

        // Com dono: só o pedido; quem tem a posse o honra e a origem é retomada.
        Assert.Equal(ResultadoDoCancelamento.Solicitada, (await a.DelegacoesAsync(s => s.CancelarAsync(Dono, rodando.Id, null))).Resultado);
        Assert.Equal(EstadoDaDelegacao.EmAndamento, await a.EstadoDaDelegacaoAsync(rodando.Id));
        var sessao = await a.SessaoAsync();
        await AppAsync(a.Provider, f => f.VincularSessaoAsync(filha.Posse, filha.IdEtapa!.Value, sessao));
        var desfecho = await a.ConcluirAsync(filha, Resposta, sessao);
        Assert.Equal((EstadoDaDelegacao.Cancelada, true), (desfecho.Estado, desfecho.Retomada));
        Assert.Null((await a.DelegacoesAsync(s => s.ObterAsync(Dono, rodando.Id)))!.Resultado);
        Assert.Equal(EstadoDaExecucao.Pendente, await a.EstadoAsync(a.IdOrigem));
    }

    [PostgreSqlFact]
    public async Task CancelamentoDaRaizPedeOCancelamentoDasFilhasEQuemTemAPosseOHonraSemRetomar()
    {
        await using var a = await Arvore.CriarAsync();
        var d1 = (await a.SolicitarAsync("Primeira")).Delegacao;
        var d2 = (await a.SolicitarAsync("Segunda")).Delegacao;
        await a.ConcluirOrigemAsync();
        var filha = await a.ReivindicarAsync();
        Assert.Equal(d1.IdExecucaoSubordinada, filha.IdExecucao);
        await a.DelegacoesAsync(s => s.IniciarAsync(filha.Posse, filha.IdEtapa!.Value));

        // A raiz espera (ninguém tem a posse dela): o dono a cancela; as filhas abertas recebem o pedido durável.
        await using (var escopo = a.Provider.CreateAsyncScope())
            Assert.Equal(ResultadoDoCancelamento.Cancelada, (await escopo.ServiceProvider.GetRequiredService<IControleDeExecucoesAppService>()
                .SolicitarCancelamentoAsync(Dono, $"E{a.NumeroOrigem}", "mudei de ideia")).Resultado);
        Assert.Equal(EstadoDaExecucao.Cancelada, await a.EstadoAsync(a.IdOrigem));
        Assert.Equal(2L, await a.Escalar($"SELECT count(*) FROM orquestracao.eventos WHERE tipo = {(int)TipoDeEventoDeOrquestracao.CancelamentoSolicitado} " +
            $"AND id_execucao IN ('{d1.IdExecucaoSubordinada}', '{d2.IdExecucaoSubordinada}')"));
        Assert.True(await AppAsync(a.Provider, f => f.CancelamentoSolicitadoAsync(d1.IdExecucaoSubordinada)));

        // A que está rodando: o executor confere no heartbeat e grava Cancelada sob posse (como faz o processador).
        var avaliada = await AppAsync(a.Provider, f => f.AvaliarAsync(filha));
        Assert.Equal(AcaoDoTrabalho.Cancelar, avaliada.Acao);
        await AppAsync(a.Provider, f => f.CancelarSolicitadaAsync(filha.Posse, avaliada.Alvo!));
        // A que estava na fila: avaliada como Cancelar quando reivindicada, nunca executada.
        var segunda = await a.ReivindicarAsync();
        var avaliacao = await AppAsync(a.Provider, f => f.AvaliarAsync(segunda));
        Assert.Equal(AcaoDoTrabalho.Cancelar, avaliacao.Acao);
        await AppAsync(a.Provider, f => f.CancelarSolicitadaAsync(segunda.Posse, avaliacao.Alvo!));

        Assert.Equal(EstadoDaDelegacao.Cancelada, await a.EstadoDaDelegacaoAsync(d1.Id));
        Assert.Equal(EstadoDaDelegacao.Cancelada, await a.EstadoDaDelegacaoAsync(d2.Id));
        Assert.Equal(EstadoDaExecucao.Cancelada, await a.EstadoAsync(a.IdOrigem));
        Assert.Equal(1L, await a.Escalar($"SELECT count(*) FROM orquestracao.etapas_da_execucao WHERE id_execucao = '{a.IdOrigem}'"));
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.trabalhos_da_fila WHERE chave_de_deduplicacao LIKE 'retomar-origem:%'"));
    }

    [PostgreSqlFact]
    public async Task TentativasEsgotadasNaSubordinadaFalhamADelegacaoERetomamAOrigem()
    {
        await using var a = await Arvore.CriarAsync();
        var d = (await a.SolicitarAsync("Conferir o índice por estado")).Delegacao;
        await a.ConcluirOrigemAsync();
        Assert.Equal(EstadoDaExecucao.AguardandoDelegacoes, await a.EstadoAsync(a.IdOrigem));
        var filha = await a.ReivindicarAsync();
        var avaliacao = await AppAsync(a.Provider, f => f.AvaliarAsync(filha));
        await AppAsync(a.Provider, f => f.EncerrarPorTentativasEsgotadasAsync(filha.Posse, avaliacao.Alvo!, "Tentativas esgotadas na fila (teste)."));
        Assert.Equal(EstadoDaDelegacao.Falhou, await a.EstadoDaDelegacaoAsync(d.Id));
        Assert.Equal(EstadoDaExecucao.Falhou, await a.EstadoAsync(d.IdExecucaoSubordinada));
        Assert.Equal(EstadoDaExecucao.Pendente, await a.EstadoAsync(a.IdOrigem));
    }

    // ---- apoio ----

    // ---- PR 3: pela sessão do dante_coordenacao ----

    // A solicitação pela ferramenta grava sob a posse do trabalho da origem e só da sessão vinculada à etapa: sem
    // vínculo, outra sessão, posse antiga ou etapa já concluída, nada é criado. O trabalho da origem continua
    // reivindicado pela mesma posse. O acompanhamento mostra estado e último marco só ao dono.
    [PostgreSqlFact]
    public async Task PelaSessaoSoAVinculadaSobAPosseAtualDelegaEOAcompanhamentoMostraOProgresso()
    {
        await using var a = await Arvore.CriarAsync();
        var sessao = await a.SessaoAsync();
        var outra = await a.SessaoAsync();
        SolicitacaoDeDelegacaoDto Pedido(string objetivo) =>
            new(Dono, a.IdOrigem, a.IdEtapa, "revisor", "consulta.v1", Pergunta, objetivo);
        Task<DelegacaoSolicitadaDto> Solicitar(PosseDoTrabalho posse, long numero, string objetivo) =>
            a.DelegacoesAsync(s => s.SolicitarSobPosseAsync(posse, numero, Pedido(objetivo)));

        // Sem vínculo: nenhuma sessão é a da etapa.
        Assert.Equal(DelegacoesAppService.SessaoNaoAutorizada,
            (await Assert.ThrowsAsync<DelegacaoRecusadaException>(() => Solicitar(a.Origem.Posse, sessao, "x"))).Message);
        await AppAsync(a.Provider, f => f.VincularSessaoAsync(a.Origem.Posse, a.IdEtapa, sessao));
        // Outra sessão do mesmo dono, ou uma que nem existe: recusadas.
        await Assert.ThrowsAsync<DelegacaoRecusadaException>(() => Solicitar(a.Origem.Posse, outra, "x"));
        await Assert.ThrowsAsync<DelegacaoRecusadaException>(() => Solicitar(a.Origem.Posse, 999_999, "x"));
        // Fencing: geração antiga não grava.
        await Assert.ThrowsAsync<PosseDoTrabalhoPerdidaException>(() =>
            Solicitar(a.Origem.Posse with { Geracao = a.Origem.Posse.Geracao - 1 }, sessao, "x"));
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.delegacoes"));

        var nova = await Solicitar(a.Origem.Posse, sessao, "Conferir o índice por estado");
        Assert.True(nova.Nova);
        var repetida = await Solicitar(a.Origem.Posse, sessao, "Conferir o índice por estado");
        Assert.Equal((nova.Delegacao.Id, false), (repetida.Delegacao.Id, repetida.Nova));
        var trabalho = await TrabalhoAsync(a.Provider, $"tarefa:{a.IdOrigem:N}");
        Assert.Equal((EstadoDoTrabalho.Reivindicado, a.Origem.Geracao), (trabalho.Estado, trabalho.Geracao));
        Assert.Equal(1L, await a.Escalar("SELECT count(*) FROM orquestracao.delegacoes"));

        var antes = Assert.Single(await a.DelegacoesAsync(s => s.AcompanharDaOrigemAsync(Dono, a.IdOrigem)));
        Assert.Equal((EstadoDaDelegacao.Aceita, (MarcoDaDelegacao?)null), (antes.Delegacao.Estado, antes.UltimoMarco));
        Assert.Empty(await a.DelegacoesAsync(s => s.AcompanharDaOrigemAsync(new ProprietarioDaExecucao("telegram", 2002), a.IdOrigem)));

        // Etapa concluída: a posse acabou e a sessão não pede mais nada.
        await a.ConcluirOrigemAsync();
        await Assert.ThrowsAsync<PosseDoTrabalhoPerdidaException>(() => Solicitar(a.Origem.Posse, sessao, "Outra pergunta"));
        Assert.Equal(1L, await a.Escalar("SELECT count(*) FROM orquestracao.delegacoes"));

        var filha = await a.ReivindicarAsync();
        await a.DelegacoesAsync(s => s.IniciarAsync(filha.Posse, filha.IdEtapa!.Value));
        await a.DelegacoesAsync(s => s.RegistrarProgressoAsync(filha.Posse, filha.IdEtapa!.Value, MarcoDaDelegacao.SessaoAberta));
        await a.DelegacoesAsync(s => s.RegistrarProgressoAsync(filha.Posse, filha.IdEtapa!.Value, MarcoDaDelegacao.TurnoEnviado));
        var depois = Assert.Single(await a.DelegacoesAsync(s => s.AcompanharDaOrigemAsync(Dono, a.IdOrigem)));
        Assert.Equal((EstadoDaDelegacao.EmAndamento, (MarcoDaDelegacao?)MarcoDaDelegacao.TurnoEnviado),
            (depois.Delegacao.Estado, depois.UltimoMarco));
    }

    internal sealed record Tentativa(string Destinatario, DelegacaoSolicitadaDto? Ok, MotivoDaRecusaDeDelegacao? Recusa, Exception? Erro);

    internal sealed class Arvore : IAsyncDisposable
    {
        private Banco banco = null!;
        public ServiceProvider Provider { get; private set; } = null!;
        public RelogioManual Relogio { get; } = new(DateTimeOffset.UtcNow);
        public Dictionary<string, Guid> Agentes { get; } = [];
        public Guid IdOrigem { get; private set; }
        public long NumeroOrigem { get; private set; }
        public Guid IdEtapa { get; private set; }
        public TrabalhoReivindicado Origem { get; private set; } = null!;
        public IdentidadeDoProcessador Eu { get; } = Processador();

        // Tarefa de pesquisador em @dante, criada e iniciada pela fila; destinatários com cada situação de cadastro.
        public static async Task<Arvore> CriarAsync()
        {
            var a = new Arvore { banco = await FilaAsync() };
            a.Provider = Provider(a.banco.ConnectionString, a.Relogio);
            await using var escopo = a.Provider.CreateAsyncScope();
            var s = escopo.ServiceProvider;
            var solicitante = await a.AgenteAsync(s, "pesquisador",
                ConfiguracaoDoSolicitante(["revisor", "outro", "fraco", "inativo", "adulterado", "ninguem"]));
            await a.AgenteAsync(s, "revisor", ConfiguracaoDoDestinatario());
            await a.AgenteAsync(s, "outro", ConfiguracaoDoDestinatario([CapacidadeDoAgente.Pesquisar]));
            await a.AgenteAsync(s, "fraco", ConfiguracaoDoDestinatario([]));
            await a.AgenteAsync(s, "estranho", ConfiguracaoDoDestinatario());
            await a.AgenteAsync(s, "adulterado", ConfiguracaoDoDestinatario());
            var inativo = await a.AgenteAsync(s, "inativo", ConfiguracaoDoDestinatario());
            inativo.Agente.Desativar(a.Relogio.GetUtcNow());
            await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
            var fila = s.GetRequiredService<IFilaDeTrabalho>();
            var numero = await s.GetRequiredService<IExecucaoRepository>().ReservarNumeroAsync();
            var origem = new Execucao(numero, TipoDeExecucao.Tarefa, "telegram", 1001,
                """{"modo":"Repository","alias":"@dante","diretorio":"/repos/dante","vinculados":[]}""", "Investigar o índice",
                fila.Agora, solicitante.Versao.Id);
            var etapa = new EtapaDaExecucao(origem, 1, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica, fila.Agora,
                AgentKind.Codex);
            await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(origem);
            await s.GetRequiredService<IEtapaDaExecucaoRepository>().AdicionarAsync(etapa);
            await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(origem, etapa, $"tarefa:{origem.Id:N}", fila.Agora));
            await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
            (a.IdOrigem, a.NumeroOrigem, a.IdEtapa) = (origem.Id, numero, etapa.Id);
            a.Origem = (await FilaDuravelPostgreSqlTests.ReivindicarAsync(a.Provider, a.Eu, LeaseLongo))!;
            await AppAsync(a.Provider, f => f.IniciarEtapaAsync(a.Origem.Posse, etapa.Id));
            return a;
        }

        private async Task<(Agente Agente, VersaoDoAgente Versao)> AgenteAsync(IServiceProvider s, string handle,
            ConfiguracaoDoAgente configuracao)
        {
            var agente = new Agente(handle, null, null, 1, Relogio.GetUtcNow());
            var versao = agente.NovaVersao(configuracao, 1, Relogio.GetUtcNow());
            await s.GetRequiredService<IAgenteRepository>().AdicionarAsync(agente);
            await s.GetRequiredService<IVersaoDoAgenteRepository>().AdicionarAsync(versao);
            await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
            Agentes[handle] = agente.Id;
            return (agente, versao);
        }

        public Task<DelegacaoSolicitadaDto> SolicitarAsync(string objetivo, string destinatario = "revisor",
            string contrato = "consulta.v1", string entrada = Pergunta, ProprietarioDaExecucao? proprietario = null,
            bool escrita = false, Guid? idEtapa = null, int? minutosDeDuracao = null) =>
            DelegacoesAsync(s => s.SolicitarAsync(new SolicitacaoDeDelegacaoDto(proprietario ?? Dono, IdOrigem,
                idEtapa ?? IdEtapa, destinatario, contrato, entrada, objetivo, MinutosDeDuracao: minutosDeDuracao,
                Escrita: escrita)));

        // Todas as solicitações partem juntas, cada uma no seu escopo (conexão própria).
        public async Task<IReadOnlyList<Tentativa>> SimultaneasAsync(IEnumerable<(string Objetivo, string Destinatario)> pedidos)
        {
            var largada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tarefas = pedidos.Select(p => Task.Run(async () =>
            {
                await largada.Task;
                try
                {
                    return new Tentativa(p.Destinatario, await SolicitarAsync(p.Objetivo, p.Destinatario), null, null);
                }
                catch (DelegacaoRecusadaException recusa)
                {
                    return new Tentativa(p.Destinatario, null, recusa.Motivo, null);
                }
                catch (Exception erro)
                {
                    return new Tentativa(p.Destinatario, null, null, erro);
                }
            })).ToList();
            largada.SetResult();
            return await Task.WhenAll(tarefas);
        }

        public async Task<EstadoDaEtapa> ConcluirOrigemAsync() => await AppAsync(Provider,
            f => f.ConcluirEtapaAsync(Origem.Posse, IdEtapa, new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida, 0)));

        public async Task<TrabalhoReivindicado> ReivindicarAsync(TimeSpan? lease = null) =>
            (await FilaDuravelPostgreSqlTests.ReivindicarAsync(Provider, Processador(), lease ?? LeaseLongo))!;

        public Task<DesfechoDaDelegacaoDto> ConcluirAsync(TrabalhoReivindicado filha, string resultado, long sessao) =>
            DelegacoesAsync(s => s.ConcluirAsync(filha.Posse, filha.IdEtapa!.Value, new EntregaDaDelegacaoDto(resultado, sessao)));

        public async Task<long> SessaoAsync()
        {
            await using var escopo = Provider.CreateAsyncScope();
            var ledger = escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>();
            var numero = await ledger.ReservarNumeroDeSessaoAsync();
            await ledger.RegistrarSessaoCriadaAsync(new SessaoRegistradaDto(numero, AgentKind.Codex, "telegram", 1001,
                JobExecutionContext.Repository("@dante", "/repos/dante"), AgentPermissionProfile.Plan, null, null,
                Relogio.GetUtcNow(), Eu.Boot, Eu.Host));
            await ledger.RegistrarSessaoIniciadaAsync(numero, "u", null, Relogio.GetUtcNow());
            return numero;
        }

        public async Task<T> DelegacoesAsync<T>(Func<IDelegacoesAppService, Task<T>> acao)
        {
            await using var escopo = Provider.CreateAsyncScope();
            return await acao(escopo.ServiceProvider.GetRequiredService<IDelegacoesAppService>());
        }

        public async Task DelegacoesAsync(Func<IDelegacoesAppService, Task> acao)
        {
            await using var escopo = Provider.CreateAsyncScope();
            await acao(escopo.ServiceProvider.GetRequiredService<IDelegacoesAppService>());
        }

        public async Task<EstadoDaExecucao> EstadoAsync(Guid idExecucao) =>
            (EstadoDaExecucao)await Escalar($"SELECT estado FROM orquestracao.execucoes WHERE id = '{idExecucao}'");

        public async Task<EstadoDaDelegacao> EstadoDaDelegacaoAsync(Guid id) =>
            (EstadoDaDelegacao)await Escalar($"SELECT estado FROM orquestracao.delegacoes WHERE id = '{id}'");

        public Task<long> Escalar(string sql) => FilaDuravelPostgreSqlTests.Escalar(banco, sql);

        public async Task<T> Escalar<T>(string sql)
        {
            await using var conexao = new Npgsql.NpgsqlConnection(banco.ConnectionString);
            await conexao.OpenAsync();
            await using var comando = new Npgsql.NpgsqlCommand(sql, conexao);
            return (T)(await comando.ExecuteScalarAsync())!;
        }

        public Task SqlAsync(string sql) => Executar(banco, sql);

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await banco.DisposeAsync();
        }
    }
}
