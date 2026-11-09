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
public sealed class Issue147BrainIndependenteTests(ITestOutputHelper output)
{
    private const string ConsultaB = "Retome a validação de confiabilidade do D.A.N.T.E. Qual problema foi analisado, qual solução foi adotada, qual restrição permanece e qual é o próximo passo? Consulte também as relações e a origem. Separe fonte bruta, fato confirmado e conclusão do agente. Inclua o identificador verificável do ensaio. Use exclusivamente o Brain.";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [EnsaioIndependente]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SessaoIndependenteRetomaCasoRealSomentePeloBrain(int repeticao)
    {
        var admin = new Npgsql.NpgsqlConnectionStringBuilder(Banco.Administrativa);
        // Recusa erro de configuração antes de criar qualquer banco: laboratório conhecido, nunca o canônico.
        Assert.Equal("127.0.0.1", admin.Host); Assert.Equal(55433, admin.Port); Assert.Equal("postgres", admin.Database);
        await using var banco = await Banco.CriarAsync();
        var privadoA = Directory.CreateTempSubdirectory("dante147-a-").FullName;
        var privadoB = Directory.CreateTempSubdirectory("dante147-b-").FullName;
        var id = Guid.NewGuid().ToString("N");
        var evidencias = Path.Combine(Environment.GetEnvironmentVariable("DANTE_147_EVIDENCIAS") ?? Path.GetTempPath(), "dante147-" + id);
        Directory.CreateDirectory(evidencias);
        var metricas = Path.Combine(evidencias, "metricas.jsonl");
        var registro = new RegistroDeMetricasEmArquivo(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DANTE_BRAIN_METRICS_FILE"] = metricas }).Build());
        using var app = BrainMcpTests.CriarApp(banco.ConnectionString, s => s.AddSingleton<IRegistroDeMetricasDoBrain>(registro));
        var brain = app.GetRequiredService<TelegramBrain>(); await BrainMcpTests.SelecionarAsync(brain);
        using var ferramentas = BrainMcpTests.Provedor(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var ct = timeout.Token;
        var historico = new List<string>(); var chamadasA = new List<string>(); var chamadasB = new List<string>();
        var rastroB = new RastroDeFerramentas147();
        var launcherA = new LancadorSomenteBrain(privadoA); var launcherB = new LancadorSomenteBrain(privadoB);
        // Caso real da PR #283. O marcador é aleatório e só verifica transferência; não inventa fato do produto.
        var marcador = "verificacao147" + Guid.NewGuid().ToString("N");
        var markdown = "# Validação de confiabilidade do D.A.N.T.E.\n\n" +
            "Caso real: PR #283, merge 146a0fe40ea3137707f490e01df8c1219afdaa71.\n" +
            "## Incidente\nCompletion podia sinalizar término antes de confirmar a limpeza dos processos descendentes.\n" +
            "## Solução\nA contenção Linux usa escopos systemd e cgroups v2; o sucesso exige raiz encerrada e grupo vazio.\n" +
            "## Limites\nO Worker ativo e o banco canônico devem ser preservados. A busca permanece lexical.\n" +
            "## Próxima etapa\nValidar a continuidade independente do Brain na Issue #147, sem iniciar a Issue #282.\n" +
            "Referência pública: docs/development/contencao-de-agentes.md na revisão do merge.\n" +
            "Identificador exclusivo do ensaio: " + marcador;
        var pedidoA = "Execute a análise deste caso real da confiabilidade do D.A.N.T.E. para preparar sua continuidade em outra sessão. " +
            "Minha decisão literal: O Brain deve permanecer em modo lexical. " +
            "Registre o Markdown abaixo como evidência bruta no Brain, preservando exatamente o texto. " +
            "Capture separadamente um Incidente e uma Solucao, natureza ConclusaoDoAgente, com referência fonte:<id>@<revisão> da evidência na justificativa e com o identificador exclusivo no conteúdo. " +
            "Capture também a minha decisão literal como Decisao/DitoPeloUsuario. Use tags teste-147 e sensibilidade Pessoal em todos os registros deste ensaio para compatibilidade com o snapshot pessoal. " +
            "Vincule os dois candidatos incidente e solução com brain_vincular_candidatos. Não confirme nada; o operador de teste fará a confirmação na rota de negócio. " +
            "Apresente seu diagnóstico e os registros preparados. Sem arquivos, transcript ou raciocínio privado na evidência.\n\n" + markdown;
        string respostaA = "", respostaB = "", respostaSnapshot = "";
        object? resultadoFinal = null;
        try
        {
            var mensagemA = BrainMcpTests.Mensagem(pedidoA);
            var servidoresA = await ferramentas.ForSessionAsync("147A", new(42, AgentKind.Claude, Dante.Application.Contextos.JobExecutionContext.General(privadoA), BrainConversation: mensagemA.ParaFerramentas()), ct);
            Assert.Single(servidoresA);
            await using (var a = new ClaudeSessionDriver(launcherA))
            {
                await a.StartAsync(new(privadoA, IsGeneral: true, Profile: AgentPermissionProfile.Manual, ToolServers: servidoresA), ct);
                await using var eventos = a.ReadEventsAsync(ct).GetAsyncEnumerator();
                await a.StartTurnAsync(pedidoA, ct);
                respostaA = await RespostaAsync(a, eventos, chamadasA, true, ct); historico.Add(pedidoA); historico.Add(respostaA);
                await using (var db = banco.Contexto())
                {
                    var fonte = await db.Set<DocumentoFonte>().SingleAsync();
                    Assert.Equal(markdown, fonte.Conteudo); Assert.Equal("markdown", fonte.Formato); Assert.Equal(AutoriaDaFonte.Agente, fonte.Autoria);
                    Assert.Contains(brain.Instancia + ":147A", fonte.Proveniencia);
                    var candidatos = await db.CandidatosDeConhecimento.ToListAsync(); Assert.Equal(3, candidatos.Count);
                    Assert.Equal(2, candidatos.Count(x => x.Natureza == NaturezaDoConteudo.ConclusaoDoAgente));
                    Assert.All(candidatos.Where(x => x.Natureza == NaturezaDoConteudo.ConclusaoDoAgente), x => Assert.Contains(fonte.Id.ToString(), x.Justificativa));
                    Assert.Empty(await db.Conhecimentos.ToListAsync());
                }
                // Operador simulado, negócio real. Aprovação da CLI acima não consolida candidatos.
                for (var n = 0; n < 3; n++)
                {
                    Assert.Contains("Candidatos pendentes", await brain.AtenderAsync(BrainMcpTests.Mensagem("listar candidatos", 200 + n * 2), "listar candidatos"));
                    Assert.Contains("consolidado", await brain.AtenderAsync(BrainMcpTests.Mensagem("confirmar primeira", 201 + n * 2), "confirmar primeira"));
                }
                string refs; Guid decisao;
                await using (var db = banco.Contexto())
                {
                    var conhecimentos = await db.Conhecimentos.ToListAsync(); Assert.Equal(3, conhecimentos.Count);
                    Assert.All(conhecimentos.Where(x => x.NaturezaDeOrigem == NaturezaDoConteudo.ConclusaoDoAgente), x => Assert.Equal(StatusDoConhecimento.Inferido, x.Status));
                    decisao = Assert.Single(conhecimentos, x => x.Tipo == TipoDeConhecimento.Decisao && x.Status == StatusDoConhecimento.Confirmado).Id;
                    var relacao = await db.RelacoesDeConhecimento.SingleAsync();
                    Assert.Equal(Dante.Domain.RelacoesDeConhecimento.TipoDeRelacao.ResolvidoPor, relacao.Tipo);
                    var fonte = await db.Set<DocumentoFonte>().SingleAsync();
                    refs = string.Join(", ", conhecimentos.Select(x => $"conhecimento:{x.Id}@{x.Revisao}").Append($"fonte:{fonte.Id}@{fonte.Revisao}"));
                }
                var salvar = "Consulte o escopo e o contexto atuais e persista o estado operacional da tarefa analisada: objetivo, tarefa, progresso, último resultado e próximo passo. " +
                    "Referencie estes registros existentes, sem copiar o Markdown para o snapshot: " + refs + ". Decisão confirmada a referenciar em ids_decisoes_confirmadas: " + decisao + ". Não grave transcript. Informe o identificador do ensaio na resposta final.";
                var mensagemSnapshot = BrainMcpTests.Mensagem(salvar, 210);
                ferramentas.BeginTurn("147A", new AgentInput(salvar, []) { BrainConversation = mensagemSnapshot.ParaFerramentas() });
                await a.StartTurnAsync(salvar, ct); respostaSnapshot = await RespostaAsync(a, eventos, chamadasA, true, ct);
                historico.Add(salvar); historico.Add(respostaSnapshot);
            }
            // Dispose de produção aguarda Completion. Confirma sucesso estrito antes de B existir.
            await launcherA.ConfirmarEncerramentoAsync(ct); ferramentas.EndSession("147A");
            Directory.Delete(privadoA, true); Assert.False(Directory.Exists(privadoA));
            Assert.Empty(Directory.EnumerateFileSystemEntries(privadoB));
            var estadoAntesDeB = await EstadoPersistidoAsync(banco, ct);
            var mensagemB = BrainMcpTests.Mensagem(ConsultaB, 300);
            var servidoresB = await ferramentas.ForSessionAsync("147B", new(42, AgentKind.Claude, Dante.Application.Contextos.JobExecutionContext.General(privadoB), BrainConversation: mensagemB.ParaFerramentas()), ct);
            Assert.Single(servidoresB);
            var contexto = await brain.PrepararAsync(mensagemB, "147B", ConsultaB, ct);
            Assert.NotNull(contexto); Assert.NotEmpty(contexto.Pacote.Itens);
            Assert.Contains(contexto.Pacote.Itens, x => x.Origem == "snapshot");
            Assert.Contains(contexto.Pacote.Itens, x => x.Origem == "fonte_bruta");
            await using (var b = new ClaudeSessionDriver(launcherB))
            {
                await b.StartAsync(new(privadoB, IsGeneral: true, Profile: AgentPermissionProfile.Manual, ToolServers: servidoresB), ct);
                await using var eventos = b.ReadEventsAsync(ct).GetAsyncEnumerator();
                // Pedido não contém a resposta. Contexto vem exclusivamente do construtor de produção sob teste.
                await b.StartTurnAsync(contexto.Texto, ct); await brain.RegistrarInjecaoAsync(contexto, ct);
                respostaB = await RespostaAsync(b, eventos, chamadasB, false, ct, rastroB);
                await brain.RegistrarTurnoAsync(contexto.Correlacao, "claude", ConstrutorDeContextoAppService.EstimarTokens(respostaB), AgentTurnOutcome.Completed, null, ct);
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
            await using (var db = banco.Contexto())
            {
                var fontes = await db.Set<DocumentoFonte>().ToListAsync(ct);
                var conhecimentos = await db.Conhecimentos.ToListAsync(ct);
                var relacoes = await db.RelacoesDeConhecimento.ToListAsync(ct);
                var snapshots = await db.Set<Dante.Domain.ContextosDeTrabalho.ContextoDeTrabalho>().ToListAsync(ct);
                incidente = Assert.Single(conhecimentos, x => x.Tipo == TipoDeConhecimento.Incidente).Id;
                solucao = Assert.Single(conhecimentos, x => x.Tipo == TipoDeConhecimento.Solucao).Id;
                Assert.Equal(incidente, Assert.Single(relacoes).IdOrigem); Assert.Equal(solucao, Assert.Single(relacoes).IdDestino);
                Assert.All(conhecimentos.Where(x => x.NaturezaDeOrigem == NaturezaDoConteudo.ConclusaoDoAgente), x =>
                { Assert.Equal(StatusDoConhecimento.Inferido, x.Status); Assert.False(x.PodeSerConfirmado); });
                var confirmada = Assert.Single(conhecimentos, x => x.Tipo == TipoDeConhecimento.Decisao);
                Assert.Equal(StatusDoConhecimento.Confirmado, confirmada.Status); Assert.True(confirmada.PodeSerConfirmado);
                Assert.Equal("O Brain deve permanecer em modo lexical.", confirmada.Conteudo);
                var snapshot = Assert.Single(snapshots);
                Assert.Contains(confirmada.Id, snapshot.Dados.IdsDecisoesConfirmadas);
                Assert.Contains($"fonte:{Assert.Single(fontes).Id}@{Assert.Single(fontes).Revisao}", snapshot.Dados.Referencias);
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
            var ofertasIndevidas = AvaliacaoDoHarness147.OfertasDeConfirmacao(respostaB);
            var tokensHistorico = historico.Sum(ConstrutorDeContextoAppService.EstimarTokens);
            var tokensPacote = contexto.Pacote.Custo.TokensEstimados;
            resultadoFinal = new { repeticao, banco = banco.Nome, baseGit = "146a0fe40ea3137707f490e01df8c1219afdaa71", agente = "claude", consultaB = ConsultaB,
                encerramentoAConfirmado = true, diretorioARemovidoAntesDeB = true, ferramentasNativasDesabilitadas = true,
                sessoesA = launcherA.Processos.Select(x => x.ProcessId).ToArray(), sessoesB = launcherB.Processos.Select(x => x.ProcessId).ToArray(),
                essenciais, recuperacoes, tokensHistoricoVisivelA = tokensHistorico, tokensPacote, razao = (double)tokensPacote / tokensHistorico,
                estimador = "ceil(bytes UTF8 / 3), por mensagem; histórico = pedidos e respostas visíveis de A, sem uso agregado CLI",
                contexto.Pacote, registrosPersistidos, chamadasA, chamadasB, rastroB, ofertasIndevidas,
                estadoAntesDeB, estadoDepoisDeB, estadoPreservado = estadoAntesDeB == estadoDepoisDeB, historico, respostaB, markdown };
            await File.WriteAllTextAsync(Path.Combine(evidencias, "resultado.json"), JsonSerializer.Serialize(resultadoFinal, Json), ct);
            output.WriteLine("Evidências: " + evidencias);
            output.WriteLine(JsonSerializer.Serialize(new { essenciais, tokensHistorico, tokensPacote, recuperacoes }, Json));
            // Salvar antes de reprovar: a coleta completa nunca equivale ao cumprimento dos critérios.
            AvaliacaoDoHarness147.ExigirEssenciais(essenciais);
            AvaliacaoDoHarness147.ExigirPolitica(respostaB, rastroB, estadoAntesDeB, estadoDepoisDeB);
            Assert.All(rastroB.Chamadas, c => Assert.NotNull(c.Sucesso));
        }
        finally
        {
            // Mesmo uma recusa de permissão é evidência e não autoriza ampliar o perfil da CLI.
            if (resultadoFinal is null) await File.WriteAllTextAsync(Path.Combine(evidencias, "incompleto.json"), JsonSerializer.Serialize(new { repeticao, chamadasA, chamadasB, rastroB, respostaA, respostaSnapshot, respostaB }, Json));
            if (Directory.Exists(privadoA)) Directory.Delete(privadoA, true);
            if (Directory.Exists(privadoB)) Directory.Delete(privadoB, true);
        }
    }

    // Nota Markdown fornecida pelo usuário em 2026-11-09 (texto literal da mensagem dele; não é fixture inventada).
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

    // Cenário da nota: o usuário importa pela rota de produção (autoria Usuario) e uma sessão independente,
    // sem o conteúdo da nota no pedido, responde às perguntas da própria nota usando só o Brain.
    [EnsaioIndependente]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NotaMarkdownDoUsuarioERecuperadaPorSessaoIndependente(int repeticao)
    {
        var admin = new Npgsql.NpgsqlConnectionStringBuilder(Banco.Administrativa);
        Assert.Equal("127.0.0.1", admin.Host); Assert.Equal(55433, admin.Port); Assert.Equal("postgres", admin.Database);
        await using var banco = await Banco.CriarAsync();
        var privadoB = Directory.CreateTempSubdirectory("dante147-nota-b-").FullName;
        var evidencias = Path.Combine(Environment.GetEnvironmentVariable("DANTE_147_EVIDENCIAS") ?? Path.GetTempPath(), "dante147-nota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidencias);
        using var app = BrainMcpTests.CriarApp(banco.ConnectionString);
        var brain = app.GetRequiredService<TelegramBrain>(); await BrainMcpTests.SelecionarAsync(brain);
        using var ferramentas = BrainMcpTests.Provedor(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        var chamadasB = new List<string>(); var rastroB = new RastroDeFerramentas147(); var launcherB = new LancadorSomenteBrain(privadoB);
        string respostaB = ""; object? resultadoFinal = null;
        try
        {
            // Sessão A = o próprio usuário pelo Telegram: comando de importação com o texto literal.
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
            await using (var b = new ClaudeSessionDriver(launcherB))
            {
                await b.StartAsync(new(privadoB, IsGeneral: true, Profile: AgentPermissionProfile.Manual, ToolServers: servidoresB), ct);
                await using var eventos = b.ReadEventsAsync(ct).GetAsyncEnumerator();
                await b.StartTurnAsync(contexto?.Texto ?? pedidoB, ct); if (contexto is not null) await brain.RegistrarInjecaoAsync(contexto, ct);
                respostaB = await RespostaAsync(b, eventos, chamadasB, false, ct, rastroB);
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
            resultadoFinal = new { repeticao, banco = banco.Nome, agente = "claude", pedidoB, notaNoPacote, buscasDiretas, criterios, ofertas,
                rastroB, chamadasB, estadoPreservado = estadoAntesDeB == estadoDepoisDeB, respostaB,
                tokensPacote = contexto?.Pacote.Custo.TokensEstimados, tokensNota = ConstrutorDeContextoAppService.EstimarTokens(NotaDoUsuario) };
            await File.WriteAllTextAsync(Path.Combine(evidencias, "resultado.json"), JsonSerializer.Serialize(resultadoFinal, Json), ct);
            output.WriteLine("Evidências: " + evidencias);
            output.WriteLine(JsonSerializer.Serialize(new { criterios, buscasDiretas, notaNoPacote }, Json));
            // Salvar antes de reprovar.
            var ausentes = criterios.Where(x => !x.Value).Select(x => x.Key).ToArray();
            Assert.True(ausentes.Length == 0, "Critérios da nota não demonstrados: " + string.Join(", ", ausentes));
            AvaliacaoDoHarness147.ExigirPolitica(respostaB, rastroB, estadoAntesDeB, estadoDepoisDeB);
        }
        finally
        {
            if (resultadoFinal is null) await File.WriteAllTextAsync(Path.Combine(evidencias, "incompleto.json"), JsonSerializer.Serialize(new { repeticao, chamadasB, rastroB, respostaB }, Json));
            if (Directory.Exists(privadoB)) Directory.Delete(privadoB, true);
        }
    }

    private static async Task<string> RespostaAsync(IAgentSessionDriver driver, IAsyncEnumerator<AgentEvent> eventos, List<string> chamadas, bool mutacoesPermitidas, CancellationToken ct, RastroDeFerramentas147? rastro = null)
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
                case TurnCompletedEvent fim: Assert.Equal(AgentTurnOutcome.Completed, fim.Outcome); return texto.ToString();
            }
        }
        throw new InvalidOperationException("Sessão encerrou antes da conclusão do turno.");
    }

    private static async Task<string> EstadoPersistidoAsync(Banco banco, CancellationToken ct)
    {
        await using var db = banco.Contexto();
        return JsonSerializer.Serialize(new
        {
            fontes = (await db.Set<DocumentoFonte>().AsNoTracking().ToListAsync(ct)).OrderBy(x => x.Id),
            conhecimentos = (await db.Conhecimentos.AsNoTracking().ToListAsync(ct)).OrderBy(x => x.Id),
            candidatos = (await db.CandidatosDeConhecimento.AsNoTracking().ToListAsync(ct)).OrderBy(x => x.Id),
            vinculos = (await db.Set<VinculoEntreCandidatos>().AsNoTracking().ToListAsync(ct)).OrderBy(x => x.Id),
            relacoes = (await db.RelacoesDeConhecimento.AsNoTracking().ToListAsync(ct)).OrderBy(x => x.Id),
            snapshots = (await db.Set<Dante.Domain.ContextosDeTrabalho.ContextoDeTrabalho>().AsNoTracking().ToListAsync(ct)).OrderBy(x => x.Id)
        }, Json);
    }

    private sealed class LancadorSomenteBrain(string diretorio) : IInteractiveAgentProcessLauncher
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
            var processo = await new InteractiveAgentProcessLauncher(new AgentExecutableResolver()).StartAsync(request with { Arguments = args, EnvironmentVariables = ambiente }, redactOutput, cancellationToken);
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
