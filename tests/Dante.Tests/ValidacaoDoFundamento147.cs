using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dante.Application.ConstrucaoDeContexto;
using Dante.Application.DocumentosFonte;
using Dante.Domain.Agentes;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.ContextosDeTrabalho;
using Dante.Domain.DocumentosFonte;
using Dante.Domain.Projetos;
using Dante.Domain.RelacoesDeConhecimento;
using Dante.Worker.Brain;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

// Validação funcional (não medição) do contrato da PR #292 com o Claude Code real: a sessão A registra uma evidência
// controlada e captura incidente, solução, restrição e próximo passo com fundamento; o operador simulado autoriza o
// registro pela rota de negócio do Telegram; a sessão B, independente, recupera tudo só pelo Brain, com origem e relações.
// Segurança, consolidação e snapshot são verificados depois pelo mesmo canal MCP de produção. Banco descartável apenas.
public sealed partial class Issue147BrainIndependenteTests
{
    private const string Situacao = "Situação: pendente de revisão humana.";

    internal static string EvidenciaDoFundamento(string marcador) =>
        $"# Validação funcional do fundamento — ensaio {marcador}\n" +
        "## Incidente\nO Worker publicado em 01/11 não oferecia o campo fundamento do Brain.\n" +
        "## Solução\nValidar o contrato da PR #292 em harness controlado com PostgreSQL descartável.\n" +
        $"## Restrição\nO banco canônico e o Worker em execução não podem ser alterados.\n{Situacao}\n" +
        $"## Próximo passo\nPré-registrar o próximo experimento da Issue #147 somente após esta validação.\n{Situacao}";

    // Linha de conteúdo de cada seção: precisa voltar literalmente na resposta de B e na citação de A.
    private static readonly (string Papel, string Linha)[] Linhas =
    [
        ("incidente", "O Worker publicado em 01/11 não oferecia o campo fundamento do Brain."),
        ("solucao", "Validar o contrato da PR #292 em harness controlado com PostgreSQL descartável."),
        ("restricao", "O banco canônico e o Worker em execução não podem ser alterados."),
        ("proximo_passo", "Pré-registrar o próximo experimento da Issue #147 somente após esta validação.")
    ];

    // Proveniência individual na resposta B: cada conhecimento precisa da própria ReferenciaDaFonte, completa e literal,
    // depois da própria linha de conteúdo (a ocorrência conta para o item cuja linha é a última antes dela). O
    // identificador da fonte, comum aos quatro, não substitui referência alguma; referência abreviada, ausente, trocada
    // entre itens ou repetida em dois itens reprova. Devolve as referências não informadas.
    internal static string[] OrigensNaoInformadas(string resposta, IReadOnlyList<(string Linha, string Referencia)> itens)
    {
        var todas = itens.Select(i => i.Referencia).ToArray();
        if (itens.Any(i => i.Linha.Length == 0 || i.Referencia.Length == 0) || todas.Distinct(StringComparer.Ordinal).Count() != todas.Length) return todas;
        static IEnumerable<int> Ocorrencias(string texto, string valor)
        {
            for (var p = texto.IndexOf(valor, StringComparison.Ordinal); p >= 0; p = texto.IndexOf(valor, p + 1, StringComparison.Ordinal)) yield return p;
        }
        var linhas = itens.SelectMany(i => Ocorrencias(resposta, i.Linha).Select(p => (Fim: p + i.Linha.Length, i.Linha))).OrderBy(x => x.Fim).ToArray();
        return itens.Where(i => !Ocorrencias(resposta, i.Referencia).Any(p => linhas.LastOrDefault(l => l.Fim <= p).Linha == i.Linha))
            .Select(i => i.Referencia).ToArray();
    }

    [EnsaioIndependente]
    [InlineData(1)]
    public async Task FundamentoFuncionaComClaudeRealERetomadaIndependente(int repeticao)
    {
        var admin = new Npgsql.NpgsqlConnectionStringBuilder(Banco.Administrativa);
        Assert.Equal("127.0.0.1", admin.Host); Assert.Equal(55433, admin.Port); Assert.Equal("postgres", admin.Database);
        var raiz = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "dante-tests", "issue-147-validacao",
            $"{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-fundamento-r{repeticao}");
        Directory.CreateDirectory(raiz, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var arquivos = new Dictionary<string, string>(StringComparer.Ordinal);
        void Gravar(string nome, string texto) { var c = Path.Combine(raiz, nome); File.WriteAllText(c, texto, new UTF8Encoding(false)); arquivos[nome] = c; }
        void GravarJson(string nome, object valor) => Gravar(nome, JsonSerializer.Serialize(valor, Json));

        var marcador = "fundamento147" + Guid.NewGuid().ToString("N")[..12];
        var markdown = EvidenciaDoFundamento(marcador);
        await using var banco = await Banco.CriarAsync();
        var privadoA = Directory.CreateTempSubdirectory("dante147f-a-").FullName;
        var privadoB = Directory.CreateTempSubdirectory("dante147f-b-").FullName;
        using var app = BrainMcpTests.CriarApp(banco.ConnectionString);
        var brain = app.GetRequiredService<TelegramBrain>(); await BrainMcpTests.SelecionarAsync(brain);
        using var ferramentas = BrainMcpTests.Provedor(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(25)); var ct = timeout.Token;
        var rastroA = new RastroDeFerramentas147(); var rastroB = new RastroDeFerramentas147();
        var chamadasA = new List<string>(); var chamadasB = new List<string>();
        var launcherA = new LancadorSomenteBrain(privadoA, new()); var launcherB = new LancadorSomenteBrain(privadoB, new());
        var verificacoes = new Dictionary<string, bool>(StringComparer.Ordinal); var detalhes = new Dictionary<string, object?>(StringComparer.Ordinal);
        void Verificar(string nome, bool ok, object? detalhe = null) { verificacoes[nome] = ok; if (detalhe is not null) detalhes[nome] = detalhe; }
        try
        {
            Gravar("evidencia.md", markdown);
            // ---------- Sessão A: captura com fundamento pelo agente real ----------
            var pedidoA =
                "Validação funcional do Brain (Issue #147). Registre o Markdown abaixo como evidência bruta com brain_registrar_evidencia, preservando exatamente o texto, " +
                "tags teste-147 e sensibilidade Pessoal. Depois capture quatro candidatos separados, natureza ConclusaoDoAgente, tags teste-147, sensibilidade Pessoal: " +
                "um Incidente, uma Solucao, uma Nota com a restrição e um Procedimento com o próximo passo. Cada captura deve usar o campo fundamento com a referência " +
                "fonte:<id>@<revisão> devolvida pelo registro da evidência e, em citacao, as linhas inteiras da seção correspondente copiadas literalmente, incluindo a " +
                "linha de cabeçalho da seção; a citação do incidente começa na primeira linha do documento. O conteúdo de cada candidato precisa conter literalmente as " +
                "linhas citadas. Não calcule intervalos, hashes nem referências de trecho: o servidor faz isso. Vincule o incidente e a solução com brain_vincular_candidatos. " +
                "Não confirme nada; o operador fará a autorização no Telegram. Ao final, liste os IDs e revisões dos candidatos.\n\n" + markdown;
            var mensagemA = BrainMcpTests.Mensagem(pedidoA, 1000);
            var servidoresA = await ferramentas.ForSessionAsync("147FA", new(42, AgentKind.Claude, Dante.Application.Contextos.JobExecutionContext.General(privadoA), BrainConversation: mensagemA.ParaFerramentas()), ct);
            Assert.Single(servidoresA);
            string respostaA1, respostaA2;
            await using (var a = new ClaudeSessionDriver(launcherA))
            {
                await a.StartAsync(new(privadoA, IsGeneral: true, Profile: AgentPermissionProfile.Manual, ToolServers: servidoresA), ct);
                await using var eventos = a.ReadEventsAsync(ct).GetAsyncEnumerator();
                Gravar("entrada-a1.txt", pedidoA); await a.StartTurnAsync(pedidoA, ct);
                respostaA1 = (await RespostaAsync(a, eventos, chamadasA, true, ct, rastroA)).Texto; Gravar("resposta-a1.txt", respostaA1);

                // Operador simulado, negócio real: autoriza o registro de cada candidato pendente.
                var autorizacoes = new List<string>();
                for (var n = 0; n < 4; n++)
                {
                    autorizacoes.Add(await brain.AtenderAsync(BrainMcpTests.Mensagem("listar candidatos", 1100 + 2 * n), "listar candidatos") ?? "");
                    autorizacoes.Add(await brain.AtenderAsync(BrainMcpTests.Mensagem("confirmar primeira", 1101 + 2 * n), "confirmar primeira") ?? "");
                }
                GravarJson("autorizacoes-operador.json", autorizacoes);

                string refs;
                await using (var db = banco.Contexto())
                    refs = string.Join(", ", (await db.Conhecimentos.AsNoTracking().ToListAsync(ct)).Select(x => $"conhecimento:{x.Id}@{x.Revisao}")
                        .Concat((await db.Set<DocumentoFonte>().AsNoTracking().ToListAsync(ct)).Select(f => $"fonte:{f.Id}@{f.Revisao}")));
                var pedidoA2 = "Atualize o snapshot operacional com brain_atualizar_contexto_de_trabalho: objetivo (validar o fundamento da PR #292, ensaio " + marcador +
                    "), tarefa, pendências e próximos passos. Em referencias, cite exatamente estes registros já consolidados, sem recontá-los em progresso ou " +
                    "ultimo_resultado: " + refs + ". Registre em progresso só o que ainda não estiver em nenhum desses registros, ou omita o campo. Não grave transcript. " +
                    "Informe o identificador do ensaio na resposta final.";
                var mensagemA2 = BrainMcpTests.Mensagem(pedidoA2, 1200);
                ferramentas.BeginTurn("147FA", new AgentInput(pedidoA2, []) { BrainConversation = mensagemA2.ParaFerramentas() });
                Gravar("entrada-a2.txt", pedidoA2); await a.StartTurnAsync(pedidoA2, ct);
                respostaA2 = (await RespostaAsync(a, eventos, chamadasA, true, ct, rastroA)).Texto; Gravar("resposta-a2.txt", respostaA2);
            }
            await launcherA.ConfirmarEncerramentoAsync(ct); ferramentas.EndSession("147FA");
            Directory.Delete(privadoA, true);
            GravarJson("rastro-a.json", rastroA);

            // ---------- Verificação da captura e das referências ----------
            DocumentoFonte fonte; List<Conhecimento> conhecimentos; List<RelacaoDeConhecimento> relacoes; ContextoDeTrabalho? snapshotA;
            await using (var db = banco.Contexto())
            {
                fonte = Assert.Single(await db.Set<DocumentoFonte>().AsNoTracking().ToListAsync(ct));
                conhecimentos = await db.Conhecimentos.AsNoTracking().ToListAsync(ct);
                relacoes = await db.RelacoesDeConhecimento.AsNoTracking().ToListAsync(ct);
                snapshotA = await db.Set<ContextoDeTrabalho>().AsNoTracking().SingleOrDefaultAsync(ct);
            }
            Verificar("fonte_literal", fonte.Conteudo == markdown && fonte.Autoria == AutoriaDaFonte.Agente && fonte.Revisao == 1);
            Verificar("hash_do_documento", fonte.Hash == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fonte.Conteudo))), fonte.Hash);
            var runas = fonte.Conteudo.EnumerateRunes().ToArray();
            var referencias = new List<object>(); var fundamentados = new List<Conhecimento>();
            foreach (var k in conhecimentos)
            {
                var p = k.Proveniencia; var r = ReferenciaDeTrecho.Ler(p.ReferenciaDaFonte);
                var citado = r?.Inicio is { } ini ? string.Concat(runas.Skip(ini).Take(r.Fim!.Value - ini)) : null;
                var ok = r is { Inicio: not null } && r.IdDocumento == fonte.Id && r.Revisao == fonte.Revisao && r.HashDoDocumento == fonte.Hash &&
                    r.Texto == p.ReferenciaDaFonte && citado == p.TrechoDaFonte && r.Sha256 == ReferenciaDeTrecho.HashDoTexto(citado!) &&
                    p.RevisaoDaFonte == fonte.Hash && (r.Inicio == 0 || runas[r.Inicio.Value - 1].Value == '\n') &&
                    (r.Fim == runas.Length || runas[r.Fim!.Value - 1].Value == '\n') &&
                    SelecaoDeContexto.Normalizar(k.Conteudo).Contains(SelecaoDeContexto.Normalizar(citado!), StringComparison.Ordinal);
                if (ok) fundamentados.Add(k);
                referencias.Add(new { k.Id, k.Revisao, tipo = k.Tipo.ToString(), status = k.Status.ToString(), natureza = k.NaturezaDeOrigem.ToString(),
                    k.Conteudo, k.DadosEstruturados, p.Origem, p.ReferenciaDaFonte, p.RevisaoDaFonte, p.TrechoDaFonte, verificada = ok });
            }
            GravarJson("referencias.json", referencias);
            Verificar("quatro_capturas_com_fundamento", conhecimentos.Count == 4 && fundamentados.Count == 4);
            Verificar("todas_inferidas", conhecimentos.All(k => k.NaturezaDeOrigem == NaturezaDoConteudo.ConclusaoDoAgente && k.Status == StatusDoConhecimento.Inferido));
            Verificar("papeis_cobertos", Linhas.All(l => fundamentados.Any(k => k.Proveniencia.TrechoDaFonte!.Contains(l.Linha, StringComparison.Ordinal))));
            Verificar("origem_rastreia_conversa", fundamentados.All(k => k.Proveniencia.Origem.Contains(":147FA;conversa:55:0:1000", StringComparison.Ordinal)));
            // Informativo: a descrição da chamada traz os argumentos truncados em 800 caracteres. A garantia estrutural é o
            // schema (campo extra em fundamento ou no topo é recusado) e a proveniência gravada, verificada acima.
            detalhes["capturas_aceitas_com_fundamento_na_descricao"] = rastroA.Chamadas.Count(c => c.Nome == "brain_capturar_conhecimento" && c.Sucesso == true &&
                c.Descricao.Contains("fundamento", StringComparison.Ordinal));
            detalhes["capturas_recusadas"] = rastroA.Chamadas.Where(c => c.Nome == "brain_capturar_conhecimento" && c.Sucesso != true).Select(c => c.Saida).ToArray();
            var incidente = conhecimentos.SingleOrDefault(k => k.Tipo == TipoDeConhecimento.Incidente);
            var solucao = conhecimentos.SingleOrDefault(k => k.Tipo == TipoDeConhecimento.Solucao);
            Verificar("relacao_resolvido_por", incidente is not null && solucao is not null &&
                relacoes.Any(r => r.IdOrigem == incidente.Id && r.IdDestino == solucao.Id && r.Tipo == TipoDeRelacao.ResolvidoPor));
            Verificar("snapshot_gravado_pelo_agente", snapshotA is not null && fundamentados.All(k => snapshotA.Dados.Referencias.Contains($"conhecimento:{k.Id}@{k.Revisao}")) &&
                snapshotA.Dados.Referencias.Contains($"fonte:{fonte.Id}@{fonte.Revisao}"), snapshotA?.Dados);
            Verificar("snapshot_sem_recontar_registros", snapshotA is not null && Linhas.All(l =>
                !snapshotA.Dados.Progresso.Contains(l.Linha, StringComparison.Ordinal) && !snapshotA.Dados.UltimoResultado.Contains(l.Linha, StringComparison.Ordinal)));
            string estadoAntesDeB; await using (var db = banco.Contexto()) estadoAntesDeB = await RestauracaoDoBrain147.EstadoPersistidoAsync(db, ct);
            Gravar("estado-antes-de-b.json", estadoAntesDeB);

            // ---------- Sessão B: retomada independente, somente pelo Brain ----------
            Assert.Empty(Directory.EnumerateFileSystemEntries(privadoB));
            var pedidoB = "Retome a validação funcional do fundamento do Brain (Issue #147). Use exclusivamente o Brain. Informe, copiando literalmente dos registros, " +
                "o incidente, a solução, a restrição e o próximo passo. Para cada um, consulte a origem com brain_mostrar_origem e informe, logo depois do texto copiado, a referência da fonte registrada completa e literal, sem abreviar. " +
                "Consulte as relações do incidente com brain_listar_relacoes. Separe fonte bruta, fato confirmado e conclusão do agente e inclua o identificador do ensaio.";
            var mensagemB = BrainMcpTests.Mensagem(pedidoB, 1300);
            var servidoresB = await ferramentas.ForSessionAsync("147FB", new(42, AgentKind.Claude, Dante.Application.Contextos.JobExecutionContext.General(privadoB), BrainConversation: mensagemB.ParaFerramentas()), ct);
            var contexto = await brain.PrepararAsync(mensagemB, "147FB", pedidoB, ct);
            Assert.NotNull(contexto);
            Gravar("pacote-b.txt", contexto.Pacote.TextoParaInjecao); Gravar("entrada-b.txt", contexto.Texto);
            var itemFonte = contexto.Pacote.Itens.SingleOrDefault(x => x.Origem == "fonte_bruta" && x.Id == fonte.Id);
            Verificar("pacote_b_fonte_omitida_com_prova", itemFonte?.ConsolidadaEm is { Count: 4 } && !contexto.Pacote.TextoParaInjecao.Contains(markdown, StringComparison.Ordinal) &&
                contexto.Pacote.TextoParaInjecao.Contains($"fonte:{fonte.Id:D}@1", StringComparison.Ordinal), itemFonte?.ConsolidadaEm);
            Verificar("pacote_b_linhas_preservadas", markdown.Split('\n').All(l => contexto.Pacote.TextoParaInjecao.Contains(JsonEncodedText.Encode(l, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString(), StringComparison.Ordinal)));
            Verificar("pedido_b_sem_evidencia", !pedidoB.Contains(Linhas[0].Linha, StringComparison.Ordinal) && !pedidoB.Contains(marcador, StringComparison.Ordinal));
            string respostaB;
            await using (var b = new ClaudeSessionDriver(launcherB))
            {
                await b.StartAsync(new(privadoB, IsGeneral: true, Profile: AgentPermissionProfile.Manual, ToolServers: servidoresB), ct);
                await using var eventos = b.ReadEventsAsync(ct).GetAsyncEnumerator();
                await b.StartTurnAsync(contexto.Texto, ct); await brain.RegistrarInjecaoAsync(contexto, ct);
                respostaB = (await RespostaAsync(b, eventos, chamadasB, false, ct, rastroB)).Texto; Gravar("resposta-b.txt", respostaB);
            }
            await launcherB.ConfirmarEncerramentoAsync(ct); ferramentas.EndSession("147FB"); Directory.Delete(privadoB, true);
            GravarJson("rastro-b.json", rastroB);
            string estadoDepoisDeB; await using (var db = banco.Contexto()) estadoDepoisDeB = await RestauracaoDoBrain147.EstadoPersistidoAsync(db, ct);
            foreach (var (papel, linha) in Linhas) Verificar("b_recupera_" + papel, respostaB.Contains(linha, StringComparison.Ordinal));
            Verificar("b_identificador", respostaB.Contains(marcador, StringComparison.Ordinal));
            Verificar("b_origem_consultada", fundamentados.All(k => rastroB.ConsultaComprovada("brain_mostrar_origem", k.Id.ToString(), k.Proveniencia.ReferenciaDaFonte!)));
            var origensB = fundamentados.Select(k => (Linha: Linhas.Where(l => k.Proveniencia.TrechoDaFonte!.Contains(l.Linha, StringComparison.Ordinal))
                .Select(l => l.Linha).Take(2).ToArray() is [var unica] ? unica : "", Referencia: k.Proveniencia.ReferenciaDaFonte ?? "")).ToArray();
            var origensNaoInformadas = OrigensNaoInformadas(respostaB, origensB);
            Verificar("b_origem_informada", fundamentados.Count == 4 && origensNaoInformadas.Length == 0, origensNaoInformadas);
            Verificar("b_relacoes_consultadas", incidente is not null && solucao is not null && rastroB.ConsultaComprovada("brain_listar_relacoes", incidente.Id.ToString(), solucao.Id.ToString()));
            Verificar("b_relacao_informada", respostaB.Contains("ResolvidoPor", StringComparison.Ordinal));
            Verificar("b_somente_leitura", rastroB.Violacoes.Count == 0 && estadoAntesDeB == estadoDepoisDeB, rastroB.Violacoes);

            // ---------- Segurança: mesmo canal MCP de produção, sessão própria ----------
            var registroOps = app.GetRequiredService<RegistroDeOperacoesBrain>(); var mensagemV = BrainMcpTests.Mensagem("validação determinística", 1400);
            registroOps.RegistrarSessao("147FV", mensagemV);
            var escopo = (await brain.ResolverEscopoMcpAsync(mensagemV, ct))!; var operacoes = app.GetRequiredService<OperacoesMcpDoBrain>();
            Task<object?> Operar(string ferramenta, object args) => operacoes.ExecutarAsync(escopo, mensagemV, "147FV", "claude", ferramenta, JsonSerializer.SerializeToElement(args), ct);
            async Task<string> Erro(object fundamento, string conteudo)
            {
                try { await Operar("brain_capturar_conhecimento", new { conteudo, natureza = "ConclusaoDoAgente", justificativa = "validação", fundamento }); return "aceito"; }
                catch (ErroDoBrainMcp e) { return ErrosDoBrainMcp.Codigo(e.Categoria); }
            }
            var refFonte = $"fonte:{fonte.Id:D}@1"; var linhaSol = Linhas[1].Linha;
            Guid foraDoEscopo;
            await using (var db = banco.Contexto())
            {
                var outro = new Projeto(escopo.Acesso.IdEspacoDeConhecimento, "Fora do escopo 147"); db.Add(outro); await db.SaveChangesAsync(ct);
                var f2 = new DocumentoFonte(escopo.Acesso.IdEspacoDeConhecimento, outro.Id, "nota:fora-147", "markdown", linhaSol, AutoriaDaFonte.Agente, Sensibilidade.Pessoal,
                    escopo.Acesso.IdUsuario, DateTimeOffset.UtcNow);
                db.Add(f2); await db.SaveChangesAsync(ct); foraDoEscopo = f2.Id;
            }
            var seguranca = new Dictionary<string, string>
            {
                ["fonte_inexistente"] = await Erro(new { fonte = $"fonte:{Guid.NewGuid():D}@1", citacao = linhaSol }, linhaSol),
                ["revisao_desatualizada"] = await Erro(new { fonte = $"fonte:{fonte.Id:D}@2", citacao = linhaSol }, linhaSol),
                ["citacao_inexistente"] = await Erro(new { fonte = refFonte, citacao = "Linha que não está na fonte." }, "Linha que não está na fonte."),
                ["citacao_ambigua"] = await Erro(new { fonte = refFonte, citacao = Situacao }, Situacao),
                ["citacao_fora_do_conteudo"] = await Erro(new { fonte = refFonte, citacao = linhaSol }, "Paráfrase da solução."),
                ["citacao_meia_linha"] = await Erro(new { fonte = refFonte, citacao = "harness controlado" }, "harness controlado"),
                ["fonte_fora_do_escopo"] = await Erro(new { fonte = $"fonte:{foraDoEscopo:D}@1", citacao = linhaSol }, linhaSol),
            };
            var semFundamento = JsonSerializer.SerializeToElement(await Operar("brain_capturar_conhecimento", new { conteudo = "Nota sem fundamento do ensaio " + marcador,
                natureza = "ConclusaoDoAgente", justificativa = "fluxo anterior" }));
            seguranca["sem_fundamento_fluxo_anterior"] = semFundamento.GetProperty("candidato").GetProperty("estado").GetString() == "Pendente" ? "aceito" : "falhou";
            GravarJson("seguranca.json", seguranca);
            Verificar("seguranca", seguranca["fonte_inexistente"] == "nao_encontrado_ou_nao_autorizado" && seguranca["revisao_desatualizada"] == "revisao_desatualizada" &&
                seguranca["citacao_inexistente"] == "entrada_invalida" && seguranca["citacao_ambigua"] == "entrada_invalida" &&
                seguranca["citacao_fora_do_conteudo"] == "entrada_invalida" && seguranca["citacao_meia_linha"] == "entrada_invalida" &&
                seguranca["fonte_fora_do_escopo"] == "nao_encontrado_ou_nao_autorizado" && seguranca["sem_fundamento_fluxo_anterior"] == "aceito", seguranca);

            // ---------- Consolidação: cobertura parcial mantém a fonte integral ----------
            using (var s = EscoposBrainDeTeste.Criar(app, escopo.Acesso.IdUsuario, escopo.Acesso.IdEspacoDeConhecimento, escopo.Acesso.IdProjeto))
            {
                var construtor = s.ServiceProvider.GetRequiredService<ConstrutorDeContextoAppService>();
                PedidoDeContextoDto Pedido(IReadOnlyDictionary<string, int> ja) => new() { Mensagem = "consolidação 147", Filtros = new() { Texto = "marcadorsemcorrespondencia147" },
                    OrcamentoDeTokens = 30000, LimiteDeItens = 20, JaInjetados = ja };
                var integral = await construtor.ConstruirAsync(escopo.Acesso, Pedido(new Dictionary<string, int>()), ct);
                var excluido = fundamentados[0];
                var parcial = await construtor.ConstruirAsync(escopo.Acesso, Pedido(new Dictionary<string, int> { [$"conhecimento:{excluido.Id:D}"] = excluido.Revisao }), ct);
                var fi = integral.Itens.SingleOrDefault(x => x.Id == fonte.Id); var fp = parcial.Itens.SingleOrDefault(x => x.Id == fonte.Id);
                Gravar("pacote-cobertura-integral.txt", integral.TextoParaInjecao); Gravar("pacote-cobertura-parcial.txt", parcial.TextoParaInjecao);
                Verificar("consolidacao_integral_omite", fi?.ConsolidadaEm is { Count: 4 });
                Verificar("consolidacao_parcial_mantem_fonte", fp is not null && fp.ConsolidadaEm is null && fp.Conteudo == markdown);
            }

            // ---------- Snapshot: omitir preserva, "" limpa, objetivo/tarefa obrigatórios ----------
            var atual = JsonSerializer.SerializeToElement(await Operar("brain_obter_contexto_de_trabalho", new { }));
            var rev = atual.GetProperty("Revisao").GetInt32(); var dados = atual.GetProperty("Dados");
            await Operar("brain_atualizar_contexto_de_trabalho", new { revisao = rev, objetivo = dados.GetProperty("Objetivo").GetString(), tarefa = dados.GetProperty("Tarefa").GetString(),
                progresso = "progresso de validação ainda não consolidado", ultimo_resultado = "resultado de validação" });
            await Operar("brain_atualizar_contexto_de_trabalho", new { revisao = rev + 1, objetivo = dados.GetProperty("Objetivo").GetString(), tarefa = "tarefa ajustada" });
            var preservado = JsonSerializer.SerializeToElement(await Operar("brain_obter_contexto_de_trabalho", new { })).GetProperty("Dados");
            await Operar("brain_atualizar_contexto_de_trabalho", new { revisao = rev + 2, objetivo = dados.GetProperty("Objetivo").GetString(), tarefa = "tarefa ajustada", progresso = "", ultimo_resultado = "" });
            var limpo = JsonSerializer.SerializeToElement(await Operar("brain_obter_contexto_de_trabalho", new { })).GetProperty("Dados");
            string Obrigatorio(object args) { try { Operar("brain_atualizar_contexto_de_trabalho", args).GetAwaiter().GetResult(); return "aceito"; } catch (ErroDoBrainMcp e) { return ErrosDoBrainMcp.Codigo(e.Categoria); } catch (ArgumentException) { return "schema"; } }
            var semObjetivo = Obrigatorio(new { revisao = rev + 3, objetivo = "", tarefa = "t" });
            var semTarefa = Obrigatorio(new { revisao = rev + 3, objetivo = "o" });
            var snapshot = new { preservadoProgresso = preservado.GetProperty("Progresso").GetString(), preservadoUltimo = preservado.GetProperty("UltimoResultado").GetString(),
                limpoProgresso = limpo.GetProperty("Progresso").GetString(), limpoUltimo = limpo.GetProperty("UltimoResultado").GetString(), semObjetivo, semTarefa,
                referenciasPreservadas = limpo.GetProperty("Referencias").GetArrayLength() };
            GravarJson("snapshot.json", snapshot);
            Verificar("snapshot_omitir_preserva", snapshot.preservadoProgresso == "progresso de validação ainda não consolidado" && snapshot.preservadoUltimo == "resultado de validação");
            Verificar("snapshot_vazio_limpa", snapshot.limpoProgresso == "" && snapshot.limpoUltimo == "");
            Verificar("snapshot_objetivo_tarefa_obrigatorios", semObjetivo != "aceito" && semTarefa != "aceito");
        }
        finally
        {
            var (commit, limpa) = RaizDeEvidencias147.EstadoGit();
            GravarJson("resultado.json", new { commit, arvoreLimpa = limpa, banco = banco.Nome, marcador, verificacoes, detalhes, chamadasA, chamadasB });
            var somas = arquivos.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(x.Value)))}  {x.Key}");
            File.WriteAllText(Path.Combine(raiz, "SHA256SUMS"), string.Join("\n", somas) + "\n");
            output.WriteLine("Evidências: " + raiz);
            output.WriteLine(JsonSerializer.Serialize(verificacoes, Json));
            if (Directory.Exists(privadoA)) Directory.Delete(privadoA, true);
            if (Directory.Exists(privadoB)) Directory.Delete(privadoB, true);
        }
        var falhas = verificacoes.Where(x => !x.Value).Select(x => x.Key).ToArray();
        Assert.True(falhas.Length == 0, "Verificações reprovadas: " + string.Join(", ", falhas));
    }
}
