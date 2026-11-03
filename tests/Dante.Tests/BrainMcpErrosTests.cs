using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dante.Application.Comum;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.Projetos;
using Dante.Domain.RelacoesDeConhecimento;
using Dante.Infrastructure.Data;
using Dante.Infrastructure.Persistence;
using Dante.Worker.Brain;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ambiente = Dante.Tests.BrainMcpConhecimentoTests.Ambiente;

namespace Dante.Tests;

// #251: erros MCP classificados por categoria, sem oráculo de enumeração.
public sealed class BrainMcpErrosTests
{
    private static readonly string[] Codigos = ["entrada_invalida", "revisao_desatualizada", "nao_encontrado_ou_nao_autorizado", "escopo_mudou", "indisponivel"];
    private static readonly Regex Guid_ = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

    public static TheoryData<Exception, CategoriaDeErroMcp> Excecoes => new()
    {
        { new ErroDoBrainMcp(CategoriaDeErroMcp.EscopoMudou), CategoriaDeErroMcp.EscopoMudou },
        { new ArgumentException("Tipos dos itens não correspondem à semântica da relação."), CategoriaDeErroMcp.EntradaInvalida },
        { new ArgumentOutOfRangeException("limite"), CategoriaDeErroMcp.EntradaInvalida },
        { new JsonException("json"), CategoriaDeErroMcp.EntradaInvalida },
        { new KeyNotFoundException("campo"), CategoriaDeErroMcp.EntradaInvalida },
        { new ConflitoDeConcorrenciaException(), CategoriaDeErroMcp.RevisaoDesatualizada },
        // Revisão divergente lançada de fato pelo Domain (candidato com revisão errada).
        { RevisaoDoDominio(), CategoriaDeErroMcp.RevisaoDesatualizada },
        // Acessor de JsonElement com tipo errado: entrada, não revisão.
        { Lancada(() => JsonSerializer.SerializeToElement(5).GetString()), CategoriaDeErroMcp.EntradaInvalida },
        // InvalidOperationException fora do Domain/Application (EF, Npgsql, LINQ) é bug/infraestrutura, não revisão.
        { Lancada(() => Array.Empty<int>().Single()), CategoriaDeErroMcp.Indisponivel },
        { new InvalidOperationException("A second operation was started on this context instance."), CategoriaDeErroMcp.Indisponivel },
        { new ObjectDisposedException("DanteDbContext"), CategoriaDeErroMcp.Indisponivel },
        { new UnauthorizedAccessException("Conhecimento fora do escopo autorizado."), CategoriaDeErroMcp.NaoEncontradoOuNaoAutorizado },
        { new NpgsqlException("conexão recusada"), CategoriaDeErroMcp.Indisponivel },
        // EF embrulha falha transitória do driver em InvalidOperationException: a causa de infraestrutura vence.
        { new InvalidOperationException("transient", new NpgsqlException("conexão recusada")), CategoriaDeErroMcp.Indisponivel },
        { new DbUpdateException("gravação", new NpgsqlException("conexão recusada")), CategoriaDeErroMcp.Indisponivel },
        { new TimeoutException(), CategoriaDeErroMcp.Indisponivel },
        { new OperationCanceledException(), CategoriaDeErroMcp.Indisponivel },
        { new IOException("pipe"), CategoriaDeErroMcp.Indisponivel },
        { new NullReferenceException(), CategoriaDeErroMcp.Indisponivel },
        { new Exception("inesperado"), CategoriaDeErroMcp.Indisponivel }
    };

    [Theory]
    [MemberData(nameof(Excecoes))]
    public void ClassificaPorTipoSemDependerDaMensagem(Exception excecao, CategoriaDeErroMcp esperada) =>
        Assert.Equal(esperada, ErrosDoBrainMcp.Classificar(excecao));

    [Fact]
    public void CodigosSaoConjuntoFechadoEstavel()
    {
        Assert.Equal(Codigos, Enum.GetValues<CategoriaDeErroMcp>().Select(ErrosDoBrainMcp.Codigo));
        Assert.Equal(Codigos.Length, Enum.GetValues<CategoriaDeErroMcp>().Select(ErrosDoBrainMcp.Mensagem).Distinct().Count());
    }

    [Fact]
    public void RespostaNaoContemMensagemDeExcecaoStackTraceTabelaNemGuid()
    {
        var id = Guid.NewGuid();
        var internas = new Exception[]
        {
            new UnauthorizedAccessException($"Conhecimento {id} fora do escopo; tabela brain_data.conhecimentos"),
            new ArgumentException($"Candidato {id} não encontrado no escopo."),
            new InvalidOperationException($"Revisão {id} desatualizada em brain_data.candidatos_de_conhecimento"),
            new NpgsqlException($"42P01: relation \"brain_data.conhecimentos\" does not exist ({id})"),
            new Exception($"segredo interno {id}")
        };
        foreach (var lancada in internas)
        {
            Exception capturada;
            try { throw lancada; } catch (Exception ex) { capturada = ex; } // com stack trace preenchido
            // Verifica o JSON bruto e o texto decodificado: o escape de acentos não pode mascarar vazamento.
            var json = ErrosDoBrainMcp.Resultado(capturada).ToJsonString() + ErrosDoBrainMcp.Resultado(capturada)["content"]![0]!["text"]!.GetValue<string>();
            var corpo = Corpo(ErrosDoBrainMcp.Resultado(capturada));
            Assert.True(ErrosDoBrainMcp.Resultado(capturada)["isError"]!.GetValue<bool>());
            Assert.Contains(corpo["erro"]!.GetValue<string>(), Codigos);
            Assert.DoesNotMatch(Guid_, json); Assert.DoesNotContain("brain_data", json); Assert.DoesNotContain("relation", json);
            Assert.DoesNotContain("segredo", json); Assert.DoesNotContain("fora do escopo", json); Assert.DoesNotContain(" at ", json);
            Assert.DoesNotContain(capturada.Message, json);
        }
    }

    [Fact]
    public void NaoEncontradoNuncaLevaDetalheEDetalheSoVemDoAdapter()
    {
        Assert.Null(new ErroDoBrainMcp(CategoriaDeErroMcp.NaoEncontradoOuNaoAutorizado, "existe mas é secreto").Detalhe);
        Assert.Equal(ErrosDoBrainMcp.Resultado(ErroDoBrainMcp.NaoEncontrado()).ToJsonString(),
            ErrosDoBrainMcp.Resultado(new UnauthorizedAccessException("Candidato protegido.")).ToJsonString());
        var comDetalhe = Corpo(ErrosDoBrainMcp.Resultado(ErroDoBrainMcp.Entrada(ErrosDoBrainMcp.DetalheTiposDaRelacao)));
        Assert.Equal("entrada_invalida", comDetalhe["erro"]!.GetValue<string>());
        Assert.Equal(ErrosDoBrainMcp.DetalheTiposDaRelacao, comDetalhe["detalhe"]!.GetValue<string>());
        // Uma ArgumentException genérica é entrada_invalida, mas sem carregar a mensagem interna como detalhe.
        Assert.Null(Corpo(ErrosDoBrainMcp.Resultado(new ArgumentException("interna")))["detalhe"]);
    }

    [Fact]
    public async Task ConfirmacaoNoTelegramInformaACausaClassificada()
    {
        var casos = new (Exception Falha, string Esperado)[]
        {
            // Evidência da #249: incompatibilidade semântica não pode virar "escopo, revisão ou disponibilidade".
            (new ArgumentException("Tipos dos itens não correspondem à semântica da relação."), "não é válida para os itens escolhidos"),
            (RevisaoDoDominio(), "o item mudou"),
            (new ConflitoDeConcorrenciaException(), "o item mudou"),
            (ErroDoBrainMcp.NaoEncontrado(), "não encontrado ou não autorizado"),
            (ErroDoBrainMcp.EscopoMudou(), "o escopo ou a conversa mudou"),
            (new NpgsqlException("conexão recusada"), "indisponível")
        };
        var mensagens = new List<string>();
        foreach (var (falha, esperado) in casos)
        {
            var registro = new RegistroDeOperacoesBrain(); var m = BrainMcpTests.Mensagem("pedido");
            registro.RegistrarSessao("S1", m);
            // Tarefa falha, como numa closure async real: o await preserva o throw site original.
            registro.Propor(m, "S1", (_, _) => Task.FromException<string>(falha));
            var resposta = (await registro.AtenderAsync(m, "confirmar", default))!;
            Assert.StartsWith("A operação MCP não foi confirmada", resposta); Assert.Contains(esperado, resposta);
            Assert.DoesNotContain(falha.Message, resposta); mensagens.Add(resposta);
        }
        Assert.DoesNotContain("disponibilidade", mensagens[0]); Assert.DoesNotContain("escopo", mensagens[0]);
    }

    [Fact]
    public async Task StdioClassificaSchemaJsonEWorkerInacessivel()
    {
        var entrada = string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"brain_obter_escopo","arguments":{"tenant":"outro"}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"brain_inexistente","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"brain_obter_escopo","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"metodo/inexistente"}""",
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"brain_obter_escopo","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":5,"arguments":{}}}""",
            """{"jsonrpc":"2.0","id":7,"method":1}""");
        using var saida = new StringWriter(); var chamadas = 0;
        await ServidorMcpDoBrain.ExecutarAsync(new StringReader(entrada), saida, (_, _, _) =>
            ++chamadas == 1 ? throw new IOException("pipe fechado") : Task.FromResult(ErrosDoBrainMcp.Resultado(ErroDoBrainMcp.EscopoMudou())));
        var codigos = saida.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Corpo(JsonNode.Parse(x)!["result"]!.AsObject())["erro"]!.GetValue<string>()).ToArray();
        // O código produzido no Worker atravessa o processo stdio sem ser reescrito.
        Assert.Equal(["entrada_invalida", "entrada_invalida", "indisponivel", "entrada_invalida", "escopo_mudou", "entrada_invalida", "entrada_invalida"], codigos);
    }

    [PostgreSqlFact]
    public async Task NaoOraculoInexistenteOutroEscopoSensibilidadeEProtegidoSaoByteAByteIdenticos()
    {
        await using var a = await Ambiente.CriarAsync();
        var acesso = a.Escopo.Acesso;
        var projeto = new Projeto(acesso.IdEspacoDeConhecimento, "Projeto vizinho");
        var visivel = a.Novo("conhecimento visível"); var outroVisivel = a.Novo("outro visível");
        var foraDoProjeto = a.Novo("de outro projeto", projeto: projeto.Id);
        var confidencial = a.Novo("dado confidencial", Sensibilidade.Confidencial); var secreto = a.Novo("conteúdo protegido", Sensibilidade.Secreto);
        var inativo = a.Novo("item inativo"); inativo.Invalidar(1, a.Prova, DateTimeOffset.UtcNow);
        await using (var db = a.Banco.Contexto()) { db.Add(projeto); db.AddRange(visivel, outroVisivel, foraDoProjeto, confidencial, secreto, inativo); await db.SaveChangesAsync(); }
        CandidatoDeConhecimento Candidato(string texto, Sensibilidade classe, Guid? idProjeto = null) => new(acesso.IdEspacoDeConhecimento, idProjeto ?? acesso.IdProjeto,
            TipoDeConhecimento.Nota, texto, classe, NaturezaDoConteudo.ConclusaoDoAgente, ModoDeCaptura.Explicita, "teste", a.Prova, DateTimeOffset.UtcNow);
        var candidatoFora = Candidato("candidato de outro projeto", Sensibilidade.Pessoal, projeto.Id);
        var candidatoConfidencial = Candidato("candidato confidencial", Sensibilidade.Confidencial); var candidatoSecreto = Candidato("candidato secreto", Sensibilidade.Secreto);
        await using (var db = a.Banco.Contexto()) { db.AddRange(candidatoFora, candidatoConfidencial, candidatoSecreto); await db.SaveChangesAsync(); }
        // Outro usuário, com espaço próprio: o mesmo canal não pode distinguir os itens dele dos inexistentes.
        await a.Brain.AtenderAsync(BrainMcpTests.Mensagem("criar espaço Alheio") with { From = new(7) }, "criar espaço Alheio");
        var escopoAlheio = (await a.Brain.ResolverEscopoMcpAsync(BrainMcpTests.Mensagem("pedido") with { From = new(7) }))!;
        var alheio = new Conhecimento(escopoAlheio.Acesso.IdEspacoDeConhecimento, escopoAlheio.Acesso.IdProjeto, TipoDeConhecimento.Fato, "item alheio", null,
            StatusDoConhecimento.Inferido, null, Sensibilidade.Pessoal, null, null, [], a.Prova, DateTimeOffset.UtcNow, NaturezaDoConteudo.DitoPeloUsuario);
        var candidatoAlheio = new CandidatoDeConhecimento(escopoAlheio.Acesso.IdEspacoDeConhecimento, escopoAlheio.Acesso.IdProjeto, TipoDeConhecimento.Nota,
            "candidato alheio", Sensibilidade.Pessoal, NaturezaDoConteudo.ConclusaoDoAgente, ModoDeCaptura.Explicita, "teste", a.Prova, DateTimeOffset.UtcNow);
        await using (var db = a.Banco.Contexto()) { db.AddRange(alheio, candidatoAlheio); await db.SaveChangesAsync(); }

        // Conhecimentos: inexistente, outro projeto, outro usuário, sensibilidade não permitida, conteúdo protegido, inativo,
        // e candidato não visível (protegido) usado onde se espera conhecimento.
        var alvosDeConhecimento = new[] { Guid.NewGuid(), foraDoProjeto.Id, alheio.Id, confidencial.Id, secreto.Id, inativo.Id, candidatoSecreto.Id, candidatoAlheio.Id };
        var ferramentasDeConhecimento = new Func<Guid, (string, object)>[]
        {
            id => ("brain_mostrar_origem", new { id, origem = "conhecimento" }),
            id => ("brain_listar_relacoes", new { id }),
            id => ("brain_atualizar_conhecimento", new { id, revisao = 1, conteudo = "nova versão", natureza = "ConclusaoDoAgente", justificativa = "análise" }),
            id => ("brain_consolidar_duplicatas", new { id, revisao = 1, duplicatas = new[] { new { id = visivel.Id, revisao = 1 } } }),
            id => ("brain_consolidar_duplicatas", new { id = visivel.Id, revisao = 1, duplicatas = new[] { new { id, revisao = 1 } } }),
            id => ("brain_criar_relacao", new { origem = id, destino = visivel.Id, revisao_origem = 1, revisao_destino = 1, tipo = "RelacionadoA" }),
            id => ("brain_criar_relacao", new { origem = visivel.Id, destino = id, revisao_origem = 1, revisao_destino = 1, tipo = "RelacionadoA" })
        };
        // Candidatos: inexistente, outro projeto, outro usuário, confidencial, secreto e conhecimento protegido usado como candidato.
        var alvosDeCandidato = new[] { Guid.NewGuid(), candidatoFora.Id, candidatoAlheio.Id, candidatoConfidencial.Id, candidatoSecreto.Id, secreto.Id, alheio.Id };
        var ferramentasDeCandidato = new Func<Guid, (string, object)>[]
        {
            id => ("brain_mostrar_origem", new { id, origem = "candidato" }),
            id => ("brain_corrigir_candidato", new { id, revisao = 1, conteudo = "correção", natureza = "ConclusaoDoAgente", justificativa = "revisão" }),
            id => ("brain_confirmar_candidato", new { id, revisao = 1 }),
            id => ("brain_cancelar_candidato", new { id, revisao = 1 })
        };
        var respostas = new HashSet<string>();
        foreach (var (ferramentas, alvos) in new[] { (ferramentasDeConhecimento, alvosDeConhecimento), (ferramentasDeCandidato, alvosDeCandidato) })
            foreach (var ferramenta in ferramentas)
                foreach (var alvo in alvos)
                {
                    var (nome, args) = ferramenta(alvo);
                    var json = await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.NaoEncontradoOuNaoAutorizado, () => a.OperarAsync(nome, args));
                    Assert.DoesNotMatch(Guid_, json); respostas.Add(json);
                }
        // Byte a byte: uma única resposta para todas as ferramentas e todos os motivos.
        var unica = Assert.Single(respostas);
        Assert.Equal("nao_encontrado_ou_nao_autorizado", Corpo(JsonNode.Parse(unica)!.AsObject())["erro"]!.GetValue<string>());
        Assert.Null(Corpo(JsonNode.Parse(unica)!.AsObject())["detalhe"]);
        Assert.Null(await a.ConfirmarAsync()); // nenhuma proposta ficou pendente
        await using (var db = a.Banco.Contexto())
        {
            Assert.All(await db.Conhecimentos.ToListAsync(), x => Assert.True(x.Revisao <= 2));
            Assert.Empty(await db.RelacoesDeConhecimento.ToListAsync());
        }
    }

    [PostgreSqlFact]
    public async Task IdDoTipoErradoSoEEntradaInvalidaQuandoOItemEVisivel()
    {
        await using var a = await Ambiente.CriarAsync();
        var conhecimento = a.Novo("conhecimento visível"); await a.GravarAsync(conhecimento);
        await a.OperarAsync("brain_capturar_conhecimento", new { conteudo = "candidato visível", natureza = "ConclusaoDoAgente", justificativa = "análise" });
        Guid candidato; await using (var db = a.Banco.Contexto()) candidato = (await db.CandidatosDeConhecimento.SingleAsync()).Id;
        // Caso real da #147: listar relações com o ID de um candidato que o próprio agente acabou de listar.
        foreach (var (nome, args) in new (string, object)[] { ("brain_listar_relacoes", new { id = candidato }), ("brain_mostrar_origem", new { id = candidato, origem = "conhecimento" }),
            ("brain_atualizar_conhecimento", new { id = candidato, revisao = 1, conteudo = "x", natureza = "ConclusaoDoAgente", justificativa = "x" }) })
        {
            var json = await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.EntradaInvalida, () => a.OperarAsync(nome, args));
            Assert.Equal(ErrosDoBrainMcp.DetalheIdDeCandidato, BrainMcpTests.DetalheDe(json)); Assert.DoesNotMatch(Guid_, json);
        }
        foreach (var (nome, args) in new (string, object)[] { ("brain_confirmar_candidato", new { id = conhecimento.Id, revisao = 1 }), ("brain_mostrar_origem", new { id = conhecimento.Id, origem = "candidato" }) })
            Assert.Equal(ErrosDoBrainMcp.DetalheIdDeConhecimento, BrainMcpTests.DetalheDe(await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.EntradaInvalida, () => a.OperarAsync(nome, args))));
    }

    [PostgreSqlFact]
    public async Task RelacaoComTiposIncompativeisERecusadaNaPreparacaoENaConfirmacao()
    {
        await using var a = await Ambiente.CriarAsync();
        var fatoA = a.Novo("fato A"); var fatoB = a.Novo("fato B");
        var incidente = a.Novo("incidente IPv6", tipo: TipoDeConhecimento.Incidente); var solucao = a.Novo("Happy Eyeballs", tipo: TipoDeConhecimento.Solucao);
        await a.GravarAsync(fatoA, fatoB, incidente, solucao);
        var json = await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.EntradaInvalida, () => a.OperarAsync("brain_criar_relacao",
            new { origem = fatoA.Id, destino = fatoB.Id, revisao_origem = 1, revisao_destino = 1, tipo = "ResolvidoPor" }));
        Assert.Equal(ErrosDoBrainMcp.DetalheTiposDaRelacao, BrainMcpTests.DetalheDe(json));
        Assert.Null(await a.ConfirmarAsync()); // recusa antecipada: nada a confirmar
        await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.EntradaInvalida, () => a.OperarAsync("brain_criar_relacao",
            new { origem = fatoA.Id, destino = fatoA.Id, revisao_origem = 1, revisao_destino = 1, tipo = "RelacionadoA" }));
        // Preparação válida; o tipo muda no banco sem nova revisão antes da confirmação (cenário da #249).
        await a.OperarAsync("brain_criar_relacao", new { origem = incidente.Id, destino = solucao.Id, revisao_origem = 1, revisao_destino = 1, tipo = "ResolvidoPor" });
        await using (var db = a.Banco.Contexto())
        {
            var item = await db.Conhecimentos.SingleAsync(x => x.Id == solucao.Id);
            db.Entry(item).Property(x => x.Tipo).CurrentValue = TipoDeConhecimento.Fato; await db.SaveChangesAsync();
        }
        var resposta = await a.ConfirmarAsync();
        Assert.Contains("não é válida para os itens escolhidos", resposta); Assert.DoesNotContain("disponibilidade", resposta);
        await using (var db = a.Banco.Contexto()) Assert.Empty(await db.RelacoesDeConhecimento.ToListAsync());
    }

    [PostgreSqlFact]
    public async Task RevisaoDesatualizadaEmCorrecaoConfirmacaoAtualizacaoConsolidacaoERelacao()
    {
        await using var a = await Ambiente.CriarAsync();
        var destino = a.Novo("destino"); var duplicata = a.Novo("duplicata"); await a.GravarAsync(destino, duplicata);
        await a.OperarAsync("brain_capturar_conhecimento", new { conteudo = "candidato", natureza = "ConclusaoDoAgente", justificativa = "análise" });
        Guid candidato; await using (var db = a.Banco.Contexto()) candidato = (await db.CandidatosDeConhecimento.SingleAsync()).Id;
        var chamadas = new (string, object)[]
        {
            ("brain_corrigir_candidato", new { id = candidato, revisao = 2, conteudo = "corrigido", natureza = "ConclusaoDoAgente", justificativa = "x" }),
            ("brain_confirmar_candidato", new { id = candidato, revisao = 2 }),
            ("brain_cancelar_candidato", new { id = candidato, revisao = 0 }),
            ("brain_atualizar_conhecimento", new { id = destino.Id, revisao = 2, conteudo = "nova", natureza = "ConclusaoDoAgente", justificativa = "x" }),
            ("brain_consolidar_duplicatas", new { id = destino.Id, revisao = 2, duplicatas = new[] { new { id = duplicata.Id, revisao = 1 } } }),
            ("brain_consolidar_duplicatas", new { id = destino.Id, revisao = 1, duplicatas = new[] { new { id = duplicata.Id, revisao = 3 } } }),
            ("brain_criar_relacao", new { origem = destino.Id, destino = duplicata.Id, revisao_origem = 1, revisao_destino = 9, tipo = "RelacionadoA" })
        };
        foreach (var (nome, args) in chamadas)
            await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.RevisaoDesatualizada, () => a.OperarAsync(nome, args));
        // Candidato encerrado: estado não permite a operação, mesma categoria.
        await a.OperarAsync("brain_cancelar_candidato", new { id = candidato, revisao = 1 });
        Assert.Contains("descartado", await a.ConfirmarAsync());
        await BrainMcpTests.RecusaAsync(CategoriaDeErroMcp.RevisaoDesatualizada, () => a.OperarAsync("brain_corrigir_candidato",
            new { id = candidato, revisao = 2, conteudo = "corrigido", natureza = "ConclusaoDoAgente", justificativa = "x" }));
        // Revisão muda entre preparação e confirmação: o Telegram informa a causa.
        await a.OperarAsync("brain_atualizar_conhecimento", new { id = destino.Id, revisao = 1, conteudo = "nova", natureza = "ConclusaoDoAgente", justificativa = "x" });
        await using (var db = a.Banco.Contexto())
        {
            var item = await db.Conhecimentos.SingleAsync(x => x.Id == destino.Id);
            item.Corrigir(1, item.Tipo, "edição concorrente", null, null, item.Sensibilidade, null, null, [], a.Prova, DateTimeOffset.UtcNow, NaturezaDoConteudo.DitoPeloUsuario); await db.SaveChangesAsync();
        }
        Assert.Contains("o item mudou", await a.ConfirmarAsync());
        // Removido antes da confirmação: não encontrado, não "revisão".
        await a.OperarAsync("brain_atualizar_conhecimento", new { id = duplicata.Id, revisao = 1, conteudo = "nova", natureza = "ConclusaoDoAgente", justificativa = "x" });
        await using (var db = a.Banco.Contexto()) { db.Remove(await db.Conhecimentos.SingleAsync(x => x.Id == duplicata.Id)); await db.SaveChangesAsync(); }
        Assert.Contains("não encontrado ou não autorizado", await a.ConfirmarAsync());
    }

    [PostgreSqlFact]
    public async Task CanalProduzCadaCodigoECapturaConcorrenteEFalhaDeBancoSaoClassificadas()
    {
        var falha = new FalhaSimulada();
        await using var a = await Ambiente.CriarAsync(s => s.AddScoped<IUnitOfWork>(p => new UnidadeComFalha(new UnitOfWork(p.GetRequiredService<DanteDbContext>()), falha)));
        using var ferramentas = BrainMcpTests.Provedor(a.App);
        var servidor = Assert.Single(await ferramentas.ForSessionAsync("CANAL", new(42, AgentKind.Codex, JobExecutionContext.General(AppContext.BaseDirectory),
            BrainConversation: BrainMcpTests.Mensagem("pedido").ParaFerramentas())));
        var cliente = await BrainMcpTests.Cliente.ConectarAsync(servidor);
        async Task<string> Codigo(string nome, object args) { var r = await cliente.ChamarAsync(nome, args); Assert.True(r["isError"]!.GetValue<bool>()); return Corpo(r)["erro"]!.GetValue<string>(); }

        Assert.Equal("entrada_invalida", await Codigo("brain_buscar_conhecimento", new { texto = 42 }));
        Assert.Equal("entrada_invalida", await Codigo("brain_capturar_conhecimento", new { conteudo = "token=abcdef123456", natureza = "ConclusaoDoAgente", justificativa = "x" }));
        Assert.Equal("nao_encontrado_ou_nao_autorizado", await Codigo("brain_listar_relacoes", new { id = Guid.NewGuid() }));
        Assert.Equal("revisao_desatualizada", await Codigo("brain_atualizar_contexto_de_trabalho", new { revisao = 3, objetivo = "x", tarefa = "x", progresso = "x", ultimo_resultado = "x" }));
        // Corrida entre capturas equivalentes (UNIQUE/xmin) chega como conflito de revisão, não como indisponibilidade.
        falha.Proxima = () => new ConflitoDeConcorrenciaException();
        Assert.Equal("revisao_desatualizada", await Codigo("brain_capturar_conhecimento", new { conteudo = "concorrente", natureza = "ConclusaoDoAgente", justificativa = "x" }));
        // Banco cai durante a gravação.
        falha.Proxima = () => new NpgsqlException("conexão recusada pelo servidor 10.0.0.5");
        var indisponivel = await cliente.ChamarAsync("brain_capturar_conhecimento", new { conteudo = "durante queda", natureza = "ConclusaoDoAgente", justificativa = "x" });
        Assert.Equal("indisponivel", Corpo(indisponivel)["erro"]!.GetValue<string>()); Assert.DoesNotContain("10.0.0.5", indisponivel.ToJsonString());
        // O canal continua utilizável depois de erros que não são de escopo.
        Assert.False((await cliente.ChamarAsync("brain_obter_escopo", new { }))["isError"]!.GetValue<bool>());
        // Troca de projeto: escopo_mudou e o canal é encerrado (comportamento de revogação preservado).
        await a.Brain.AtenderAsync(BrainMcpTests.Mensagem("criar projeto Outro"), "criar projeto Outro");
        Assert.Equal("escopo_mudou", await Codigo("brain_obter_escopo", new { }));
        await Assert.ThrowsAnyAsync<Exception>(() => cliente.ChamarAsync("brain_obter_escopo", new { }));
        try { await cliente.DisposeAsync(); } catch (IOException) { } // pipe já encerrado pelo Worker
    }

    // Revisão divergente lançada de fato pelo Domain: descartar candidato com revisão errada.
    private static Exception RevisaoDoDominio() => Lancada(() => new CandidatoDeConhecimento(Guid.NewGuid(), null, TipoDeConhecimento.Nota, "conteúdo", Sensibilidade.Pessoal,
        NaturezaDoConteudo.ConclusaoDoAgente, ModoDeCaptura.Explicita, "teste", new ProvenienciaDoConhecimento(Guid.NewGuid(), "teste", "fonte:1", trechoDaFonte: "conteúdo"), DateTimeOffset.UtcNow)
        .Descartar(99, new ProvenienciaDoConhecimento(Guid.NewGuid(), "teste", "fonte:2", trechoDaFonte: "x"), DateTimeOffset.UtcNow));
    private static Exception Lancada(Action acao) { try { acao(); } catch (Exception ex) { return ex; } throw new InvalidOperationException("Ação não lançou."); }
    private static JsonNode Corpo(JsonObject resultado) => JsonNode.Parse(resultado["content"]![0]!["text"]!.GetValue<string>())!;
    private sealed class FalhaSimulada { public Func<Exception>? Proxima { get; set; } }
    private sealed class UnidadeComFalha(IUnitOfWork real, FalhaSimulada falha) : IUnitOfWork
    {
        public Task SalvarAlteracoesAsync(CancellationToken cancellationToken = default)
        {
            if (falha.Proxima is { } criar) { falha.Proxima = null; throw criar(); }
            return real.SalvarAlteracoesAsync(cancellationToken);
        }
    }
}
