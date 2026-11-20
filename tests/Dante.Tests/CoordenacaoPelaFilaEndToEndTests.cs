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
using Npgsql;
using static Dante.Tests.FilaDuravelPostgreSqlTests;

namespace Dante.Tests;

// #310 (AD-75), PR 2, de ponta a ponta contra PostgreSQL descartável: o ProcessadorDaFila real com o
// ExecutorDeCoordenacoesDaFila e o ExecutorDeDelegacoesDaFila reais, sessões reservadas com drivers falsos, o ledger
// espelhado e o dante_coordenacao real (o teste faz o papel do processo MCP). Fluxo: coordenação criada → coordenador
// propõe pela ferramenta propor_plano → validação pura no servidor (uma correção) → gate do plano, com aviso ao dono →
// aprovação humana pelo hash → unidades despachadas uma por vez como delegações consulta.v1 → resultados pela ferramenta
// → síntese do coordenador pela ferramenta entregar_sintese, validada só na estrutura (PR 3) → consolidação mecânica e
// aviso do fim. Também: plano inválido duas vezes, correção e recusa no gate, unidade sem
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

        // O coordenador planeja numa sessão reservada da versão congelada: plan, sem o ambiente do Worker, com o
        // dante_coordenacao só no perfil planejamento (propor_plano), com os papéis do snapshot e o schema da entrada; a
        // sessão fica vinculada à etapa.
        var coordenador = await a.DriverAsync(c => c.StartsWith("turn:Planejamento da coordenação", StringComparison.Ordinal));
        var opcoes = coordenador.StartOptions!;
        Assert.Equal((AgentPermissionProfile.Plan, a.Repositorio), (opcoes.Profile, opcoes.WorkingDirectory));
        var ferramentas = Assert.Single(opcoes.ToolServers!);
        Assert.Equal(ServidorMcpDeCoordenacao.Nome, ferramentas.Name);
        Assert.Equal(["--perfil", "planejamento"], ferramentas.Arguments.SkipWhile(x => x != "--perfil").Take(2));
        Assert.Equal([ServidorMcpDeCoordenacao.ProporPlano], ferramentas.PreAuthorizedTools);
        Assert.DoesNotContain(opcoes.EnvironmentVariables ?? new Dictionary<string, string>(), v => v.Key == "DANTE_SEGREDO_DE_TESTE");
        var prompt = coordenador.TurnInputs[0].Text;
        Assert.Contains("- pesq: agente pesquisador v1; capacidades: Pesquisar", prompt);
        Assert.Contains("- rev: agente revisor v1", prompt);
        Assert.DoesNotContain("- coord:", prompt);
        Assert.Contains("no máximo 4 unidade(s)", prompt);
        Assert.Contains("\"pergunta\"", prompt);
        Assert.Contains("Investigar o índice por estado", prompt);
        Assert.Contains("propor_plano", prompt);

        // Inválido: as recusas voltam na resposta da ferramenta, na mesma sessão e no mesmo turno; nada de plano gravado.
        var recusado = JsonNode.Parse(await a.ProporAsync(coordenador, PlanoInvalido))!["erro"]!.GetValue<string>();
        Assert.Contains("RequerGateway (u1)", recusado);
        Assert.Contains("UnidadeDoCoordenador (u2)", recusado);
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.planos_da_coordenacao"));
        var aceito = JsonNode.Parse(await a.ProporAsync(coordenador, PlanoComTres))!;
        Assert.True((bool)aceito["registrado"]!);
        // Depois do plano registrado, nada mais é aceito nesta etapa.
        Assert.Contains("já terminou", JsonNode.Parse(await a.ProporAsync(coordenador, Plano))!["erro"]!.GetValue<string>());
        Responder(coordenador, "Plano proposto.");

        // Gate aberto com o hash calculado no servidor; nenhuma delegação antes da decisão. A pessoa é avisada com as
        // unidades, o hash e os comandos.
        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.AguardandoAprovacao);
        Assert.Single(coordenador.TurnInputs);
        var aguardando = (await a.CoordenacaoDtoAsync())!;
        var plano = aguardando.PlanoAtual!;
        Assert.Equal(plano.Hash[..12], aceito["hash"]!.GetValue<string>());
        Assert.Equal((1, EstadoDoPlano.AguardandoAprovacao, 3), (plano.Revisao, plano.Estado, plano.QuantidadeDeUnidades));
        Assert.Equal(EstadoDoPedidoDeAprovacao.Pendente, aguardando.PedidoPendente!.Estado);
        Assert.Equal(plano.Hash, aguardando.PedidoPendente.HashDoArtefato);
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.delegacoes"));
        var planejamento = aguardando.Etapas.Single(e => e.Passo == PassoDaCoordenacao.Planejamento);
        Assert.NotNull(planejamento.CodigoDaSessao);
        Assert.Equal(1L, await a.Escalar($"SELECT count(*) FROM orquestracao.eventos WHERE tipo = {(int)TipoDeEventoDeOrquestracao.PlanoRecusado}"));
        var avisoDoGate = await a.Api.ProximaAsync();
        Assert.Contains($"{codigo} (pesquisa v1) aguarda sua aprovação do plano (revisão 1, 3 unidade(s), hash {plano.Hash[..12]})",
            avisoDoGate);
        Assert.Contains($"/execucao aprovar {codigo} {plano.Hash[..12]}", avisoDoGate);

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

        // Sem nada a despachar e com respostas, a execução não termina antes da síntese do coordenador: a mesma versão
        // congelada, em plan, com o dante_coordenacao só no perfil síntese e a trilha como dado não confiável.
        var sintetizador = await a.DriverAsync(c => c.StartsWith("turn:Síntese da coordenação", StringComparison.Ordinal));
        Assert.Equal(EstadoDaExecucao.EmExecucao, await a.EstadoAsync());
        var ferramentasDaSintese = Assert.Single(sintetizador.StartOptions!.ToolServers!);
        Assert.Equal(["--perfil", "sintese"], ferramentasDaSintese.Arguments.SkipWhile(x => x != "--perfil").Take(2));
        Assert.Equal(AgentPermissionProfile.Plan, sintetizador.StartOptions.Profile);
        var antes = (await a.ConsolidarAsync(codigo))!;
        var hashes = antes.Unidades.ToDictionary(u => u.Unidade, u => u.HashDoResultado!);
        var promptDaSintese = sintetizador.TurnInputs[0].Text;
        Assert.Contains("<<<DADO_NAO_CONFIAVEL", promptDaSintese);
        Assert.Contains($"unidade indice, papel pesq (pesquisador v1): respondida; hash {hashes["indice"]}", promptDaSintese);

        // Cobertura incompleta e conclusão com hash que não é o do resultado: recusada, uma correção.
        var incompleta = JsonNode.Parse(await a.SintetizarAsync(sintetizador, new
        {
            resumo = "Há índice.",
            conclusoes = new[] { new { texto = "Há ix_estado.", citacoes = new[] { new { revisao = 1, unidade = "indice", hash = new string('0', 12) } } } }
        }))!["erro"]!.GetValue<string>();
        Assert.Contains("HashDivergente", incompleta);
        Assert.Contains("CoberturaIncompleta (r1/consulta)", incompleta);
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.sinteses_da_coordenacao"));
        // Válida: a migração e o índice concordam; a consulta diverge do índice e a divergência fica explícita.
        var registrada = JsonNode.Parse(await a.SintetizarAsync(sintetizador, new
        {
            resumo = "O índice existe; há desacordo sobre a cobertura da consulta.",
            conclusoes = new[]
            {
                new { texto = "Existe ix_estado.", citacoes = new object[]
                {
                    new { revisao = 1, unidade = "indice", hash = hashes["indice"][..12] },
                    new { revisao = 1, unidade = "migracao", hash = hashes["migracao"] }
                } }
            },
            divergencias = new[]
            {
                new { texto = "Cobertura da consulta.", citacoes = new object[]
                {
                    new { revisao = 1, unidade = "consulta", hash = hashes["consulta"][..16] },
                    new { revisao = 1, unidade = "indice", hash = hashes["indice"][..12] }
                } }
            }
        }))!;
        Assert.True((bool)registrada["registrado"]!);
        Responder(sintetizador, "Síntese entregue.");

        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.Concluida);
        Assert.Equal(1L, await a.Escalar($"SELECT count(*) FROM orquestracao.eventos WHERE tipo = {(int)TipoDeEventoDeOrquestracao.SinteseRecusada}"));
        var resultado = (await a.ResultadoAsync(codigo))!;
        var sintese = resultado.Sintese!;
        Assert.Equal(sintese.Hash[..12], registrada["hash"]!.GetValue<string>());
        Assert.Equal(["indice", "migracao"], sintese.Conclusoes.Single().Citacoes.Select(c => c.Unidade));
        // O hash citado é gravado completo, o da trilha; a divergência continua divergência.
        Assert.All(sintese.Conclusoes.Single().Citacoes, c => Assert.Equal(hashes[c.Unidade], c.Hash));
        Assert.Equal(["consulta", "indice"], sintese.Divergencias.Single().Citacoes.Select(c => c.Unidade));
        Assert.Empty(sintese.Inconclusivos);
        Assert.NotNull(sintese.CodigoDaSessao);
        Assert.Equal(EstadoDaEtapa.Concluida, resultado.EstadoDaSintese);
        // A síntese não substitui a trilha: as duas existem lado a lado.
        Assert.Equal(3, resultado.Trilha.Respondidas);
        var avisoDoFim = await a.Api.ProximaAsync();
        Assert.Contains($"Coordenação {codigo} (pesquisa v1) concluída. Unidades: 3 respondida(s)", avisoDoFim);
        Assert.Contains("1 conclusão(ões), 1 divergência(s), 0 inconclusivo(s)", avisoDoFim);
        Assert.Contains($"/execucao consolidacao {codigo}", avisoDoFim);
        // A síntese é imutável no banco.
        await Assert.ThrowsAnyAsync<Exception>(() => a.SqlAsync(
            "UPDATE orquestracao.sinteses_da_coordenacao SET registrada_em = registrada_em + interval '1 second'"));

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
        Assert.Contains("RequerGateway", JsonNode.Parse(await a.ProporAsync(coordenador, PlanoInvalido))!["erro"]!.GetValue<string>());
        // Terminar o turno sem plano pela ferramenta também conta como recusa: a segunda encerra.
        Responder(coordenador, "Não sei planejar isso.");
        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.Falhou);

        Assert.Single(coordenador.TurnInputs);
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
        await a.ProporEEncerrarAsync(primeiro, Plano);
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
        await a.ProporEEncerrarAsync(segundo, Plano.Replace("Quais índices existem?", "Quais índices, inclusive parciais?"));
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
        await a.ProporEEncerrarAsync(coordenador, Plano);
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
        Assert.Contains($"- unidade \"indice\", papel \"pesq\": Falhou; motivo: \"{limite}\"", prompt);
        // Uma vaga do orçamento foi consumida pela delegação que falhou.
        Assert.Contains("no máximo 3 unidade(s)", prompt);
        await a.ProporEEncerrarAsync(replanejar, Plano);
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
        await a.ProporEEncerrarAsync(coordenador, Plano);
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
        await a.ProporEEncerrarAsync(coordenador, Plano);
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
        await a.SintetizarTudoAsync(codigo);
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
        await a.ProporEEncerrarAsync(coordenador, Plano);
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
        await a.ProporEEncerrarAsync(coordenador, Plano);
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

    // PR 3 (D7): a síntese é validada só na estrutura. Uma síntese que inventa consenso (cita uma unidade e deixa a outra
    // de fora) é recusada; o turno que termina sem corrigir conta como a segunda recusa: a etapa de síntese falha e a
    // execução termina pelo desfecho das unidades (concluída), só com a trilha. O banco só aceita síntese da etapa de
    // síntese em curso do coordenador congelado, com a sessão vinculada e o hash do conteúdo — nunca depois da falha — e
    // nunca a altera nem remove.
    [PostgreSqlFact]
    public async Task SinteseInvalidaDuasVezesEncerraPelasUnidadesSoComATrilha()
    {
        await using var a = await Ambiente.CriarAsync();
        var codigo = await a.CoordenacaoAsync();
        a.Iniciar();
        var coordenador = await a.DriverAsync(c => c.StartsWith("turn:Planejamento da coordenação", StringComparison.Ordinal));
        await a.ProporEEncerrarAsync(coordenador, Plano);
        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.AguardandoAprovacao);
        await a.DecidirAsync(codigo, TipoDeDecisaoHumana.Aprovar, (await a.CoordenacaoDtoAsync())!.PlanoAtual!.Hash[..12]);
        var indice = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal));
        Assert.True((bool)JsonNode.Parse(await a.EntregarAsync(indice, Resposta))!["registrado"]!);
        Responder(indice, "Entregue.");
        var consulta = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal), depoisDe: indice);
        Assert.True((bool)JsonNode.Parse(await a.EntregarAsync(consulta, Resposta.Replace("Há ix_estado.", "Não cobre.")))!["registrado"]!);
        Responder(consulta, "Entregue.");

        var sintetizador = await a.DriverAsync(c => c.StartsWith("turn:Síntese da coordenação", StringComparison.Ordinal));
        var hash = (await a.ConsolidarAsync(codigo))!.Unidades.Single(u => u.Unidade == "indice").HashDoResultado!;
        var recusa = JsonNode.Parse(await a.SintetizarAsync(sintetizador, new
        {
            resumo = "Todos concordam.",
            conclusoes = new[] { new { texto = "Consenso.", citacoes = new[] { new { revisao = 1, unidade = "indice", hash } } } }
        }))!["erro"]!.GetValue<string>();
        Assert.Contains("CoberturaIncompleta (r1/consulta)", recusa);
        Responder(sintetizador, "Não vou corrigir.");

        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.Concluida);
        Assert.Single(sintetizador.TurnInputs);
        Assert.Equal(2L, await a.Escalar($"SELECT count(*) FROM orquestracao.eventos WHERE tipo = {(int)TipoDeEventoDeOrquestracao.SinteseRecusada}"));
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.sinteses_da_coordenacao"));
        var resultado = (await a.ResultadoAsync(codigo))!;
        Assert.Null(resultado.Sintese);
        Assert.Equal(EstadoDaEtapa.Falhou, resultado.EstadoDaSintese);
        Assert.Contains("Síntese inválida depois da correção permitida", resultado.MotivoDaSintese);
        Assert.Equal((2, 0), (resultado.Trilha.Respondidas, resultado.Trilha.SemResposta));
        Assert.Contains("aguarda sua aprovação do plano", await a.Api.ProximaAsync());
        var fim = await a.Api.ProximaAsync();
        Assert.Contains($"Coordenação {codigo} (pesquisa v1) concluída", fim);
        Assert.Contains("Sem síntese do coordenador (Síntese inválida depois da correção permitida", fim);

        // Por fora do domínio: pela etapa de planejamento (sem papel), com o hash que não é o do conteúdo, ou pela etapa
        // de síntese depois que ela falhou e a execução terminou, recusada.
        string Inserir(string filtro, string hashDoConteudo) => $"""
            INSERT INTO orquestracao.sinteses_da_coordenacao
                (id, id_execucao, id_etapa, id_sessao_de_agente, conteudo, hash, registrada_em)
            SELECT gen_random_uuid(), e.id_execucao, e.id, e.id_sessao_de_agente, 'x', {hashDoConteudo}, now()
              FROM orquestracao.etapas_da_execucao e
             WHERE e.id_execucao = '{a.IdExecucao}' AND e.tipo = {(int)TipoDeEtapa.TurnoDeAgente} AND e.agente IS NOT NULL AND {filtro}
             LIMIT 1
            """;
        const string doConteudo = "encode(sha256(convert_to('x', 'UTF8')), 'hex')";
        var planejamento = await Assert.ThrowsAnyAsync<Exception>(() => a.SqlAsync(Inserir("e.papel IS NULL", doConteudo)));
        Assert.Contains("síntese só pela etapa de síntese", planejamento.Message);
        var adulterada = await Assert.ThrowsAnyAsync<Exception>(() => a.SqlAsync(Inserir("e.papel IS NOT NULL", $"'{new string('0', 64)}'")));
        Assert.Contains("o hash da síntese não é o do conteúdo", adulterada.Message);
        var depoisDaFalha = await Assert.ThrowsAnyAsync<Exception>(() => a.SqlAsync(Inserir("e.papel IS NOT NULL", doConteudo)));
        Assert.Contains("síntese só com a execução aberta e a etapa de síntese em execução", depoisDaFalha.Message);
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.sinteses_da_coordenacao"));
        // Uma linha que só existe com os triggers desligados (restauração adulterada): a leitura confere a forma canônica
        // contra a trilha e não exibe o que não confere. Com os triggers ligados, nada muda nem sai.
        await a.SqlAsync("SET session_replication_role = replica; " + Inserir("e.papel IS NOT NULL", doConteudo) +
            "; SET session_replication_role = origin");
        var naoConfere = (await a.ResultadoAsync(codigo))!;
        Assert.Null(naoConfere.Sintese);
        Assert.Contains("não confere com a trilha", naoConfere.MotivoDaSintese);
        Assert.Contains("imutável", (await Assert.ThrowsAnyAsync<Exception>(() =>
            a.SqlAsync("UPDATE orquestracao.sinteses_da_coordenacao SET conteudo = 'y'"))).Message);
        Assert.Contains("imutável", (await Assert.ThrowsAnyAsync<Exception>(() =>
            a.SqlAsync("DELETE FROM orquestracao.sinteses_da_coordenacao"))).Message);
    }

    // Review da PR 3: por SQL direto, o banco só aceita a síntese com a execução aberta, a etapa de síntese em execução e
    // uma sessão desta execução e do coordenador congelado — não basta o id coincidir com o vínculo da etapa. Cada
    // tentativa adultera o vínculo com os triggers desligados e insere com eles ligados, na mesma transação implícita,
    // que a recusa desfaz.
    [PostgreSqlFact]
    public async Task SinteseSoPelaEtapaEmCursoComSessaoDestaExecucaoEDoCoordenadorCongelado()
    {
        await using var a = await Ambiente.CriarAsync();
        var codigo = await a.CoordenacaoAsync();
        a.Iniciar();
        var coordenador = await a.DriverAsync(c => c.StartsWith("turn:Planejamento da coordenação", StringComparison.Ordinal));
        await a.ProporEEncerrarAsync(coordenador, Plano);
        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.AguardandoAprovacao);
        await a.DecidirAsync(codigo, TipoDeDecisaoHumana.Aprovar, (await a.CoordenacaoDtoAsync())!.PlanoAtual!.Hash[..12]);
        var indice = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal));
        Assert.True((bool)JsonNode.Parse(await a.EntregarAsync(indice, Resposta))!["registrado"]!);
        Responder(indice, "Entregue.");
        var consulta = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal), depoisDe: indice);
        Assert.True((bool)JsonNode.Parse(await a.EntregarAsync(consulta, Resposta.Replace("Há ix_estado.", "Cobre.")))!["registrado"]!);
        Responder(consulta, "Entregue.");
        var sintetizador = await a.DriverAsync(c => c.StartsWith("turn:Síntese da coordenação", StringComparison.Ordinal));
        Assert.Equal(EstadoDaExecucao.EmExecucao, await a.EstadoAsync());
        // Uma versão nova do coordenador, publicada depois do snapshot.
        await a.EditarAgenteAsync("coordenador");

        var id = a.IdExecucao;
        var daSintese = $"id_execucao = '{id}' AND tipo = {(int)TipoDeEtapa.TurnoDeAgente} AND papel IS NOT NULL AND id_plano IS NULL";
        Assert.Equal((long)EstadoDaEtapa.EmExecucao, await a.Escalar($"SELECT estado FROM orquestracao.etapas_da_execucao WHERE {daSintese}"));
        var sessaoDaEtapa = await a.TextoAsync($"SELECT id_sessao_de_agente::text FROM orquestracao.etapas_da_execucao WHERE {daSintese}");
        const string doConteudo = "encode(sha256(convert_to('x', 'UTF8')), 'hex')";
        var inserir = $"""
            INSERT INTO orquestracao.sinteses_da_coordenacao
                (id, id_execucao, id_etapa, id_sessao_de_agente, conteudo, hash, registrada_em)
            SELECT gen_random_uuid(), e.id_execucao, e.id, e.id_sessao_de_agente, 'x', {doConteudo}, now()
              FROM orquestracao.etapas_da_execucao e WHERE {daSintese}
            """;
        string VincularEInserir(string sessao) =>
            "SET session_replication_role = replica; " +
            $"UPDATE orquestracao.etapas_da_execucao SET id_sessao_de_agente = {sessao} WHERE {daSintese}; " +
            "SET session_replication_role = origin; " + inserir;
        // Cópias da sessão do coordenador, cada uma com uma diferença (o resto, inclusive canal e dono, igual).
        string[] colunas =
        [
            "id", "agente", "boot_do_worker", "canal", "contexto", "criada_em", "encerrada_em", "esforco", "estado",
            "handle_do_agente", "hash_da_configuracao_do_agente", "id_do_dono", "id_execucao", "id_upstream",
            "id_versao_do_agente", "iniciada_em", "maquina_do_worker", "modelo", "modelo_informado", "motivo_do_fim",
            "numero", "perfil", "unidade_de_contencao"
        ];
        async Task<string> CopiarSessaoAsync(Dictionary<string, string> trocas)
        {
            var nova = Guid.NewGuid();
            trocas["id"] = $"'{nova}'";
            trocas["numero"] = "(SELECT max(numero) + 1 FROM orquestracao.sessoes_de_agente)";
            await a.SqlAsync($"""
                INSERT INTO orquestracao.sessoes_de_agente ({string.Join(", ", colunas)})
                SELECT {string.Join(", ", colunas.Select(c => trocas.GetValueOrDefault(c, c)))}
                  FROM orquestracao.sessoes_de_agente WHERE id = '{sessaoDaEtapa}'
                """);
            return $"'{nova}'";
        }
        const string daSubordinada = "(SELECT id FROM orquestracao.execucoes WHERE id_execucao_pai = '{0}' ORDER BY numero LIMIT 1)";
        string Participante(string coluna, string papel) =>
            $"(SELECT {coluna} FROM orquestracao.participantes_da_execucao WHERE id_execucao = '{id}' AND papel = '{papel}')";
        const string versaoNova = """
            (SELECT v.{0} FROM orquestracao.versoes_de_agente v JOIN orquestracao.agentes g ON g.id = v.id_agente
              WHERE g.handle = 'coordenador' ORDER BY v.numero DESC LIMIT 1)
            """;
        var marcadaNaSubordinada = await CopiarSessaoAsync(new() { ["id_execucao"] = string.Format(daSubordinada, id) });
        var deOutroDono = await CopiarSessaoAsync(new() { ["id_do_dono"] = "999" });
        var deOutroAgente = await CopiarSessaoAsync(new()
        {
            ["handle_do_agente"] = Participante("handle_do_agente", "pesq"),
            ["id_versao_do_agente"] = Participante("id_versao_do_agente", "pesq"),
            ["hash_da_configuracao_do_agente"] = Participante("hash_da_configuracao_do_agente", "pesq")
        });
        var deOutraVersao = await CopiarSessaoAsync(new()
        {
            ["id_versao_do_agente"] = string.Format(versaoNova, "id"),
            ["hash_da_configuracao_do_agente"] = string.Format(versaoNova, "hash_da_configuracao")
        });
        Assert.NotEqual(0L, await a.Escalar($"SELECT count(*) FROM orquestracao.sessoes_de_agente WHERE id = {deOutraVersao} " +
            $"AND id_versao_do_agente <> {Participante("id_versao_do_agente", "coord")}"));
        // A sessão real de uma etapa da subordinada (a unidade "indice"): pertence a outra execução.
        var daOutraExecucao = $"""
            (SELECT et.id_sessao_de_agente FROM orquestracao.etapas_da_execucao et
               JOIN orquestracao.execucoes x ON x.id = et.id_execucao
              WHERE x.id_execucao_pai = '{id}' AND et.id_sessao_de_agente IS NOT NULL ORDER BY x.numero LIMIT 1)
            """;

        foreach (var (sessao, recusa) in new[]
                 {
                     (daOutraExecucao, "a sessão da síntese não pertence a esta execução"),
                     (marcadaNaSubordinada, "a sessão da síntese não pertence a esta execução"),
                     (deOutroDono, "a sessão da síntese não pertence a esta execução"),
                     (deOutroAgente, "a sessão da síntese não é a do coordenador congelado"),
                     (deOutraVersao, "a sessão da síntese não é a do coordenador congelado")
                 })
        {
            var erro = await Assert.ThrowsAnyAsync<Exception>(() => a.SqlAsync(VincularEInserir(sessao)));
            Assert.Contains(recusa, erro.Message);
            // A recusa desfaz a adulteração do vínculo junto com o INSERT.
            Assert.Equal(sessaoDaEtapa, await a.TextoAsync(
                $"SELECT id_sessao_de_agente::text FROM orquestracao.etapas_da_execucao WHERE {daSintese}"));
        }
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.sinteses_da_coordenacao"));

        // Pela etapa em curso, com a sessão vinculada dela, o banco aceita (aqui numa transação desfeita, para a síntese
        // seguir pelo caso de uso).
        Assert.Equal(1L, await a.InserirEDesfazerAsync(inserir, "SELECT count(*) FROM orquestracao.sinteses_da_coordenacao"));
        Assert.Equal(0L, await a.Escalar("SELECT count(*) FROM orquestracao.sinteses_da_coordenacao"));

        // Pelo caso de uso, a síntese é gravada com a etapa ainda em execução e só depois a etapa conclui.
        var hashes = (await a.ConsolidarAsync(codigo))!.Unidades.ToDictionary(u => u.Unidade, u => u.HashDoResultado!);
        var registrada = JsonNode.Parse(await a.SintetizarAsync(sintetizador, new
        {
            resumo = "O índice existe e cobre a consulta.",
            conclusoes = new[]
            {
                new { texto = "Existe ix_estado e ele cobre a consulta.", citacoes = new object[]
                {
                    new { revisao = 1, unidade = "indice", hash = hashes["indice"] },
                    new { revisao = 1, unidade = "consulta", hash = hashes["consulta"] }
                } }
            }
        }))!;
        Assert.True((bool)registrada["registrado"]!, registrada.ToJsonString());
        Responder(sintetizador, "Síntese entregue.");
        await Eventually(async () => await a.EstadoAsync() == EstadoDaExecucao.Concluida);
        Assert.Equal(1L, await a.Escalar($"SELECT count(*) FROM orquestracao.sinteses_da_coordenacao WHERE id_sessao_de_agente = '{sessaoDaEtapa}'"));

        // Depois da conclusão, nem pela etapa de síntese com a sessão dela.
        var depois = await Assert.ThrowsAnyAsync<Exception>(() => a.SqlAsync(inserir));
        Assert.Contains("síntese só com a execução aberta e a etapa de síntese em execução", depois.Message);
        Assert.Equal(1L, await a.Escalar("SELECT count(*) FROM orquestracao.sinteses_da_coordenacao"));
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
        public TarefaPelaFilaEndToEndTests.BotDeTeste Api { get; } = new();
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
                new ExecutorDeCoordenacoesDaFila(Escopos, () => Sessoes, NullLogger<ExecutorDeCoordenacoesDaFila>.Instance, Api)
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

        public async Task<string> TextoAsync(string sql)
        {
            await using var conexao = new NpgsqlConnection(banco.ConnectionString);
            await conexao.OpenAsync();
            await using var comando = new NpgsqlCommand(sql, conexao);
            return (string)(await comando.ExecuteScalarAsync())!;
        }

        // Executa o comando numa transação, devolve a contagem vista dentro dela e desfaz tudo.
        public async Task<long> InserirEDesfazerAsync(string sql, string contagem)
        {
            await using var conexao = new NpgsqlConnection(banco.ConnectionString);
            await conexao.OpenAsync();
            await using var transacao = await conexao.BeginTransactionAsync();
            await using (var comando = new NpgsqlCommand(sql, conexao, transacao))
                await comando.ExecuteNonQueryAsync();
            await using var consulta = new NpgsqlCommand(contagem, conexao, transacao);
            var vista = (long)(await consulta.ExecuteScalarAsync())!;
            await transacao.RollbackAsync();
            return vista;
        }

        public Task<AvancoDaCoordenacaoDto> AvancarAsync(PosseDoTrabalho posse, Guid idEtapa) =>
            Servico(s => s.AvancarAsync(posse, idEtapa));

        public async Task<ConferenciaDaDelegacaoDto> ConferirAsync(Guid idSubordinada)
        {
            await using var escopo = Provider.CreateAsyncScope();
            return await escopo.ServiceProvider.GetRequiredService<IDelegacoesAppService>().ConferirAsync(idSubordinada);
        }

        public Task<string> EntregarAsync(FakeSessionDriver filha, string resultado) =>
            ChamarAsync(filha, "entregar_resultado", new { resultado = JsonNode.Parse(resultado) });

        public Task<string> ProporAsync(FakeSessionDriver coordenador, string plano) =>
            ChamarAsync(coordenador, ServidorMcpDeCoordenacao.ProporPlano, new { plano = JsonNode.Parse(plano) });

        // O coordenador propõe pela ferramenta (aceito) e termina o turno.
        public async Task ProporEEncerrarAsync(FakeSessionDriver coordenador, string plano)
        {
            Assert.True((bool)JsonNode.Parse(await ProporAsync(coordenador, plano))!["registrado"]!);
            Responder(coordenador, "Plano proposto.");
        }

        public Task<string> SintetizarAsync(FakeSessionDriver coordenador, object sintese) =>
            ChamarAsync(coordenador, ServidorMcpDeCoordenacao.EntregarSintese, new { sintese });

        // Síntese mínima válida: as respondidas numa conclusão (com o hash), as demais num inconclusivo.
        public async Task SintetizarTudoAsync(string codigo, FakeSessionDriver? depoisDe = null)
        {
            var sintetizador = await DriverAsync(c => c.StartsWith("turn:Síntese da coordenação", StringComparison.Ordinal),
                depoisDe);
            var trilha = (await ConsolidarAsync(codigo))!;
            var respondidas = trilha.Unidades.Where(u => u.Situacao == SituacaoDaUnidade.Respondida)
                .Select(u => (object)new { revisao = u.Revisao, unidade = u.Unidade, hash = u.HashDoResultado }).ToArray();
            var outras = trilha.Unidades.Where(u => u.Situacao != SituacaoDaUnidade.Respondida)
                .Select(u => (object)new { revisao = u.Revisao, unidade = u.Unidade }).ToArray();
            var sintese = new Dictionary<string, object>
            {
                ["resumo"] = "Resumo.",
                ["conclusoes"] = new[] { new { texto = "O que as unidades responderam.", citacoes = respondidas } }
            };
            if (outras.Length > 0) sintese["inconclusivos"] = new[] { new { texto = "Sem resposta.", citacoes = outras } };
            Assert.True((bool)JsonNode.Parse(await SintetizarAsync(sintetizador, sintese))!["registrado"]!);
            Responder(sintetizador, "Síntese entregue.");
        }

        public Task<ResultadoDaCoordenacaoDto?> ResultadoAsync(string codigo) =>
            Servico(s => s.ObterResultadoDoProprietarioAsync(new ProprietarioDaExecucao("telegram", Dono), codigo));

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
