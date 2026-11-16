using System.Text;
using System.Text.Json;
using Dante.Application.BuscaDoBrain;
using Dante.Application.ConstrucaoDeContexto;
using Dante.Application.MetricasDoBrain;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.DocumentosFonte;
using Dante.Infrastructure.Agentes;
using Dante.Infrastructure.MetricasDoBrain;
using Dante.Worker.Brain;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Dante.Tests;

// CLI real, somente Brain descartável. Resultados negativos de qualidade são dados, não asserts invertidos.
public sealed partial class Issue147BrainIndependenteTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Curto (2 turnos de A), médio (6) e longo (12) sobre os casos reais da fixture pré-registrada issue147-cenarios@2
    // (CenariosDaRetomada147/cenarios-fundamento.json): Incidente e Solucao capturados pelo campo fundamento, de modo que a
    // omissão condicional da fonte (#289/#292) é exercitada. O @1 (cenarios.json) segue só como pré-registro das retomadas
    // de 15/11. Cada execução tem banco, histórico e estado próprios.
    [EnsaioIndependente]
    [InlineData("curto", 1)]
    [InlineData("curto", 2)]
    [InlineData("medio", 1)]
    [InlineData("medio", 2)]
    [InlineData("longo", 1)]
    [InlineData("longo", 2)]
    public async Task SessaoIndependenteRetomaCasoRealSomentePeloBrain(string duracao, int repeticao)
    {
        var admin = new Npgsql.NpgsqlConnectionStringBuilder(Banco.Administrativa);
        // Recusa erro de configuração antes de criar qualquer banco: laboratório conhecido, nunca o canônico.
        Assert.Equal("127.0.0.1", admin.Host); Assert.Equal(55433, admin.Port); Assert.Equal("postgres", admin.Database);
        var roteiro = RoteiroDaRetomada147.Carregar(RoteiroDaRetomada147.CaminhoComFundamento);
        var casos = roteiro.Casos(duracao); var consultaB = roteiro.ConsultaB(duracao);
        // Evidências persistentes fora de /tmp, recusadas antes de criar banco ou iniciar a CLI.
        var execucao = ExecucaoDeMedicao147.Iniciar(RaizDeEvidencias147.Validar(RaizDeEvidencias147.Padrao()), RoteiroDaRetomada147.Cenario(duracao), repeticao);
        // O roteiro executado fica na própria execução, com hash no manifesto.
        execucao.GravarTexto("roteiro.json", "roteiro-pre-registrado", File.ReadAllText(RoteiroDaRetomada147.CaminhoComFundamento, new UTF8Encoding(false, true)));
        var criteriosB = CriteriosDaRespostaB147.Carregar();
        execucao.GravarTexto(CriteriosDaRespostaB147.NomeDoArquivo, "criterios-pre-registrados", File.ReadAllText(CriteriosDaRespostaB147.Caminho, new UTF8Encoding(false, true)));
        await using var banco = await Banco.CriarAsync();
        var privadoA = Directory.CreateTempSubdirectory("dante147-a-").FullName;
        var privadoB = Directory.CreateTempSubdirectory("dante147-b-").FullName;
        // Métricas do produto escritas direto na execução; declaradas com hash no fechamento.
        var metricas = execucao.Reservar("metricas.jsonl");
        var registro = new RegistroDeMetricasEmArquivo(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DANTE_BRAIN_METRICS_FILE"] = metricas }).Build());
        using var app = BrainMcpTests.CriarApp(banco.ConnectionString, s => s.AddSingleton<IRegistroDeMetricasDoBrain>(registro));
        var brain = app.GetRequiredService<TelegramBrain>(); await BrainMcpTests.SelecionarAsync(brain);
        using var ferramentas = BrainMcpTests.Provedor(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12 + 6 * (casos.Count - 1)));
        var ct = timeout.Token;
        var historico = new List<string>(); var chamadasA = new List<string>(); var chamadasB = new List<string>();
        var rastroB = new RastroDeFerramentas147();
        var usoA = new CapturaDeUsoClaude147(); var usoB = new CapturaDeUsoClaude147(); var turnos = new List<TurnoMedido147>();
        var launcherA = new LancadorSomenteBrain(privadoA, usoA); var launcherB = new LancadorSomenteBrain(privadoB, usoB);
        // Casos reais (PRs integradas). O marcador é aleatório e só verifica transferência; não inventa fato do produto.
        var marcador = "verificacao147" + Guid.NewGuid().ToString("N");
        var markdown = roteiro.Markdown(casos[0], marcador);
        string respostaA = "", respostaB = "", respostaSnapshot = "";
        object? resultadoFinal = null; Exception? falha = null;
        try
        {
            var mensagemA = BrainMcpTests.Mensagem(roteiro.PedidoDoCaso(casos[0], marcador));
            var servidoresA = await ferramentas.ForSessionAsync("147A", new(42, AgentKind.Claude, Dante.Application.Contextos.JobExecutionContext.General(privadoA), BrainConversation: mensagemA.ParaFerramentas()), ct);
            Assert.Single(servidoresA);
            await using (var a = new ClaudeSessionDriver(launcherA))
            {
                await a.StartAsync(new(privadoA, IsGeneral: true, Profile: AgentPermissionProfile.Manual, ToolServers: servidoresA), ct);
                await using var eventos = a.ReadEventsAsync(ct).GetAsyncEnumerator();
                for (var i = 0; i < casos.Count; i++)
                {
                    var caso = casos[i]; var (nPedido, nSnapshot) = (2 * i + 1, 2 * i + 2);
                    // Ids de mensagem do caso 1 iguais aos das retomadas medidas; os demais casos em faixas próprias.
                    var desloc = 100 * i;
                    var pedido = roteiro.PedidoDoCaso(caso, marcador); var md = roteiro.Markdown(caso, marcador);
                    if (i > 0) ferramentas.BeginTurn("147A", new AgentInput(pedido, []) { BrainConversation = BrainMcpTests.Mensagem(pedido, 123 + desloc).ParaFerramentas() });
                    execucao.GravarTexto($"entrada-a{nPedido}.txt", "entrada-enviada", pedido); await a.StartTurnAsync(pedido, ct);
                    var turnoPedido = await RespostaAsync(a, eventos, chamadasA, true, ct); respostaA = turnoPedido.Texto;
                    turnos.Add(new($"A{nPedido}", usoA.Proximo(), turnoPedido.Uso)); execucao.GravarTexto($"resposta-a{nPedido}.txt", "resposta-visivel", respostaA);
                    historico.Add(pedido); historico.Add(respostaA);
                    var pendentes = i == 0 ? 3 : 2;
                    await using (var db = banco.Contexto())
                    {
                        var fontes = await db.Set<DocumentoFonte>().ToListAsync(ct); Assert.Equal(i + 1, fontes.Count);
                        var fonte = Assert.Single(fontes, x => x.Conteudo == md);
                        Assert.Equal("markdown", fonte.Formato); Assert.Equal(AutoriaDaFonte.Agente, fonte.Autoria);
                        Assert.Contains(brain.Instancia + ":147A", fonte.Proveniencia);
                        var candidatos = await db.CandidatosDeConhecimento.ToListAsync(ct); Assert.Equal(3 + 2 * i, candidatos.Count);
                        // @2: o vínculo com a evidência é a proveniência por trecho resolvida pelo servidor, não a justificativa.
                        var doCaso = candidatos.Where(x => x.Natureza == NaturezaDoConteudo.ConclusaoDoAgente &&
                            Dante.Application.DocumentosFonte.ReferenciaDeTrecho.Ler(x.Proveniencia.ReferenciaDaFonte) is { Inicio: not null } t && t.IdDocumento == fonte.Id && t.Revisao == fonte.Revisao).ToArray();
                        Assert.Equal(2, doCaso.Length);
                        Assert.Equal(i == 0 ? 0 : 1 + 2 * i, await db.Conhecimentos.CountAsync(ct));
                    }
                    // Operador simulado, negócio real. Aprovação da CLI acima não consolida candidatos.
                    for (var n = 0; n < pendentes; n++)
                    {
                        Assert.Contains("Candidatos pendentes", await brain.AtenderAsync(BrainMcpTests.Mensagem("listar candidatos", 200 + desloc + n * 2), "listar candidatos"));
                        Assert.Contains("consolidado", await brain.AtenderAsync(BrainMcpTests.Mensagem("confirmar primeira", 201 + desloc + n * 2), "confirmar primeira"));
                    }
                    string refs; Guid decisao;
                    await using (var db = banco.Contexto())
                    {
                        var conhecimentos = await db.Conhecimentos.ToListAsync(ct); Assert.Equal(3 + 2 * i, conhecimentos.Count);
                        Assert.All(conhecimentos.Where(x => x.NaturezaDeOrigem == NaturezaDoConteudo.ConclusaoDoAgente), x => Assert.Equal(StatusDoConhecimento.Inferido, x.Status));
                        decisao = Assert.Single(conhecimentos, x => x.Tipo == TipoDeConhecimento.Decisao && x.Status == StatusDoConhecimento.Confirmado).Id;
                        var relacoes = await db.RelacoesDeConhecimento.ToListAsync(ct); Assert.Equal(i + 1, relacoes.Count);
                        Assert.All(relacoes, r => Assert.Equal(Dante.Domain.RelacoesDeConhecimento.TipoDeRelacao.ResolvidoPor, r.Tipo));
                        var fontes = await db.Set<DocumentoFonte>().ToListAsync(ct);
                        // Para um caso, idêntico ao roteiro de 15/11: conhecimentos e depois a fonte.
                        refs = string.Join(", ", conhecimentos.Select(x => $"conhecimento:{x.Id}@{x.Revisao}").Concat(fontes.Select(f => $"fonte:{f.Id}@{f.Revisao}")));
                    }
                    var salvar = roteiro.PedidoDoSnapshot(refs, decisao);
                    var mensagemSnapshot = BrainMcpTests.Mensagem(salvar, 210 + desloc);
                    ferramentas.BeginTurn("147A", new AgentInput(salvar, []) { BrainConversation = mensagemSnapshot.ParaFerramentas() });
                    execucao.GravarTexto($"entrada-a{nSnapshot}.txt", "entrada-enviada", salvar); await a.StartTurnAsync(salvar, ct);
                    var turnoSnapshot = await RespostaAsync(a, eventos, chamadasA, true, ct); respostaSnapshot = turnoSnapshot.Texto;
                    turnos.Add(new($"A{nSnapshot}", usoA.Proximo(), turnoSnapshot.Uso)); execucao.GravarTexto($"resposta-a{nSnapshot}.txt", "resposta-visivel", respostaSnapshot);
                    historico.Add(salvar); historico.Add(respostaSnapshot);
                }
                // Insumo da comparação: somente o histórico visível de A (pedidos e respostas), não o transcript integral.
                execucao.GravarTexto("historico-visivel.txt", "historico-visivel-nao-integral", string.Join(SeparadorDoHistorico, historico));
            }
            // Dispose de produção aguarda Completion. Confirma sucesso estrito antes de B existir.
            await launcherA.ConfirmarEncerramentoAsync(ct); ferramentas.EndSession("147A");
            Directory.Delete(privadoA, true); Assert.False(Directory.Exists(privadoA));
            Assert.Empty(Directory.EnumerateFileSystemEntries(privadoB));
            var estadoAntesDeB = await EstadoPersistidoAsync(banco, ct);
            var mensagemB = BrainMcpTests.Mensagem(consultaB, 300);
            var servidoresB = await ferramentas.ForSessionAsync("147B", new(42, AgentKind.Claude, Dante.Application.Contextos.JobExecutionContext.General(privadoB), BrainConversation: mensagemB.ParaFerramentas()), ct);
            Assert.Single(servidoresB);
            var contexto = await brain.PrepararAsync(mensagemB, "147B", consultaB, ct);
            Assert.NotNull(contexto); Assert.NotEmpty(contexto.Pacote.Itens);
            Assert.Contains(contexto.Pacote.Itens, x => x.Origem == "snapshot");
            Assert.Contains(contexto.Pacote.Itens, x => x.Origem == "fonte_bruta");
            GravarPacote(execucao, contexto.Pacote);
            Assert.True(PacoteInjetado147.Confere(contexto.Texto, contexto.Pacote.TextoParaInjecao), "Texto enviado não começa pelo pacote exportado.");
            await using (var b = new ClaudeSessionDriver(launcherB))
            {
                await b.StartAsync(new(privadoB, IsGeneral: true, Profile: AgentPermissionProfile.Manual, ToolServers: servidoresB), ct);
                await using var eventos = b.ReadEventsAsync(ct).GetAsyncEnumerator();
                // Pedido não contém a resposta. Contexto vem exclusivamente do construtor de produção sob teste.
                execucao.GravarTexto("entrada-b.txt", "entrada-enviada", contexto.Texto); await b.StartTurnAsync(contexto.Texto, ct);
                await brain.RegistrarInjecaoAsync(contexto, ct);
                var turnoB = await RespostaAsync(b, eventos, chamadasB, false, ct, rastroB); respostaB = turnoB.Texto;
                turnos.Add(new("B", usoB.Proximo(), turnoB.Uso)); execucao.GravarTexto("resposta-b.txt", "resposta-visivel", respostaB);
                // Uso reportado pela CLI vai ao registro do produto; antes era null.
                await brain.RegistrarTurnoAsync(contexto.Correlacao, "claude", ConstrutorDeContextoAppService.EstimarTokens(respostaB), AgentTurnOutcome.Completed, turnoB.Uso, ct);
            }
            await launcherB.ConfirmarEncerramentoAsync(ct); ferramentas.EndSession("147B");
            var estadoDepoisDeB = await EstadoPersistidoAsync(banco, ct);
            var registroOperacoes = app.GetRequiredService<RegistroDeOperacoesBrain>();
            registroOperacoes.RegistrarSessao("147MEDICAO", mensagemB);
            var escopo = (await brain.ResolverEscopoMcpAsync(mensagemB, ct))!;
            var operacoes = app.GetRequiredService<OperacoesMcpDoBrain>();
            var consultas = new[] { "Completion", "processos descendentes", "encerramento dos subprocessos", "faxina dos subordinados", "busca lexical" };
            var recuperacoes = new List<object>();
            object registrosPersistidos;
            Guid incidente; Guid solucao;
            // Critérios do estado (um caso = fonte literal, incidente/solução inferidos e ResolvidoPor; uma decisão confirmada)
            // conferidos pelo mesmo verificador usado nas evidências.
            var estadoExportado = new EstadoExportado147(estadoAntesDeB);
            var divergenciasDoEstado = new List<string>();
            VerificacaoDaRetomada147.VerificarEstado(estadoExportado, roteiro, casos, marcador, divergenciasDoEstado);
            Assert.True(divergenciasDoEstado.Count == 0, "Estado diverge do roteiro: " + string.Join("; ", divergenciasDoEstado));
            await using (var db = banco.Contexto())
            {
                var fontes = await db.Set<DocumentoFonte>().ToListAsync(ct);
                var conhecimentos = await db.Conhecimentos.ToListAsync(ct);
                var relacoes = await db.RelacoesDeConhecimento.ToListAsync(ct);
                var snapshots = await db.Set<Dante.Domain.ContextosDeTrabalho.ContextoDeTrabalho>().ToListAsync(ct);
                // Caso 1: o mesmo par das retomadas medidas, identificado pelo marcador exato.
                incidente = Assert.Single(conhecimentos, x => x.Tipo == TipoDeConhecimento.Incidente && EstadoExportado147.ContemIdentificador(x.Conteudo ?? "", marcador)).Id;
                solucao = Assert.Single(conhecimentos, x => x.Tipo == TipoDeConhecimento.Solucao && EstadoExportado147.ContemIdentificador(x.Conteudo ?? "", marcador)).Id;
                Assert.Single(relacoes, r => r.IdOrigem == incidente && r.IdDestino == solucao);
                Assert.All(conhecimentos.Where(x => x.NaturezaDeOrigem == NaturezaDoConteudo.ConclusaoDoAgente), x =>
                { Assert.Equal(StatusDoConhecimento.Inferido, x.Status); Assert.False(x.PodeSerConfirmado); });
                var confirmada = Assert.Single(conhecimentos, x => x.Tipo == TipoDeConhecimento.Decisao);
                Assert.Equal(StatusDoConhecimento.Confirmado, confirmada.Status); Assert.True(confirmada.PodeSerConfirmado);
                Assert.Equal(roteiro.DecisaoLiteral, confirmada.Conteudo);
                var snapshot = Assert.Single(snapshots);
                Assert.Contains(confirmada.Id, snapshot.Dados.IdsDecisoesConfirmadas);
                Assert.All(fontes, f => Assert.Contains($"fonte:{f.Id}@{f.Revisao}", snapshot.Dados.Referencias));
                registrosPersistidos = new { fontes, conhecimentos, relacoes, snapshots };
            }
            foreach (var consulta in consultas)
            {
                var busca = (BuscaDoBrainDto)(await operacoes.ExecutarAsync(escopo, mensagemB, "147MEDICAO", "claude", "brain_buscar_conhecimento", JsonSerializer.SerializeToElement(new { texto = consulta }), ct))!;
                Assert.Equal("lexical", busca.Modo); Assert.Null(busca.ModeloEmbedding);
                recuperacoes.Add(new { consulta, resultados = busca.Resultados.Count,
                    incidenteRecuperado = busca.Resultados.Any(x => x.Item.Id == incidente), solucaoRecuperada = busca.Resultados.Any(x => x.Item.Id == solucao),
                    busca.Modo, busca.ModeloEmbedding });
            }
            var essenciais = AvaliacaoDoHarness147.AvaliarResposta(respostaB, marcador);
            essenciais["proveniencia_consultada"] = rastroB.ConsultaComprovada("brain_mostrar_origem", incidente.ToString(), brain.Instancia + ":147A");
            essenciais["relacao_consultada"] = rastroB.ConsultaComprovada("brain_listar_relacoes", incidente.ToString(), solucao.ToString());
            // Médio/longo: cada caso avaliado à parte na resposta B (identificação, incidente e solução no texto atribuído ao caso)
            // e no rastro (origem e relação pelos Ids do próprio caso), pelos critérios pré-registrados. Curto: vazio.
            var essenciaisPorCaso = RespostaPorCaso147.Avaliar(respostaB, roteiro, criteriosB, duracao, marcador,
                caso => RespostaPorCaso147.Consultas(rastroB, estadoExportado, caso, marcador, brain.Instancia + ":147A"));
            // Presença das unidades essenciais no pacote: condição necessária; ausência de qualquer uma reprova a repetição.
            var essenciaisDoPacote = EssenciaisDaRetomada147.AvaliarPacote(contexto.Pacote.TextoParaInjecao, estadoExportado, roteiro, duracao, marcador);
            // Consolidação condicional: fonte omitida só com os dois trechos pré-registrados íntegros e selecionados.
            var consolidacao = ConsolidacaoDaRetomada147.Verificar(contexto.Pacote.TextoParaInjecao, estadoExportado, roteiro, casos, marcador);
            var ofertasIndevidas = AvaliacaoDoHarness147.OfertasDeConfirmacao(respostaB);
            // Estimativas do produto ficam rotuladas e separadas; não substituem os contadores de uso-real.json.
            var estimativaHistorico = historico.Sum(ConstrutorDeContextoAppService.EstimarTokens);
            var estimativaPacote = contexto.Pacote.Custo.TokensEstimados;
            resultadoFinal = new { repeticao, duracao, roteiro = roteiro.Versao, criteriosB = criteriosB.Versao, marcador, execucao = execucao.Id, banco = banco.Nome, agente = "claude", consultaB,
                encerramentoAConfirmado = true, diretorioARemovidoAntesDeB = true, ferramentasNativasDesabilitadas = true,
                sessoesA = launcherA.Processos.Select(x => x.ProcessId).ToArray(), sessoesB = launcherB.Processos.Select(x => x.ProcessId).ToArray(),
                essenciais, essenciaisPorCaso, essenciaisDoPacote, consolidacao, recuperacoes,
                estimativaNaoMedida = new { historicoVisivelA = estimativaHistorico, pacote = estimativaPacote, metodo = "ceil(bytes UTF8 / 3), por mensagem; não é contagem de tokens" },
                contexto.Pacote, registrosPersistidos, chamadasA, chamadasB, rastroB, ofertasIndevidas,
                estadoAntesDeB, estadoDepoisDeB, estadoPreservado = estadoAntesDeB == estadoDepoisDeB, historico, respostaB, markdown };
            execucao.GravarJson("resultado.json", "resultado", resultadoFinal);
            output.WriteLine("Evidências: " + execucao.Diretorio);
            output.WriteLine(JsonSerializer.Serialize(new { essenciais, essenciaisPorCaso, essenciaisDoPacote, turnos, recuperacoes }, Json));
            // Salvar antes de reprovar: a coleta completa nunca equivale ao cumprimento dos critérios. Regra da #147: cada repetição
            // precisa de 100% dos essenciais — históricos da resposta B, por caso (médio/longo) e no pacote.
            AvaliacaoDoHarness147.ExigirEssenciais(essenciais);
            RespostaPorCaso147.Exigir(essenciaisPorCaso, RespostaPorCaso147.ChavesEsperadas(roteiro, duracao), "Critérios por caso da resposta B");
            RespostaPorCaso147.Exigir(essenciaisDoPacote, EssenciaisDaRetomada147.ChavesEsperadas(roteiro, duracao), "Essenciais do pacote");
            Assert.Equal(roteiro.TotalDeEssenciais(duracao), essenciaisDoPacote.Count);
            Assert.True(consolidacao.Divergencias.Count == 0, "Consolidação condicional: " + string.Join("; ", consolidacao.Divergencias));
            AvaliacaoDoHarness147.ExigirPolitica(respostaB, rastroB, estadoAntesDeB, estadoDepoisDeB);
            Assert.All(rastroB.Chamadas, c => Assert.NotNull(c.Sucesso));
        }
        catch (Exception e) { falha = e; throw; }
        finally
        {
            // Mesmo uma recusa de permissão é evidência e não autoriza ampliar o perfil da CLI.
            // Falha ao fechar evidências não mascara a falha original do ensaio.
            try
            {
                if (resultadoFinal is null) execucao.GravarJson("incompleto.json", "incompleto", new { repeticao, duracao, chamadasA, chamadasB, rastroB, respostaA, respostaSnapshot, respostaB });
                FecharExecucao(execucao, turnos, new() { ["A"] = usoA, ["B"] = usoB }, resultadoFinal is not null);
            }
            catch (Exception fechamento) when (falha is not null) { output.WriteLine("Falha ao fechar evidências: " + fechamento); }
            if (Directory.Exists(privadoA)) Directory.Delete(privadoA, true);
            if (Directory.Exists(privadoB)) Directory.Delete(privadoB, true);
        }
    }

    // Fixture de teste: texto da nota colado na conversa do Telegram em 2026-11-09, sem verificação de quem o redigiu.
    // No ensaio ele é submetido por um ator simulado, em banco descartável. Não comprova ingestão no Brain operacional.
    internal const string NotaDoUsuario = """
        Nota de validação — D.A.N.T.E. Brain
        Identificador: DANTE147-MD-2026-11-09-ALFA
        Projeto: D.A.N.T.E.
        Issue: #147
        Data: 09/11/2026
        Objetivo
        Validar se o Brain consegue ingerir, preservar e recuperar uma fonte Markdown entre sessões independentes, sem depender do histórico da conversa original.
        Contexto da validação
        A validação da Issue #147 utiliza três candidatos de conhecimento:
        1. Incidente IPv6: problema relacionado à conectividade IPv6.
        2. Happy Eyeballs: solução técnica considerada para lidar com tentativas de conexão IPv4 e IPv6.
        3. Limitação da busca: a recuperação lexical não encontrou resultados para consultas por paráfrases.
        Esses três itens permanecem como candidatos pendentes de confirmação.
        Critérios de recuperação
        Após a ingestão desta nota, uma sessão independente deverá conseguir recuperar:
        - O identificador exclusivo desta fonte.
        - A relação entre esta nota e a Issue #147.
        - Os três assuntos mencionados.
        - O estado de confirmação dos candidatos.
        - A data da validação.
        Restrições
        - Não confirmar automaticamente os candidatos.
        - Não criar relações sem autorização.
        - Não transformar conclusões do agente em fatos confirmados.
        - Preservar a proveniência da fonte Markdown.
        - Não alterar o banco canônico fora das operações autorizadas.
        Resultado esperado
        O Brain deverá conseguir localizar esta fonte por consultas como:
        - Qual é a nota de validação do dia 9 de novembro?
        - O que foi documentado sobre a Issue 147?
        - Qual identificador foi atribuído à nota Markdown?
        - Quais são os três assuntos registrados como candidatos?
        Finalidade: comprovar ingestão e recuperação independente de uma fonte Markdown, sem fornecer novamente seu conteúdo à sessão que realizará a consulta.
        """;
    private static readonly string[] PerguntasDaNota =
    [
        "Qual é a nota de validação do dia 9 de novembro?", "O que foi documentado sobre a Issue 147?",
        "Qual identificador foi atribuído à nota Markdown?", "Quais são os três assuntos registrados como candidatos?"
    ];

    // Cenário da nota: um ator simulado como usuário importa pela rota de produção (autoria Usuario) e uma sessão independente,
    // sem o conteúdo da nota no pedido, responde às perguntas da própria nota usando só o Brain.
    [EnsaioIndependente]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NotaMarkdownDoUsuarioERecuperadaPorSessaoIndependente(int repeticao)
    {
        var admin = new Npgsql.NpgsqlConnectionStringBuilder(Banco.Administrativa);
        Assert.Equal("127.0.0.1", admin.Host); Assert.Equal(55433, admin.Port); Assert.Equal("postgres", admin.Database);
        var execucao = ExecucaoDeMedicao147.Iniciar(RaizDeEvidencias147.Validar(RaizDeEvidencias147.Padrao()), "nota", repeticao);
        await using var banco = await Banco.CriarAsync();
        var privadoB = Directory.CreateTempSubdirectory("dante147-nota-b-").FullName;
        using var app = BrainMcpTests.CriarApp(banco.ConnectionString);
        var brain = app.GetRequiredService<TelegramBrain>(); await BrainMcpTests.SelecionarAsync(brain);
        using var ferramentas = BrainMcpTests.Provedor(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        var chamadasB = new List<string>(); var rastroB = new RastroDeFerramentas147();
        var usoB = new CapturaDeUsoClaude147(); var turnos = new List<TurnoMedido147>(); var launcherB = new LancadorSomenteBrain(privadoB, usoB);
        string respostaB = ""; object? resultadoFinal = null; Exception? falha = null;
        try
        {
            // Sessão A: ator simulado no papel do usuário envia o comando de importação com o texto da fixture.
            var importe = "importe Markdown Nota de validação — D.A.N.T.E. Brain: " + NotaDoUsuario;
            Assert.Contains("Fonte bruta adicionada", await brain.AtenderAsync(BrainMcpTests.Mensagem(importe, 100), importe, null, ct));
            await using (var db = banco.Contexto())
            {
                var fonte = await db.Set<DocumentoFonte>().SingleAsync(ct);
                Assert.Equal("markdown", fonte.Formato); Assert.Equal(AutoriaDaFonte.Usuario, fonte.Autoria);
                Assert.Contains("DANTE147-MD-2026-11-09-ALFA", fonte.Conteudo);
                Assert.Empty(await db.CandidatosDeConhecimento.ToListAsync(ct)); Assert.Empty(await db.Conhecimentos.ToListAsync(ct));
            }
            var medicao = BrainMcpTests.Mensagem("medição", 150);
            var escopo = (await brain.ResolverEscopoMcpAsync(medicao, ct))!;
            // Recuperação do Brain sozinho (MCP, AND implícito) para cada pergunta, sem reformulação do agente.
            var buscasDiretas = new List<object>();
            foreach (var pergunta in PerguntasDaNota)
            {
                await using var s = app.CreateAsyncScope();
                s.ServiceProvider.GetRequiredService<Dante.Application.SegurancaDoBrain.AutorizacaoDoBrain>().Estabelecer(escopo.Identidade, escopo.Acesso);
                var busca = await s.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>().BuscarAsync(escopo.Acesso, new() { Texto = pergunta, Limite = 10 }, ct);
                buscasDiretas.Add(new { pergunta, resultados = busca.Resultados.Count, notaRecuperada = busca.Resultados.Any(x => x.Origem == OrigemDoResultado.FonteBruta), busca.Modo });
            }

            var pedidoB = "Responda usando exclusivamente o Brain, citando a origem e o rótulo de cada item:\n" + string.Join("\n", PerguntasDaNota.Select((p, i) => $"{i + 1}. {p}")) +
                "\nInforme também o estado de confirmação dos candidatos e a data da validação.";
            Assert.DoesNotContain("DANTE147", pedidoB); Assert.DoesNotContain("Happy", pedidoB);
            Assert.Empty(Directory.EnumerateFileSystemEntries(privadoB));
            var estadoAntesDeB = await EstadoPersistidoAsync(banco, ct);
            var mensagemB = BrainMcpTests.Mensagem(pedidoB, 300);
            var servidoresB = await ferramentas.ForSessionAsync("147NB", new(42, AgentKind.Claude, Dante.Application.Contextos.JobExecutionContext.General(privadoB), BrainConversation: mensagemB.ParaFerramentas()), ct);
            Assert.Single(servidoresB);
            var contexto = await brain.PrepararAsync(mensagemB, "147NB", pedidoB, ct);
            var notaNoPacote = contexto?.Pacote.Itens.Any(x => x.Origem == "fonte_bruta") == true;
            if (contexto is not null)
            {
                GravarPacote(execucao, contexto.Pacote);
                Assert.True(PacoteInjetado147.Confere(contexto.Texto, contexto.Pacote.TextoParaInjecao), "Texto enviado não começa pelo pacote exportado.");
            }
            await using (var b = new ClaudeSessionDriver(launcherB))
            {
                await b.StartAsync(new(privadoB, IsGeneral: true, Profile: AgentPermissionProfile.Manual, ToolServers: servidoresB), ct);
                await using var eventos = b.ReadEventsAsync(ct).GetAsyncEnumerator();
                var entradaB = contexto?.Texto ?? pedidoB;
                execucao.GravarTexto("entrada-b.txt", "entrada-enviada", entradaB); await b.StartTurnAsync(entradaB, ct);
                if (contexto is not null) await brain.RegistrarInjecaoAsync(contexto, ct);
                var turnoB = await RespostaAsync(b, eventos, chamadasB, false, ct, rastroB); respostaB = turnoB.Texto;
                turnos.Add(new("B", usoB.Proximo(), turnoB.Uso)); execucao.GravarTexto("resposta-b.txt", "resposta-visivel", respostaB);
            }
            await launcherB.ConfirmarEncerramentoAsync(ct); ferramentas.EndSession("147NB");
            var estadoDepoisDeB = await EstadoPersistidoAsync(banco, ct);
            var t = respostaB;
            static bool Re(string texto, string padrao) => System.Text.RegularExpressions.Regex.IsMatch(texto, padrao, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var criterios = new Dictionary<string, bool>
            {
                ["identificador"] = t.Contains("DANTE147-MD-2026-11-09-ALFA", StringComparison.Ordinal),
                ["issue_147"] = Re(t, @"(#|issue\s*)147\b"),
                ["assunto_ipv6"] = Re(t, "IPv6"),
                ["assunto_happy_eyeballs"] = Re(t, "Happy Eyeballs"),
                // Nome do assunto na nota ("Limitação da busca") ou o termo da descrição.
                ["assunto_busca"] = Re(t, @"limita\w* da busca|par[áa]frase"),
                ["estado_pendente"] = Re(t, "pendente"),
                ["data"] = t.Contains("09/11/2026", StringComparison.Ordinal) || Re(t, "9 de novembro de 2026"),
                ["rotulo_fonte_usuario"] = Re(t, "Fonte bruta de autoria do usuário, não confirmada")
            };
            var ofertas = AvaliacaoDoHarness147.OfertasDeConfirmacao(respostaB);
            resultadoFinal = new { repeticao, execucao = execucao.Id, banco = banco.Nome, agente = "claude", pedidoB, notaNoPacote, buscasDiretas, criterios, ofertas,
                rastroB, chamadasB, estadoPreservado = estadoAntesDeB == estadoDepoisDeB, respostaB,
                estimativaNaoMedida = new { pacote = contexto?.Pacote.Custo.TokensEstimados, nota = ConstrutorDeContextoAppService.EstimarTokens(NotaDoUsuario), metodo = "ceil(bytes UTF8 / 3); não é contagem de tokens" } };
            execucao.GravarJson("resultado.json", "resultado", resultadoFinal);
            output.WriteLine("Evidências: " + execucao.Diretorio);
            output.WriteLine(JsonSerializer.Serialize(new { criterios, buscasDiretas, notaNoPacote }, Json));
            // Salvar antes de reprovar.
            var ausentes = criterios.Where(x => !x.Value).Select(x => x.Key).ToArray();
            Assert.True(ausentes.Length == 0, "Critérios da nota não demonstrados: " + string.Join(", ", ausentes));
            AvaliacaoDoHarness147.ExigirPolitica(respostaB, rastroB, estadoAntesDeB, estadoDepoisDeB);
        }
        catch (Exception e) { falha = e; throw; }
        finally
        {
            try
            {
                if (resultadoFinal is null) execucao.GravarJson("incompleto.json", "incompleto", new { repeticao, chamadasB, rastroB, respostaB });
                FecharExecucao(execucao, turnos, new() { ["B"] = usoB }, resultadoFinal is not null);
            }
            catch (Exception fechamento) when (falha is not null) { output.WriteLine("Falha ao fechar evidências: " + fechamento); }
            if (Directory.Exists(privadoB)) Directory.Delete(privadoB, true);
        }
    }

    private readonly record struct Turno147(string Texto, AgentTokenUsage? Uso);

    private static async Task<Turno147> RespostaAsync(IAgentSessionDriver driver, IAsyncEnumerator<AgentEvent> eventos, List<string> chamadas, bool mutacoesPermitidas, CancellationToken ct, RastroDeFerramentas147? rastro = null)
    {
        var texto = new StringBuilder();
        while (await eventos.MoveNextAsync())
        {
            switch (eventos.Current)
            {
                case ToolStartedEvent t:
                    chamadas.Add(t.Description); rastro?.Iniciar(t);
                    Assert.Equal("dante_brain", t.Server); break;
                case ToolCompletedEvent fimFerramenta: rastro?.Concluir(fimFerramenta); break;
                case MessageCompletedEvent m: texto.Append(m.Text); break;
                case ApprovalRequestedEvent a:
                    rastro?.AprovacoesSolicitadas.Add(a.Action);
                    var nome = a.Action.Split(' ', 2)[0];
                    var permitida = mutacoesPermitidas && ServidorMcpDoBrain.Ferramentas.Any(f => nome == "mcp__dante_brain__" + f.Nome);
                    await driver.RespondAsync(a.UpstreamRequestId, new AgentApprovalResponse(permitida ? AgentApprovalDecision.ApproveOnce : AgentApprovalDecision.Deny), ct); break;
                case ErrorEvent e: throw new InvalidOperationException(e.Message);
                case UserInputRequestedEvent: throw new InvalidOperationException("Pedido inesperado de informação para fixture fechada.");
                case TurnCompletedEvent fim: Assert.Equal(AgentTurnOutcome.Completed, fim.Outcome); return new(texto.ToString(), fim.Usage);
            }
        }
        throw new InvalidOperationException("Sessão encerrou antes da conclusão do turno.");
    }

    internal const string SeparadorDoHistorico = "\n\n";

    // PacoteDeContexto exportado duas vezes: texto exato injetado e estrutura completa (itens, referências, custo estimado).
    internal static void GravarPacote(ExecucaoDeMedicao147 execucao, PacoteDeContextoDto pacote)
    {
        execucao.GravarTexto("pacote-texto-para-injecao.txt", "comparacao-pacote", pacote.TextoParaInjecao);
        execucao.GravarJson("pacote.json", "pacote", pacote);
    }

    // Executado também em falha: todos os results observados (inclusive os não associados a um turno concluído),
    // a sessão de cada CLI e o manifesto. O estado diz se a coleta terminou; Confiavel diz se o turno pode ser usado.
    internal static string FecharExecucao(ExecucaoDeMedicao147 execucao, IReadOnlyList<TurnoMedido147> turnos,
        Dictionary<string, CapturaDeUsoClaude147> capturas, bool completa, string? mounts = null)
    {
        execucao.GravarJson("uso-real.json", "uso-real", new
        {
            fonte = "result do stream-json do Claude Code, campo a campo; ausente = indisponivel; result de erro ou sem chamada = nao-medido",
            semantica = new
            {
                usage = "por turno: soma de todas as chamadas do loop principal do turno; com ferramentas o contexto é recontado em cada chamada (ChamadasNoTurno)",
                porModeloNoTurno = "model_usage_since_last_result: chamadas desde o result anterior, por modelo, incluindo subagentes/internas",
                porModeloAcumulado = "modelUsage: total acumulado da sessão; não somar entre turnos",
                entradaDaPrimeiraChamada = "first_request_input_tokens, campo interno opcional: entrada da primeira chamada do turno",
                historico = "historico-visivel.txt contém só pedidos e respostas visíveis de A, não o transcript integral"
            },
            turnos = turnos.Select(t => new { t.Rotulo, t.Confiavel, t.ConsistenteComDriver, t.Associacao.ResultsPendentes, t.Associacao.FalhasDeLeitura, t.Uso, t.UsoDoDriver }),
            sessoes = capturas.ToDictionary(c => c.Key, c => new { c.Value.Sessao, c.Value.FalhasDeLeitura, results = c.Value.Turnos })
        });
        var (commit, limpa) = RaizDeEvidencias147.EstadoGit();
        return execucao.Finalizar(completa ? "completo" : "incompleto", commit, limpa, mounts);
    }

    // Serialização única do estado, compartilhada com a restauração (RestauracaoDoBrain147) para a prova byte a byte.
    private static async Task<string> EstadoPersistidoAsync(Banco banco, CancellationToken ct)
    {
        await using var db = banco.Contexto();
        return await RestauracaoDoBrain147.EstadoPersistidoAsync(db, ct);
    }

    private sealed class LancadorSomenteBrain(string diretorio, CapturaDeUsoClaude147 uso) : IInteractiveAgentProcessLauncher
    {
        public List<InteractiveAgentProcess> Processos { get; } = [];
        public async Task<InteractiveAgentProcess> StartAsync(AgentProcessRequest request, Func<string, string>? redactOutput = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal(AgentKind.Claude, request.Agent); Assert.True(request.IsGeneral);
            var args = request.Arguments.ToArray(); var i = Array.IndexOf(args, "--tools"); Assert.True(i >= 0);
            args[i + 1] = ""; // Retira ferramentas nativas; nenhuma permissão é ampliada.
            var config = Path.Combine(diretorio, "cli"); Directory.CreateDirectory(config);
            var origem = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
            var destino = Path.Combine(config, ".credentials.json");
            if (File.Exists(origem)) { File.Copy(origem, destino, false); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destino, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            var ambiente = new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = config };
            // Observa cada linha bruta (usage/modelo) sem alterá-la; o driver recebe o mesmo texto.
            Func<string, string> observar = linha => uso.Observar(redactOutput is null ? linha : redactOutput(linha));
            var processo = await new InteractiveAgentProcessLauncher(new AgentExecutableResolver()).StartAsync(request with { Arguments = args, EnvironmentVariables = ambiente }, observar, cancellationToken);
            Assert.True(processo.ContencaoNativa); Processos.Add(processo); return processo;
        }
        public async Task ConfirmarEncerramentoAsync(CancellationToken ct)
        {
            Assert.Single(Processos);
            foreach (var p in Processos) await p.Completion.WaitAsync(ct);
        }
    }

    private sealed class EnsaioIndependenteAttribute : TheoryAttribute
    {
        public EnsaioIndependenteAttribute()
        {
            if (Environment.GetEnvironmentVariable("DANTE_LIVE_BRAIN_MCP") != "1" || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DANTE_TEST_POSTGRES")))
                Skip = "Opt-in: DANTE_LIVE_BRAIN_MCP=1 e PostgreSQL descartável em 127.0.0.1:55433.";
        }
    }
}
