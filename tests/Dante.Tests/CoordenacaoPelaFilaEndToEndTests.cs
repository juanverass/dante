using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Application.Agentes;
using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Application.Grupos;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Domain.Grupos;
using Dante.Worker.Coordenacao;
using Dante.Worker.Execucoes;
using Dante.Worker.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using static Dante.Tests.FilaDuravelPostgreSqlTests;

namespace Dante.Tests;

// #310 (AD-75), PR 2, de ponta a ponta contra PostgreSQL descartável: o ProcessadorDaFila real com o
// ExecutorDeCoordenacoesDaFila e o ExecutorDeDelegacoesDaFila reais, sessões reservadas com drivers falsos, o ledger
// espelhado e o dante_coordenacao real só para as entregas das unidades (o teste faz o papel do processo MCP). Fluxo:
// coordenação criada → coordenador propõe (JSON da última mensagem) → validação pura no servidor (uma correção) → gate
// do plano → aprovação humana pelo hash → unidades despachadas uma por vez como delegações consulta.v1 → resultados
// pela ferramenta → consolidação mecânica. Também: plano inválido duas vezes, correção e recusa no gate, unidade sem
// resposta (motivo do provider preservado) com replanejamento, cancelamento pelo dono e Worker parado no meio.
[Collection("FilaDuravel")]
public sealed class CoordenacaoPelaFilaEndToEndTests
{
    private const string Resposta = """{"resposta":"Há ix_estado.","confianca":"alta","evidencias":[{"tipo":"arquivo","referencia":"README.md:1"}]}""";

    // Duas unidades: a consulta depende do índice; declaradas fora da ordem topológica.
    private const string Plano = """
        {"unidades":[
          {"id":"consulta","papel":"rev","contrato":"consulta.v1","objetivo":"Conferir a consulta por estado","entrada":{"pergunta":"O índice cobre a consulta?"},"dependeDe":["indice"]},
          {"id":"indice","papel":"pesq","contrato":"consulta.v1","objetivo":"Levantar os índices","entrada":{"pergunta":"Quais índices existem?"},"criterios":"Citar o arquivo."}
        ]}
        """;

    // Três unidades: a migração é independente das outras duas, mas só roda depois delas (paralelismo 1, ordem
    // topológica gravada com empate pela proposta).
    private const string PlanoComTres = """
        {"unidades":[
          {"id":"consulta","papel":"rev","contrato":"consulta.v1","objetivo":"Conferir a consulta por estado","entrada":{"pergunta":"O índice cobre a consulta?"},"dependeDe":["indice"]},
          {"id":"indice","papel":"pesq","contrato":"consulta.v1","objetivo":"Levantar os índices","entrada":{"pergunta":"Quais índices existem?"},"criterios":"Citar o arquivo."},
          {"id":"migracao","papel":"rev","contrato":"consulta.v1","objetivo":"Achar a migration do índice","entrada":{"pergunta":"Qual migration cria o índice?"}}
        ]}
        """;

    // Escrita exige o gateway (#311) e uma unidade para o coordenador: duas recusas, nenhuma gravação de plano.
    private const string PlanoInvalido = """
        {"unidades":[
          {"id":"u1","papel":"pesq","contrato":"consulta.v1","objetivo":"Mudar o índice","entrada":{"pergunta":"P?"},"escrita":true},
          {"id":"u2","papel":"coord","contrato":"consulta.v1","objetivo":"Eu mesmo","entrada":{"pergunta":"P?"}}
        ]}
        """;

    [PostgreSqlFact]
    public async Task PlanoCorrigidoUmaVezAprovadoPeloHashEDespachadoEmSequenciaAteAConsolidacao()
    {
        await using var a = await Ambiente.CriarAsync();
        var codigo = await a.CoordenacaoAsync();
        a.Iniciar();

        // O coordenador planeja numa sessão reservada da versão congelada: plan, sem o ambiente do Worker, sem
        // ferramentas de coordenação, com os papéis do snapshot e o schema da entrada; a sessão fica vinculada à etapa.
        var coordenador = await a.DriverAsync(c => c.StartsWith("turn:Planejamento da coordenação", StringComparison.Ordinal));
        var opcoes = coordenador.StartOptions!;
        Assert.Equal((AgentPermissionProfile.Plan, a.Repositorio), (opcoes.Profile, opcoes.WorkingDirectory));
        Assert.True(opcoes.ToolServers is null or { Count: 0 });
        Assert.DoesNotContain(opcoes.EnvironmentVariables ?? new Dictionary<string, string>(), v => v.Key == "DANTE_SEGREDO_DE_TESTE");
        var prompt = coordenador.TurnInputs[0].Text;
        Assert.Contains("- pesq: agente pesquisador v1; capacidades: Pesquisar", prompt);
        Assert.Contains("- rev: agente revisor v1", prompt);
        Assert.DoesNotContain("- coord:", prompt);
        Assert.Contains("no máximo 4 unidade(s)", prompt);
        Assert.Contains("\"pergunta\"", prompt);
        Assert.Contains("Investigar o índice por estado", prompt);

        // Inválido: as recusas voltam ao coordenador na mesma sessão, uma vez; nada de plano gravado.
        Responder(coordenador, $"Plano:\n```json\n{PlanoInvalido}\n```");
        await Eventually(() => Task.FromResult(coordenador.TurnInputs.Count == 2));
        Assert.Contains("RequerGateway (u1)", coordenador.TurnInputs[1].Text);
        Assert.Contains("UnidadeDoCoordenador (u2)", coordenador.TurnInputs[1].Text);
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.planos_da_coordenacao"));
        Responder(coordenador, $"Corrigido:\n```json\n{PlanoComTres}\n```");

        // Gate aberto com o hash calculado no servidor; nenhuma delegação antes da decisão.
        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.AguardandoAprovacao);
        var aguardando = (await a.CoordenacaoDtoAsync())!;
        var plano = aguardando.PlanoAtual!;
        Assert.Equal((1, EstadoDoPlano.AguardandoAprovacao, 3), (plano.Revisao, plano.Estado, plano.QuantidadeDeUnidades));
        Assert.Equal(EstadoDoPedidoDeAprovacao.Pendente, aguardando.PedidoPendente!.Estado);
        Assert.Equal(plano.Hash, aguardando.PedidoPendente.HashDoArtefato);
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.delegacoes"));
        var planejamento = aguardando.Etapas.Single(e => e.Passo == PassoDaCoordenacao.Planejamento);
        Assert.NotNull(planejamento.CodigoDaSessao);
        Assert.Equal(1L, await a.Escalar($"SELECT count(*) FROM orquestracao.eventos WHERE tipo = {(int)TipoDeEventoDeOrquestracao.PlanoRecusado}"));

        // Hash errado ou de outra pessoa: nada decidido. Com o hash exibido, aprovado.
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.DecidirAsync(codigo, TipoDeDecisaoHumana.Aprovar,
            new string('0', 12)));
        Assert.Equal(ResultadoDaDecisao.NaoEncontrada, (await a.DecidirAsync(codigo, TipoDeDecisaoHumana.Aprovar,
            plano.Hash[..12], dono: 999)).Resultado);
        // Editar o destinatário depois do snapshot não muda a versão que roda a unidade.
        await a.EditarAgenteAsync("pesquisador");
        Assert.Equal(ResultadoDaDecisao.Aprovada,
            (await a.DecidirAsync(codigo, TipoDeDecisaoHumana.Aprovar, plano.Hash[..12])).Resultado);

        // Primeira unidade (índice, na ordem topológica): delegação pelo plano e pelo snapshot, sem concessão de
        // coordenação no agente; só ela roda (paralelismo 1), embora a migração também esteja pronta.
        var indice = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal));
        Assert.Contains("Levantar os índices", indice.TurnInputs[0].Text);
        Assert.Single(await a.DelegacoesAsync());
        Assert.Equal(1, a.Drivers.Created.Count(d => d.Calls.Any(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal))));
        Assert.Equal(EstadoDaExecucao.AguardandoDelegacoes, await a.EstadoAsync());
        Assert.Equal(a.VersaoOriginal, (await a.DelegacoesAsync()).Single().IdVersaoDestinataria);
        Assert.True((bool)JsonNode.Parse(await a.EntregarAsync(indice, Resposta))!["registrado"]!);
        Responder(indice, "Entregue.");

        var consulta = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal), depoisDe: indice);
        Assert.Contains("Conferir a consulta por estado", consulta.TurnInputs[0].Text);
        Assert.True((bool)JsonNode.Parse(await a.EntregarAsync(consulta, Resposta.Replace("Há ix_estado.", "Cobre.")))!["registrado"]!);
        Responder(consulta, "Entregue.");
        var migracao = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal), depoisDe: consulta);
        Assert.Contains("Achar a migration do índice", migracao.TurnInputs[0].Text);
        Assert.True((bool)JsonNode.Parse(await a.EntregarAsync(migracao, Resposta))!["registrado"]!);
        Responder(migracao, "Entregue.");

        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.Concluida);
        // Consolidação mecânica: cada unidade atribuída ao agente, à versão congelada e à sessão que entregou, com o
        // resultado persistido e o hash; a revisão com a decisão do gate e a sessão do planejamento.
        var trilha = (await a.ConsolidarAsync(codigo))!;
        Assert.Equal((3, 0, 0, 0), (trilha.Respondidas, trilha.SemResposta, trilha.Ignoradas, trilha.Abertas));
        Assert.Equal(["indice", "consulta", "migracao"], trilha.Unidades.Select(u => u.Unidade));
        Assert.All(trilha.Unidades, u => Assert.NotNull(u.CodigoDaSessao));
        Assert.All(trilha.Unidades, u => Assert.Equal(64, u.HashDoResultado!.Length));
        Assert.Equal(("pesquisador", 1), (trilha.Unidades[0].Handle, trilha.Unidades[0].NumeroDaVersao));
        Assert.Contains("Cobre.", trilha.Unidades[1].Resultado);
        var revisao = Assert.Single(trilha.Revisoes);
        Assert.Equal((EstadoDoPlano.Aprovado, EstadoDoPedidoDeAprovacao.Aprovado, "telegram:123", planejamento.CodigoDaSessao),
            (revisao.Estado, revisao.Decisao, revisao.Decisor, revisao.CodigoDaSessaoDoPlanejamento));
        // Proveniência no ledger: raiz → plano → unidade → delegação → subordinada → sessão da etapa da subordinada.
        Assert.Equal(3L, await a.Escalar($"""
            SELECT count(*) FROM orquestracao.etapas_da_execucao u
            JOIN orquestracao.planos_da_coordenacao p ON p.id = u.id_plano AND p.estado = {(int)EstadoDoPlano.Aprovado}
            JOIN orquestracao.delegacoes d ON d.id_etapa_origem = u.id AND d.estado = {(int)EstadoDaDelegacao.Respondida}
            JOIN orquestracao.etapas_da_execucao s ON s.id_execucao = d.id_execucao_subordinada
                 AND s.id_sessao_de_agente = d.id_sessao_do_resultado
            WHERE u.id_execucao = '{a.IdExecucao}'
            """));
    }

    [PostgreSqlFact]
    public async Task PlanoInvalidoDepoisDaCorrecaoTerminaSemPlanoFilhosNemTrabalhos()
    {
        await using var a = await Ambiente.CriarAsync();
        await a.CoordenacaoAsync();
        a.Iniciar();
        var coordenador = await a.DriverAsync(c => c.StartsWith("turn:Planejamento da coordenação", StringComparison.Ordinal));
        Responder(coordenador, $"```json\n{PlanoInvalido}\n```");
        await Eventually(() => Task.FromResult(coordenador.TurnInputs.Count == 2));
        // Sem bloco JSON também conta como recusa.
        Responder(coordenador, "Não sei planejar isso.");
        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.Falhou);

        Assert.Equal(2, coordenador.TurnInputs.Count);
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.planos_da_coordenacao"));
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.pedidos_de_aprovacao"));
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.delegacoes"));
        Assert.Equal(1L, await a.Escalar("SELECT count(*) FROM orquestracao.execucoes"));
        Assert.Equal(1L, await a.Escalar("SELECT count(*) FROM orquestracao.trabalhos_da_fila"));
        Assert.Equal(2L, await a.Escalar($"SELECT count(*) FROM orquestracao.eventos WHERE tipo = {(int)TipoDeEventoDeOrquestracao.PlanoRecusado}"));
        var coordenacao = (await a.CoordenacaoDtoAsync())!;
        Assert.Contains("Plano inválido depois da correção permitida", coordenacao.Erro);
        Assert.Equal(EstadoDaEtapa.Falhou, Assert.Single(coordenacao.Etapas).Estado);
        Assert.Single(a.Drivers.Created);
    }

    [PostgreSqlFact]
    public async Task CorrecaoPedidaNoGateGeraRevisaoSubstitutaERecusarCancelaSemDelegar()
    {
        await using var a = await Ambiente.CriarAsync();
        var codigo = await a.CoordenacaoAsync();
        a.Iniciar();
        var primeiro = await a.DriverAsync(c => c.StartsWith("turn:Planejamento da coordenação", StringComparison.Ordinal));
        Responder(primeiro, $"```json\n{Plano}\n```");
        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.AguardandoAprovacao);
        var hash = (await a.CoordenacaoDtoAsync())!.PlanoAtual!.Hash;

        // Instrução com aparência de credencial é recusada antes de qualquer gravação.
        await Assert.ThrowsAsync<ArgumentException>(() => a.DecidirAsync(codigo, TipoDeDecisaoHumana.Corrigir, hash[..12],
            "use ghp_0123456789abcdefghijklmnopqrstuvwxyz"));
        Assert.Equal(ResultadoDaDecisao.CorrecaoAgendada, (await a.DecidirAsync(codigo, TipoDeDecisaoHumana.Corrigir,
            hash[..12], "Pergunte também pelo índice parcial.")).Resultado);
        var segundo = await a.DriverAsync(c => c.StartsWith("turn:Planejamento da coordenação", StringComparison.Ordinal),
            depoisDe: primeiro);
        Assert.Contains("Pergunte também pelo índice parcial.", segundo.TurnInputs[0].Text);
        Assert.Contains("revisão 2 do plano", segundo.TurnInputs[0].Text);
        Responder(segundo, $"```json\n{Plano.Replace("Quais índices existem?", "Quais índices, inclusive parciais?")}\n```");
        await Eventually(async () => (await a.CoordenacaoDtoAsync())!.Planos.Count == 2 &&
            await a.EstadoAsync() == EstadoDaExecucao.AguardandoAprovacao);

        var coordenacao = (await a.CoordenacaoDtoAsync())!;
        Assert.Equal([(1, EstadoDoPlano.Substituido, MotivoDoPlano.Inicial), (2, EstadoDoPlano.AguardandoAprovacao, MotivoDoPlano.PedidoHumano)],
            coordenacao.Planos.Select(p => (p.Revisao, p.Estado, p.Motivo)));
        Assert.Equal(EstadoDoPedidoDeAprovacao.CorrecaoSolicitada, coordenacao.Planos[0].Pedido!.Estado);
        // O hash da revisão 1 não decide a revisão 2.
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.DecidirAsync(codigo, TipoDeDecisaoHumana.Aprovar, hash[..12]));
        Assert.Equal(ResultadoDaDecisao.Recusada, (await a.DecidirAsync(codigo, TipoDeDecisaoHumana.Recusar)).Resultado);

        coordenacao = (await a.CoordenacaoDtoAsync())!;
        Assert.Equal(EstadoDaExecucao.Cancelada, coordenacao.Estado);
        Assert.Equal(EstadoDoPlano.Recusado, coordenacao.Planos[1].Estado);
        Assert.All(coordenacao.Etapas, e => Assert.True(e.Estado is EstadoDaEtapa.Concluida or EstadoDaEtapa.Cancelada));
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.delegacoes"));
        var trilha = (await a.ConsolidarAsync(codigo))!;
        Assert.Equal([EstadoDoPedidoDeAprovacao.CorrecaoSolicitada, EstadoDoPedidoDeAprovacao.Recusado],
            trilha.Revisoes.Select(r => r.Decisao!.Value));
        Assert.Empty(trilha.Unidades);
    }

    [PostgreSqlFact]
    public async Task UnidadeSemRespostaPreservaOMotivoDoProviderEReplanejaComGateProprio()
    {
        await using var a = await Ambiente.CriarAsync();
        var codigo = await a.CoordenacaoAsync();
        a.Iniciar();
        var coordenador = await a.DriverAsync(c => c.StartsWith("turn:Planejamento da coordenação", StringComparison.Ordinal));
        Responder(coordenador, $"```json\n{Plano}\n```");
        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.AguardandoAprovacao);
        await a.DecidirAsync(codigo, TipoDeDecisaoHumana.Aprovar, (await a.CoordenacaoDtoAsync())!.PlanoAtual!.Hash[..12]);

        // Limite do provider (D9): chega como falha do turno, com o texto do provider; nenhum Retry-After é inventado.
        const string limite = "You've hit your usage limit. Try again at 3:05 PM.";
        var indice = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal));
        indice.Emit(new TurnCompletedEvent(AgentTurnOutcome.Failed, limite));

        // Replanejamento por unidade sem resposta: o coordenador recebe o desfecho como dado não confiável.
        var replanejar = await a.DriverAsync(c => c.StartsWith("turn:Planejamento da coordenação", StringComparison.Ordinal),
            depoisDe: coordenador);
        var prompt = replanejar.TurnInputs[0].Text;
        Assert.Contains("revisão 2 do plano", prompt);
        Assert.Contains("<<<DADO_NAO_CONFIAVEL", prompt);
        Assert.Contains($"indice (pesq): Falhou; motivo: {limite}", prompt);
        // Uma vaga do orçamento foi consumida pela delegação que falhou.
        Assert.Contains("no máximo 3 unidade(s)", prompt);
        Responder(replanejar, $"```json\n{Plano}\n```");
        await Eventually(async () => (await a.CoordenacaoDtoAsync())!.Planos.Count == 2 &&
            await a.EstadoAsync() == EstadoDaExecucao.AguardandoAprovacao);

        var coordenacao = (await a.CoordenacaoDtoAsync())!;
        Assert.Equal([(EstadoDoPlano.Substituido, MotivoDoPlano.Inicial), (EstadoDoPlano.AguardandoAprovacao, MotivoDoPlano.UnidadeSemResposta)],
            coordenacao.Planos.Select(p => (p.Estado, p.Motivo)));
        var antigas = coordenacao.Etapas.Where(e => e is { Passo: PassoDaCoordenacao.Unidade, Revisao: 1 }).ToList();
        Assert.Equal([("indice", EstadoDaEtapa.Falhou), ("consulta", EstadoDaEtapa.Ignorada)],
            antigas.Select(e => (e.Unidade!, e.Estado)));
        Assert.Equal("Unidade substituída pela revisão 2 do plano.", antigas[1].Motivo);

        // Cancelada pelo dono no gate da revisão 2: o plano em espera termina Cancelado; a revisão 1 continua como foi.
        await a.CancelarAsync(codigo);
        var trilha = (await a.ConsolidarAsync(codigo))!;
        Assert.Equal(EstadoDaExecucao.Cancelada, trilha.Estado);
        Assert.Equal([EstadoDoPlano.Substituido, EstadoDoPlano.Cancelado], trilha.Revisoes.Select(r => r.Estado));
        Assert.Equal((0, 1, 1, 0), (trilha.Respondidas, trilha.SemResposta, trilha.Ignoradas, trilha.Abertas));
        var semResposta = trilha.Unidades.Single(u => u.Situacao == SituacaoDaUnidade.SemResposta);
        Assert.Equal((EstadoDaDelegacao.Falhou, limite), (semResposta.EstadoDaDelegacao, semResposta.Motivo));
    }

    [PostgreSqlFact]
    public async Task CancelamentoDoDonoDuranteAUnidadeEncerraASubordinadaSemNovoDespacho()
    {
        await using var a = await Ambiente.CriarAsync();
        var codigo = await a.CoordenacaoAsync();
        a.Iniciar();
        var coordenador = await a.DriverAsync(c => c.StartsWith("turn:Planejamento da coordenação", StringComparison.Ordinal));
        Responder(coordenador, $"```json\n{Plano}\n```");
        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.AguardandoAprovacao);
        await a.DecidirAsync(codigo, TipoDeDecisaoHumana.Aprovar, (await a.CoordenacaoDtoAsync())!.PlanoAtual!.Hash[..12]);
        var indice = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal));

        // A raiz espera a delegação sem trabalho próprio: o cancelamento é direto, e a subordinada recebe o pedido.
        Assert.Equal(ResultadoDoCancelamento.Cancelada, (await a.CancelarAsync(codigo)).Resultado);
        await Eventually(async () => (await a.DelegacoesAsync()).Single().Estado == EstadoDaDelegacao.Cancelada);
        var coordenacao = (await a.CoordenacaoDtoAsync())!;
        Assert.Equal(EstadoDaExecucao.Cancelada, coordenacao.Estado);
        // O plano aprovado continua aprovado (o banco não o reabre); nenhuma etapa fica aberta.
        Assert.Equal(EstadoDoPlano.Aprovado, coordenacao.PlanoAtual!.Estado);
        Assert.All(coordenacao.Etapas, e => Assert.True(e.Estado is EstadoDaEtapa.Concluida or EstadoDaEtapa.Cancelada or
            EstadoDaEtapa.Ignorada));
        // Nenhuma outra unidade foi despachada.
        await Task.Delay(500);
        Assert.Single(await a.DelegacoesAsync());
        Assert.Single(a.Drivers.Created, d => d.Calls.Any(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal)));
        // A unidade em curso terminou sem resposta; a que nunca foi despachada, como não executada.
        var trilha = (await a.ConsolidarAsync(codigo))!;
        Assert.Equal((0, 1, 1), (trilha.Respondidas, trilha.SemResposta, trilha.Ignoradas));
    }

    [PostgreSqlFact]
    public async Task WorkerParadoDuranteAUnidadeRetomaPeloLedgerSemDuplicarDelegacao()
    {
        await using var a = await Ambiente.CriarAsync();
        var codigo = await a.CoordenacaoAsync();
        a.Iniciar();
        var coordenador = await a.DriverAsync(c => c.StartsWith("turn:Planejamento da coordenação", StringComparison.Ordinal));
        Responder(coordenador, $"```json\n{Plano}\n```");
        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.AguardandoAprovacao);
        await a.DecidirAsync(codigo, TipoDeDecisaoHumana.Aprovar, (await a.CoordenacaoDtoAsync())!.PlanoAtual!.Hash[..12]);
        var primeira = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal));

        // Parado com a unidade em curso: o próximo Worker retoma a subordinada (leitura) a partir do ledger.
        await a.PararAsync();
        a.Iniciar();
        var segunda = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal), depoisDe: primeira);
        Assert.Contains("Levantar os índices", segunda.TurnInputs[0].Text);
        Assert.True((bool)JsonNode.Parse(await a.EntregarAsync(segunda, Resposta))!["registrado"]!);
        Responder(segunda, "Entregue.");
        var consulta = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal), depoisDe: segunda);
        Assert.Contains("Conferir a consulta", consulta.TurnInputs[0].Text);
        Assert.True((bool)JsonNode.Parse(await a.EntregarAsync(consulta, Resposta))!["registrado"]!);
        Responder(consulta, "Entregue.");
        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.Concluida);

        Assert.Equal(2, (await a.DelegacoesAsync()).Count);
        Assert.Equal(2L, await a.Escalar($"SELECT count(*) FROM orquestracao.execucoes WHERE tipo = {(int)TipoDeExecucao.Delegada}"));
        Assert.Equal(2, (await a.ConsolidarAsync(codigo))!.Respondidas);
    }

    [PostgreSqlFact]
    public async Task DespachoRecusadoPeloDominioFalhaAUnidadeSemDelegacaoEReplanejamentoInvalidoEncerraTudo()
    {
        await using var a = await Ambiente.CriarAsync();
        var codigo = await a.CoordenacaoAsync();
        a.Iniciar();
        var coordenador = await a.DriverAsync(c => c.StartsWith("turn:Planejamento da coordenação", StringComparison.Ordinal));
        Responder(coordenador, $"```json\n{Plano}\n```");
        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.AguardandoAprovacao);
        // Desativar restringe: o despacho do índice é recusado pelo domínio, sem delegação nem sessão do destinatário.
        await a.DesativarAgenteAsync("pesquisador");
        await a.DecidirAsync(codigo, TipoDeDecisaoHumana.Aprovar, (await a.CoordenacaoDtoAsync())!.PlanoAtual!.Hash[..12]);

        // A consulta depende do índice: nada mais a despachar, e a revisão por unidade sem resposta é aberta.
        var replanejar = await a.DriverAsync(c => c.StartsWith("turn:Planejamento da coordenação", StringComparison.Ordinal),
            depoisDe: coordenador);
        Assert.Contains("Despacho recusado", replanejar.TurnInputs[0].Text);
        Assert.Contains("no máximo 4 unidade(s)", replanejar.TurnInputs[0].Text);
        Assert.Empty(await a.DelegacoesAsync());
        Responder(replanejar, "Sem plano.");
        await Eventually(() => Task.FromResult(replanejar.TurnInputs.Count == 2));
        Responder(replanejar, "Ainda sem plano.");
        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.Falhou);

        var trilha = (await a.ConsolidarAsync(codigo))!;
        // A revisão 1 continua aprovada; nenhuma revisão 2 foi gravada; a unidade bloqueada terminou não executada.
        Assert.Equal([EstadoDoPlano.Aprovado], trilha.Revisoes.Select(r => r.Estado));
        Assert.Equal([("indice", SituacaoDaUnidade.SemResposta), ("consulta", SituacaoDaUnidade.Ignorada)],
            trilha.Unidades.Select(u => (u.Unidade, u.Situacao)));
        Assert.StartsWith("Despacho recusado", trilha.Unidades[0].Motivo);
        Assert.Null(trilha.Unidades[0].EstadoDaDelegacao);
        Assert.DoesNotContain(a.Drivers.Created, d => d.Calls.Any(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal)));
        Assert.All((await a.CoordenacaoDtoAsync())!.Etapas, e => Assert.True(e.Estado is EstadoDaEtapa.Concluida or
            EstadoDaEtapa.Falhou or EstadoDaEtapa.Ignorada));
    }

    [PostgreSqlFact]
    public async Task SubordinadaDeUnidadeSoRodaComOSnapshotDaCoordenacaoIntegro()
    {
        await using var a = await Ambiente.CriarAsync();
        var codigo = await a.CoordenacaoAsync();
        a.Iniciar();
        var coordenador = await a.DriverAsync(c => c.StartsWith("turn:Planejamento da coordenação", StringComparison.Ordinal));
        Responder(coordenador, $"```json\n{Plano}\n```");
        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.AguardandoAprovacao);
        await a.PararAsync();
        await a.DecidirAsync(codigo, TipoDeDecisaoHumana.Aprovar, (await a.CoordenacaoDtoAsync())!.PlanoAtual!.Hash[..12]);

        // O avanço sob posse, à mão: a primeira unidade é despachada; a conferência da subordinada passa pelo plano e
        // pelo snapshot, sem concessão de coordenação no agente.
        var avanco = (await ReivindicarAsync(a.Provider, Processador()))!;
        var despacho = await a.AvancarAsync(avanco.Posse, avanco.IdEtapa!.Value);
        Assert.Equal((EstadoDaExecucao.AguardandoDelegacoes, false), (despacho.Estado, despacho.Replanejamento));
        var delegacao = Assert.Single(await a.DelegacoesAsync());
        Assert.Equal(despacho.Despachada, delegacao.CodigoDaSubordinada);
        Assert.NotNull((await a.ConferirAsync(delegacao.IdExecucaoSubordinada)).Execucao);

        // Snapshot adulterado por fora do domínio (outro participante): a subordinada não roda.
        await a.SqlAsync("SET session_replication_role = replica; UPDATE orquestracao.participantes_da_execucao " +
            $"SET hash_da_configuracao_do_agente = repeat('f', 64) WHERE id_execucao = '{a.IdExecucao}' AND papel = 'rev'");
        var recusada = await a.ConferirAsync(delegacao.IdExecucaoSubordinada);
        Assert.Null(recusada.Execucao);
        Assert.Contains("snapshot íntegro", recusada.Recusa);
    }

    private static void Responder(FakeSessionDriver driver, string mensagem)
    {
        driver.Emit(new MessageCompletedEvent("m", mensagem));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
    }

    private sealed class Ambiente : IAsyncDisposable
    {
        private const long Dono = 123;
        private readonly string raiz = Path.Combine(Path.GetTempPath(), "dante-coordenacao-" + Guid.NewGuid().ToString("N"));
        private Banco banco = null!;
        private ProcessadorDaFila? processador;
        private FerramentasDeCoordenacao coordenacao = null!;
        public ServiceProvider Provider { get; private set; } = null!;
        public IServiceScopeFactory Escopos => Provider.GetRequiredService<IServiceScopeFactory>();
        public EspelhoDoLedger Espelho { get; private set; } = null!;
        public SessionRegistry Sessoes { get; private set; } = null!;
        public FakeSessionDriverFactory Drivers { get; } = new();
        public string Repositorio => Path.Combine(raiz, "dante");
        public Guid IdExecucao { get; private set; }
        public Guid VersaoOriginal { get; private set; }

        public static async Task<Ambiente> CriarAsync()
        {
            var a = new Ambiente { banco = await FilaAsync() };
            Directory.CreateDirectory(a.Repositorio);
            await File.WriteAllTextAsync(Path.Combine(a.Repositorio, "README.md"), "# dante\n");
            a.Provider = FilaDuravelPostgreSqlTests.Provider(a.banco.ConnectionString, configurar: services =>
                services.Replace(ServiceDescriptor.Singleton<IAgentModelCatalog>(new CatalogoDeModelosFixo())));
            a.Espelho = new EspelhoDoLedger(a.Escopos, NullLogger<EspelhoDoLedger>.Instance);
            a.coordenacao = new FerramentasDeCoordenacao(a.Escopos, NullLogger<FerramentasDeCoordenacao>.Instance);
            a.Sessoes = new SessionRegistry(a.Drivers, NullLogger<SessionRegistry>.Instance,
                toolServers: new CompositorDeFerramentas([a.coordenacao]), ledger: a.Espelho);
            return a;
        }

        // Agentes de leitura sem concessão de coordenação (o plano e o snapshot autorizam, D2), o grupo "pesquisa" no
        // modo coordenador ativado e a coordenação enfileirada pelo caso de uso, no @dante deste teste.
        public async Task<string> CoordenacaoAsync()
        {
            await using var escopo = Provider.CreateAsyncScope();
            var s = escopo.ServiceProvider;
            var agentes = s.GetRequiredService<ICadastroDeAgentesAppService>();
            async Task<Guid> Agente(string handle, CapacidadeDoAgente capacidade)
            {
                await agentes.CriarAsync(new NovoAgenteDto(handle, new ConfiguracaoDoAgenteDto
                {
                    Driver = AgentKind.Codex, Capacidades = [capacidade], Repositorios = ["@dante"]
                }, Dono));
                return (await agentes.ResolverAsync(handle)).Agente!.Id;
            }
            var coord = await Agente("coordenador", CapacidadeDoAgente.Planejar);
            var pesq = await Agente("pesquisador", CapacidadeDoAgente.Pesquisar);
            var rev = await Agente("revisor", CapacidadeDoAgente.Revisar);
            VersaoOriginal = (await agentes.ResolverAsync("pesquisador")).Versao!.Id;
            var agora = DateTimeOffset.UtcNow;
            var grupo = new GrupoDeAgentes("pesquisa", null, Dono, agora);
            var versao = grupo.NovaVersao(new ConfiguracaoDoGrupo(ModoDeCoordenacao.Coordenador,
                [
                    new ParticipanteDoGrupo(coord, "coord", "Planeja e consolida.", false),
                    new ParticipanteDoGrupo(pesq, "pesq", "Pesquisa no código.", false),
                    new ParticipanteDoGrupo(rev, "rev", "Revisa conclusões.", false)
                ], [], new PoliticaDoGrupo(0, 1, ["@dante"]), "coord"), Dono, agora);
            var ativacao = grupo.Ativar(versao, versao.HashDaConfiguracao, Dono, agora);
            await s.GetRequiredService<IGrupoDeAgentesRepository>().AdicionarAsync(grupo);
            await s.GetRequiredService<IVersaoDoGrupoRepository>().AdicionarAsync(versao);
            await s.GetRequiredService<IAtivacaoDeVersaoDoGrupoRepository>().AdicionarAsync(ativacao);
            await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
            var resolucao = await s.GetRequiredService<ICadastroDeGruposAppService>().ResolverParaExecucaoAsync("pesquisa");
            Assert.True(resolucao.Resolvido, resolucao.Erro);
            var servico = s.GetRequiredService<ICoordenacoesAppService>();
            var contexto = JobExecutionContext.Repository("@dante", Repositorio, []);
            // Objetivo com aparência de credencial: nada é gravado.
            var numero = await s.GetRequiredService<IExecucaoRepository>().ReservarNumeroAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => servico.EnfileirarAsync(new NovaCoordenacaoDto(resolucao,
                "token ghp_0123456789abcdefghijklmnopqrstuvwxyz", contexto, numero, "telegram", Dono, $"c:{numero}")));
            var criada = await servico.EnfileirarAsync(new NovaCoordenacaoDto(resolucao, "Investigar o índice por estado",
                contexto, numero, "telegram", Dono, $"c:{numero}"));
            Assert.True(criada.Novo);
            // A mesma chave devolve a mesma execução.
            Assert.False((await servico.EnfileirarAsync(new NovaCoordenacaoDto(resolucao, "Outra", contexto, numero, "telegram",
                Dono, $"c:{numero}"))).Novo);
            IdExecucao = criada.IdExecucao;
            return criada.Codigo;
        }

        public async Task EditarAgenteAsync(string handle)
        {
            await using var escopo = Provider.CreateAsyncScope();
            await escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>().EditarAsync(handle,
                new AlteracaoDoAgenteDto { Instrucoes = "Versão nova, depois do snapshot." }, Dono);
        }

        public async Task DesativarAgenteAsync(string handle)
        {
            await using var escopo = Provider.CreateAsyncScope();
            await escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>().DesativarAsync(handle);
        }

        public void Iniciar()
        {
            IExecutorDeEtapa[] executores =
            [
                new ExecutorDeDelegacoesDaFila(Escopos, () => Sessoes, NullLogger<ExecutorDeDelegacoesDaFila>.Instance),
                new ExecutorDeCoordenacoesDaFila(Escopos, () => Sessoes, NullLogger<ExecutorDeCoordenacoesDaFila>.Instance)
            ];
            processador = new ProcessadorDaFila(Escopos, Processador(), Rapidas, new SinalDaFila(), executores,
                new MetricasDaFila(), NullLogger<ProcessadorDaFila>.Instance);
            processador.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public async Task PararAsync()
        {
            await processador!.StopAsync(CancellationToken.None);
            processador.Dispose();
            processador = null;
        }

        public async Task<FakeSessionDriver> DriverAsync(Func<string, bool> chamada, FakeSessionDriver? depoisDe = null)
        {
            FakeSessionDriver? achado = null;
            await Eventually(() =>
            {
                var lista = Drivers.Created.ToList();
                achado = lista.FirstOrDefault(d => d != depoisDe && d.Calls.Any(chamada) &&
                    (depoisDe is null || lista.IndexOf(d) > lista.IndexOf(depoisDe)));
                return Task.FromResult(achado is not null);
            }, TimeSpan.FromSeconds(30));
            return achado!;
        }

        public async Task<EstadoDaExecucao> EstadoAsync() =>
            (EstadoDaExecucao)await Escalar($"SELECT estado FROM orquestracao.execucoes WHERE id = '{IdExecucao}'");

        public Task<CoordenacaoDaExecucaoDto?> CoordenacaoDtoAsync() =>
            Servico(s => s.ObterDoProprietarioAsync(new ProprietarioDaExecucao("telegram", Dono), Codigo()));

        public Task<ConsolidacaoMecanicaDto?> ConsolidarAsync(string codigo) =>
            Servico(s => s.ConsolidarDoProprietarioAsync(new ProprietarioDaExecucao("telegram", Dono), codigo));

        public Task<ResultadoDaDecisaoDto> DecidirAsync(string codigo, TipoDeDecisaoHumana tipo, string? hash = null,
            string? texto = null, long dono = Dono) =>
            Servico(s => s.DecidirAsync(new ProprietarioDaExecucao("telegram", dono), codigo, new DecisaoHumanaDto(tipo, hash, texto)));

        public async Task<CancelamentoDeExecucaoDto> CancelarAsync(string codigo)
        {
            await using var escopo = Provider.CreateAsyncScope();
            return await escopo.ServiceProvider.GetRequiredService<IControleDeExecucoesAppService>()
                .SolicitarCancelamentoAsync(new ProprietarioDaExecucao("telegram", Dono), codigo, "não preciso mais");
        }

        public async Task<IReadOnlyList<DelegacaoDto>> DelegacoesAsync()
        {
            await using var escopo = Provider.CreateAsyncScope();
            return await escopo.ServiceProvider.GetRequiredService<IDelegacoesAppService>()
                .ListarDaRaizAsync(new ProprietarioDaExecucao("telegram", Dono), IdExecucao);
        }

        public Task<long> Escalar(string sql) => FilaDuravelPostgreSqlTests.Escalar(banco, sql);

        public Task SqlAsync(string sql) => Executar(banco, sql);

        public Task<AvancoDaCoordenacaoDto> AvancarAsync(PosseDoTrabalho posse, Guid idEtapa) =>
            Servico(s => s.AvancarAsync(posse, idEtapa));

        public async Task<ConferenciaDaDelegacaoDto> ConferirAsync(Guid idSubordinada)
        {
            await using var escopo = Provider.CreateAsyncScope();
            return await escopo.ServiceProvider.GetRequiredService<IDelegacoesAppService>().ConferirAsync(idSubordinada);
        }

        public Task<string> EntregarAsync(FakeSessionDriver filha, string resultado) =>
            ChamarAsync(filha, "entregar_resultado", new { resultado = JsonNode.Parse(resultado) });

        private string Codigo() => $"E{(long)Escalar($"SELECT numero FROM orquestracao.execucoes WHERE id = '{IdExecucao}'").GetAwaiter().GetResult()}";

        private async Task<T> Servico<T>(Func<ICoordenacoesAppService, Task<T>> chamada)
        {
            await using var escopo = Provider.CreateAsyncScope();
            return await chamada(escopo.ServiceProvider.GetRequiredService<ICoordenacoesAppService>());
        }

        // Faz o que o processo MCP do dante_coordenacao da sessão faria: conecta uma vez no pipe dela, apresenta o
        // token e chama pela mesma conexão.
        private static readonly ConditionalWeakTable<FakeSessionDriver, NamedPipeClientStream> Canais = [];

        private static async Task<string> ChamarAsync(FakeSessionDriver driver, string nome, object argumentos)
        {
            var servidor = Assert.Single(driver.StartOptions!.ToolServers!, s => s.Name == ServidorMcpDeCoordenacao.Nome);
            if (!Canais.TryGetValue(driver, out var pipe))
            {
                pipe = new NamedPipeClientStream(".", servidor.Arguments[^1], PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(2_000);
                await EscreverAsync(pipe, new { token = servidor.Environment[ServidorMcpDeCoordenacao.VariavelDoToken] });
                Canais.Add(driver, pipe);
            }
            await EscreverAsync(pipe, new { nome, argumentos });
            var bytes = new List<byte>();
            var buffer = new byte[1];
            using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                if (await pipe.ReadAsync(buffer, prazo.Token) != 1) throw new IOException("canal fechado");
                if (buffer[0] == '\n') break;
                bytes.Add(buffer[0]);
            }
            return (string)JsonNode.Parse(Encoding.UTF8.GetString(bytes.ToArray()))!["content"]![0]!["text"]!;
        }

        private static async Task EscreverAsync(Stream pipe, object mensagem)
        {
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(mensagem) + "\n"));
            await pipe.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (processador is not null) await PararAsync();
            await Sessoes.DisposeAsync();
            coordenacao.Dispose();
            await Espelho.DisposeAsync();
            await Provider.DisposeAsync();
            await banco.DisposeAsync();
            if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        }
    }
}
