using System.Text.Json;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;

namespace Dante.Tests;

// #309 (AD-70), PR 1: domínio das delegações rastreadas. Hierarquia de execuções, contratos tipados do catálogo
// fechado, regras por interseção (nunca ampliação), idempotência pela chave da unidade de trabalho e imutabilidade do
// resultado. Nada aqui executa: o executor, a fila e o MCP são das entregas seguintes.
public sealed class DelegacoesDominioTests
{
    internal static readonly DateTimeOffset Agora = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    internal const string Contexto = """{"modo":"repositorio","alias":"@dante"}""";
    internal const string Pergunta = """{"pergunta":"O índice cobre a consulta por estado?"}""";

    // Solicitante (pesquisa, pode consultar o revisor) e destinatário (revisa), Codex, só leitura, no @dante.
    internal sealed class Cenario
    {
        public Cenario(ConfiguracaoDoAgente? solicitante = null, ConfiguracaoDoAgente? destinatario = null, long numero = 10)
        {
            Solicitante = new Agente("pesquisador", null, null, 1, Agora);
            VersaoSolicitante = Solicitante.NovaVersao(solicitante ?? ConfiguracaoDoSolicitante(), 1, Agora);
            Destinatario = new Agente("revisor", null, null, 1, Agora);
            VersaoDestinataria = Destinatario.NovaVersao(destinatario ?? ConfiguracaoDoDestinatario(), 1, Agora);
            Origem = new Execucao(numero, TipoDeExecucao.Tarefa, "telegram", 1001, Contexto, "Investigar o índice", Agora,
                VersaoSolicitante.Id);
            Etapa = new EtapaDaExecucao(Origem, 1, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica, Agora);
            Origem.Iniciar(Agora);
            Etapa.TornarPronta([]);
            Etapa.Iniciar(Agora);
        }

        public Agente Solicitante { get; }
        public VersaoDoAgente VersaoSolicitante { get; }
        public Agente Destinatario { get; }
        public VersaoDoAgente VersaoDestinataria { get; }
        public Execucao Origem { get; }
        public EtapaDaExecucao Etapa { get; }

        public PedidoDeDelegacao Pedido(string objetivo = "Conferir o índice por estado") => new(Origem, Etapa, Solicitante,
            VersaoSolicitante, Destinatario, VersaoDestinataria, [Solicitante.Id], ["@dante"], 0, 0,
            ContratosDeDelegacao.Consulta.Identificador, Pergunta, objetivo, objetivo, false, "Citar o arquivo da migration.");
    }

    internal static ConfiguracaoDoAgente ConfiguracaoDoSolicitante(IEnumerable<string>? consulta = null,
        LimitesDeDelegacao? limites = null, IEnumerable<string>? repositorios = null) =>
        new(AgentKind.Codex, AgentPermissionProfile.Plan, AgentPermissionProfile.Plan, false,
            capacidades: [CapacidadeDoAgente.Pesquisar], ferramentas: [FerramentaDoDante.Coordenacao],
            repositorios: repositorios ?? ["@dante"], podeConsultar: consulta ?? ["revisor"], limitesDeDelegacao: limites);

    internal static ConfiguracaoDoAgente ConfiguracaoDoDestinatario(IEnumerable<CapacidadeDoAgente>? capacidades = null,
        IEnumerable<string>? repositorios = null) =>
        new(AgentKind.Codex, AgentPermissionProfile.Plan, AgentPermissionProfile.Plan, false,
            capacidades: capacidades ?? [CapacidadeDoAgente.Revisar], repositorios: repositorios ?? ["@dante"]);

    private static MotivoDaRecusaDeDelegacao Recusa(PedidoDeDelegacao pedido) =>
        Assert.Throws<DelegacaoRecusadaException>(() => Delegacao.Criar(pedido, 11, Agora)).Motivo;

    [Fact]
    public void CriaADelegacaoEAExecucaoSubordinadaHerdandoAutoridadeEContexto()
    {
        var c = new Cenario();
        var (delegacao, filha) = Delegacao.Criar(c.Pedido(), 11, Agora);

        Assert.Equal((TipoDeExecucao.Delegada, EstadoDaExecucao.Pendente, "E11"), (filha.Tipo, filha.Estado, filha.Codigo));
        Assert.Equal((c.Origem.Id, c.Origem.Id, 1), (filha.IdExecucaoPai, filha.IdExecucaoRaiz, filha.Profundidade));
        Assert.Equal(("telegram", 1001L, Contexto), (filha.Canal, filha.IdDoUsuarioNoCanal, filha.Contexto));
        Assert.Equal(c.VersaoDestinataria.Id, filha.IdVersaoDoAgente);
        Assert.Equal("Conferir o índice por estado", filha.Objetivo);
        Assert.False(filha.Raiz);

        Assert.Equal(EstadoDaDelegacao.Solicitada, delegacao.Estado);
        Assert.Equal((c.Origem.Id, c.Etapa.Id, filha.Id, c.Origem.Id),
            (delegacao.IdExecucaoOrigem, delegacao.IdEtapaOrigem, delegacao.IdExecucaoSubordinada, delegacao.IdExecucaoRaiz));
        Assert.Equal((c.VersaoSolicitante.HashDaConfiguracao, c.VersaoDestinataria.HashDaConfiguracao),
            (delegacao.HashDaVersaoSolicitante, delegacao.HashDaVersaoDestinataria));
        Assert.Equal(("consulta.v1", "revisor"), (delegacao.Contrato, delegacao.HandleDoDestinatario));
        Assert.Equal("""{"pergunta":"O índice cobre a consulta por estado?","referencias":[]}""",
            JsonSerializer.Serialize(JsonDocument.Parse(delegacao.Entrada).RootElement,
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.Equal(ContratoDeDelegacao.Hash(delegacao.Entrada), delegacao.HashDaEntrada);
        Assert.Equal((30, 60, false), (delegacao.MinutosParaIniciar, delegacao.MinutosDeDuracao, delegacao.Escrita));
        Assert.Equal([c.Solicitante.Id], delegacao.CadeiaDeAgentes);
        Assert.Null(delegacao.Resultado);
        Assert.Matches("^[0-9a-f]{64}$", delegacao.ChaveDeDeduplicacao);
    }

    [Fact]
    public void ExecucaoComumEhRaizEDelegadaSoNasceDeUmaDelegacao()
    {
        var raiz = new Execucao(1, TipoDeExecucao.Avulsa, "telegram", 1, "{}", null, Agora);
        Assert.Equal((raiz.Id, (Guid?)null, 0, true), (raiz.IdExecucaoRaiz, raiz.IdExecucaoPai, raiz.Profundidade, raiz.Raiz));
        Assert.Throws<ArgumentException>(() =>
            new Execucao(2, TipoDeExecucao.Delegada, "telegram", 1, "{}", null, Agora, Guid.NewGuid()));
    }

    [Fact]
    public void AutodelegacaoEhRecusada()
    {
        var c = new Cenario();
        Assert.Equal(MotivoDaRecusaDeDelegacao.Autodelegacao,
            Recusa(c.Pedido() with { Destinatario = c.Solicitante, VersaoDestinataria = c.VersaoSolicitante }));
        // E nem chega a ser concedida: a versão de um agente não o lista entre os que ele consulta.
        Assert.Throws<ArgumentException>(() => c.Solicitante.NovaVersao(ConfiguracaoDoSolicitante(["pesquisador"]), 1, Agora));
    }

    // Raiz (A) delega a B; a filha de B tenta delegar de volta a A (ciclo) ou a C (profundidade acima do teto 1).
    [Fact]
    public void CicloEProfundidadeAcimaDoTetoSaoRecusados()
    {
        var c = new Cenario(destinatario: new ConfiguracaoDoAgente(AgentKind.Codex, AgentPermissionProfile.Plan,
            AgentPermissionProfile.Plan, false, capacidades: [CapacidadeDoAgente.Revisar, CapacidadeDoAgente.Pesquisar],
            ferramentas: [FerramentaDoDante.Coordenacao], repositorios: ["@dante"], podeConsultar: ["pesquisador", "terceiro"]));
        var (_, filha) = Delegacao.Criar(c.Pedido(), 11, Agora);
        var etapaDaFilha = new EtapaDaExecucao(filha, 1, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica, Agora);
        filha.Iniciar(Agora);
        etapaDaFilha.TornarPronta([]);
        etapaDaFilha.Iniciar(Agora);
        var terceiro = new Agente("terceiro", null, null, 1, Agora);
        var versaoDoTerceiro = terceiro.NovaVersao(ConfiguracaoDoDestinatario(), 1, Agora);
        var deVolta = new PedidoDeDelegacao(filha, etapaDaFilha, c.Destinatario, c.VersaoDestinataria, c.Solicitante,
            c.VersaoSolicitante, [c.Solicitante.Id, c.Destinatario.Id], ["@dante"], 0, 1, "consulta.v1", Pergunta, "x", "x", false);

        Assert.Equal(MotivoDaRecusaDeDelegacao.Ciclo, Recusa(deVolta));
        Assert.Equal(MotivoDaRecusaDeDelegacao.ProfundidadeExcedida,
            Recusa(deVolta with { Destinatario = terceiro, VersaoDestinataria = versaoDoTerceiro }));
        // A cadeia informada precisa ser a da origem: sem o solicitante no fim, ou com tamanho errado, é recusada.
        Assert.Equal(MotivoDaRecusaDeDelegacao.OrigemInvalida, Recusa(c.Pedido() with { CadeiaDeAgentes = [Guid.NewGuid()] }));
        Assert.Equal(MotivoDaRecusaDeDelegacao.OrigemInvalida,
            Recusa(c.Pedido() with { CadeiaDeAgentes = [Guid.NewGuid(), c.Solicitante.Id] }));
    }

    [Fact]
    public void FanOutEOrcamentoDaRaizSaoLimitados()
    {
        var c = new Cenario();
        Assert.NotNull(Delegacao.Criar(c.Pedido() with { DelegacoesDaOrigem = 3, DelegacoesDaRaiz = 3 }, 11, Agora));
        Assert.Equal(MotivoDaRecusaDeDelegacao.FanOutExcedido, Recusa(c.Pedido() with { DelegacoesDaOrigem = 4, DelegacoesDaRaiz = 4 }));
        Assert.Equal(MotivoDaRecusaDeDelegacao.OrcamentoExcedido, Recusa(c.Pedido() with { DelegacoesDaOrigem = 0, DelegacoesDaRaiz = 4 }));

        // A versão só restringe: com 2 por execução, a terceira já é recusada.
        var restrito = new Cenario(ConfiguracaoDoSolicitante(limites: new LimitesDeDelegacao(1, 2, 30, 60, 1)));
        Assert.Equal(MotivoDaRecusaDeDelegacao.FanOutExcedido,
            Recusa(restrito.Pedido() with { DelegacoesDaOrigem = 2, DelegacoesDaRaiz = 2 }));
    }

    [Fact]
    public void DestinatarioPrecisaEstarConcedidoAtivoEComVersaoAtualIntegra()
    {
        var c = new Cenario(ConfiguracaoDoSolicitante(["outro"]));
        Assert.Equal(MotivoDaRecusaDeDelegacao.DestinatarioNaoAutorizado, Recusa(c.Pedido()));
        // Consultar alguém exige a concessão coordenacao: a configuração sem ela nem é criada.
        Assert.Throws<ArgumentException>(() => new ConfiguracaoDoAgente(AgentKind.Codex, AgentPermissionProfile.Plan,
            AgentPermissionProfile.Plan, false, podeConsultar: ["revisor"]));

        var desativado = new Cenario();
        desativado.Destinatario.Desativar(Agora);
        Assert.Equal(MotivoDaRecusaDeDelegacao.DestinatarioIndisponivel, Recusa(desativado.Pedido()));

        var editado = new Cenario();
        editado.Destinatario.NovaVersao(ConfiguracaoDoDestinatario([CapacidadeDoAgente.Revisar, CapacidadeDoAgente.Pesquisar]), 1, Agora);
        Assert.Equal(MotivoDaRecusaDeDelegacao.DestinatarioIndisponivel, Recusa(editado.Pedido()));
    }

    [Fact]
    public void CapacidadeDoDestinatarioPrecisaAtenderAoContrato()
    {
        var c = new Cenario(destinatario: ConfiguracaoDoDestinatario([CapacidadeDoAgente.Planejar]));
        Assert.Equal(MotivoDaRecusaDeDelegacao.CapacidadeInsuficiente, Recusa(c.Pedido()));
        var pesquisador = new Cenario(destinatario: ConfiguracaoDoDestinatario([CapacidadeDoAgente.Pesquisar]));
        Assert.NotNull(Delegacao.Criar(pesquisador.Pedido(), 11, Agora));
        Assert.Equal(MotivoDaRecusaDeDelegacao.CapacidadeInsuficiente, Recusa(pesquisador.Pedido() with
        {
            Contrato = "revisao.v1", Entrada = $$"""{"artefato":"{{new string('a', 64)}}"}"""
        }));
    }

    [Fact]
    public void RepositoriosPrecisamEstarNaIntersecaoDosDoisAgentes()
    {
        var c = new Cenario(destinatario: ConfiguracaoDoDestinatario(repositorios: ["@outro"]));
        Assert.Equal(MotivoDaRecusaDeDelegacao.RepositorioForaDaIntersecao, Recusa(c.Pedido()));
        var semOrigem = new Cenario(ConfiguracaoDoSolicitante(repositorios: ["@outro"]));
        Assert.Equal(MotivoDaRecusaDeDelegacao.RepositorioForaDaIntersecao, Recusa(semOrigem.Pedido()));
        // Workspace geral (sem repositório) não depende de concessão de repositório.
        Assert.NotNull(Delegacao.Criar(c.Pedido() with { RepositoriosDaOrigem = [] }, 11, Agora));
    }

    [Fact]
    public void PrazoEDuracaoNaoPassamDosLimitesDoSolicitante()
    {
        var c = new Cenario(ConfiguracaoDoSolicitante(limites: new LimitesDeDelegacao(1, 4, 10, 20, 1)));
        var (delegacao, _) = Delegacao.Criar(c.Pedido(), 11, Agora);
        Assert.Equal((10, 20), (delegacao.MinutosParaIniciar, delegacao.MinutosDeDuracao));
        Assert.Equal(MotivoDaRecusaDeDelegacao.LimitesExcedidos, Recusa(c.Pedido() with { MinutosParaIniciar = 11 }));
        Assert.Equal(MotivoDaRecusaDeDelegacao.LimitesExcedidos, Recusa(c.Pedido() with { MinutosDeDuracao = 21 }));
        Assert.Equal(MotivoDaRecusaDeDelegacao.LimitesExcedidos, Recusa(c.Pedido() with { MinutosDeDuracao = 0 }));
        // Os limites da versão não passam do teto global aprovado (1, 4, 30, 60, 1).
        Assert.Throws<ArgumentOutOfRangeException>(() => ConfiguracaoDoSolicitante(limites: new LimitesDeDelegacao(2, 4, 30, 60, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConfiguracaoDoSolicitante(limites: new LimitesDeDelegacao(1, 5, 30, 60, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConfiguracaoDoSolicitante(limites: new LimitesDeDelegacao(1, 4, 31, 60, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConfiguracaoDoSolicitante(limites: new LimitesDeDelegacao(1, 4, 30, 61, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConfiguracaoDoSolicitante(limites: new LimitesDeDelegacao(1, 4, 30, 60, 2)));
    }

    [Fact]
    public void EscritaEhRecusadaENaoExisteDelegacaoQueEscreva()
    {
        var c = new Cenario();
        Assert.Equal(MotivoDaRecusaDeDelegacao.EscritaNaoAutorizada, Recusa(c.Pedido() with { Escrita = true }));
        Assert.False(Delegacao.Criar(c.Pedido(), 11, Agora).Delegacao.Escrita);
    }

    [Fact]
    public void OrigemPrecisaSerAEtapaDeLeituraEmCursoDeUmaTarefa()
    {
        var c = new Cenario();
        // Etapa de escrita (a sessão de escrita não recebe o dante_coordenacao).
        var escrita = new EtapaDaExecucao(c.Origem, 2, TipoDeEtapa.TurnoDeAgente, true, PoliticaDeRetomada.ComConfirmacao, Agora);
        escrita.TornarPronta([]);
        escrita.Iniciar(Agora);
        Assert.Equal(MotivoDaRecusaDeDelegacao.OrigemInvalida, Recusa(c.Pedido() with { EtapaDeOrigem = escrita }));
        // Etapa que não está em curso.
        var pendente = new EtapaDaExecucao(c.Origem, 3, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica, Agora);
        Assert.Equal(MotivoDaRecusaDeDelegacao.OrigemInvalida, Recusa(c.Pedido() with { EtapaDeOrigem = pendente }));
        // Etapa de outra execução, e execução que não é tarefa (o job legado).
        var outra = new Cenario();
        Assert.Equal(MotivoDaRecusaDeDelegacao.OrigemInvalida, Recusa(c.Pedido() with { EtapaDeOrigem = outra.Etapa }));
        var avulsa = new Execucao(20, TipoDeExecucao.Avulsa, "telegram", 1001, Contexto, null, Agora);
        var etapaAvulsa = new EtapaDaExecucao(avulsa, 1, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica, Agora);
        avulsa.Iniciar(Agora);
        etapaAvulsa.TornarPronta([]);
        etapaAvulsa.Iniciar(Agora);
        Assert.Equal(MotivoDaRecusaDeDelegacao.OrigemInvalida,
            Recusa(c.Pedido() with { Origem = avulsa, EtapaDeOrigem = etapaAvulsa }));
        // Solicitante que não é o agente congelado na origem.
        var estranho = new Cenario();
        Assert.Equal(MotivoDaRecusaDeDelegacao.OrigemInvalida,
            Recusa(c.Pedido() with { Solicitante = estranho.Solicitante, VersaoSolicitante = estranho.VersaoSolicitante }));
    }

    [Theory]
    [InlineData("contrato.v9", """{"pergunta":"x"}""")]
    [InlineData("consulta.v1", """{"pergunta":"x","extra":1}""")]
    [InlineData("consulta.v1", """{"referencias":[]}""")]
    [InlineData("consulta.v1", """{"pergunta":""}""")]
    [InlineData("consulta.v1", """{"pergunta":"x","referencias":["/etc/passwd"]}""")]
    [InlineData("consulta.v1", """{"pergunta":"x","referencias":["../segredo"]}""")]
    [InlineData("consulta.v1", """{"pergunta":"x","pergunta":"y"}""")]
    [InlineData("consulta.v1", """["pergunta"]""")]
    [InlineData("consulta.v1", "não é json")]
    [InlineData("revisao.v1", """{"artefato":"ABC"}""")]
    public void ContratoDesconhecidoOuEntradaForaDoSchemaSaoRecusados(string contrato, string entrada)
    {
        var c = new Cenario(destinatario: ConfiguracaoDoDestinatario([CapacidadeDoAgente.Revisar]));
        Assert.Equal(MotivoDaRecusaDeDelegacao.ContratoInvalido, Recusa(c.Pedido() with { Contrato = contrato, Entrada = entrada }));
    }

    [Fact]
    public void ObjetivoECriteriosTemLimites()
    {
        var c = new Cenario();
        Assert.Equal(MotivoDaRecusaDeDelegacao.ContratoInvalido, Recusa(c.Pedido() with { ObjetivoOriginal = " ", ObjetivoRedigido = " " }));
        Assert.Equal(MotivoDaRecusaDeDelegacao.ContratoInvalido,
            Recusa(c.Pedido() with { ObjetivoRedigido = new string('x', Execucao.TamanhoMaximoDoObjetivo + 1) }));
        Assert.Equal(MotivoDaRecusaDeDelegacao.ContratoInvalido,
            Recusa(c.Pedido() with { Criterios = new string('x', Delegacao.TamanhoMaximoDosCriterios + 1) }));
        // O objetivo guardado é o redigido; o original só entra no hash.
        var (protegida, filha) = Delegacao.Criar(c.Pedido() with
        {
            ObjetivoOriginal = "use a chave sk-123", ObjetivoRedigido = "use a chave [protegido]", ObjetivoAlteradoPelaRedacao = true
        }, 11, Agora);
        Assert.Equal(("use a chave [protegido]", true), (protegida.ObjetivoRedigido, protegida.ObjetivoAlteradoPelaRedacao));
        Assert.Equal("use a chave [protegido]", filha.Objetivo);
        Assert.Equal(Delegacao.HashDoObjetivoNormalizado(c.Origem.Id, "use a chave sk-123"), protegida.HashDoObjetivo);
    }

    // Mesma unidade (raiz, etapa, contrato, objetivo normalizado e entrada canônica) → mesma chave, em qualquer tentativa
    // da etapa de origem e para qualquer destinatário; o que muda a unidade muda a chave.
    [Fact]
    public void ChaveIdentificaAUnidadeDeTrabalhoSemTentativaNemDestinatario()
    {
        var c = new Cenario();
        var primeira = Delegacao.Criar(c.Pedido("Conferir o índice por estado"), 11, Agora).Delegacao;
        var normalizada = Delegacao.Criar(c.Pedido("  conferir   o ÍNDICE\npor estado ") with
        {
            Entrada = """{ "referencias": [], "pergunta": "O índice cobre a consulta por estado?\r\n" }"""
        }, 12, Agora).Delegacao;
        Assert.Equal(primeira.ChaveDeDeduplicacao, normalizada.ChaveDeDeduplicacao);

        // Nova tentativa da mesma etapa de origem (retomada automática): a chave não muda.
        c.Etapa.Interromper("reinício");
        c.Etapa.Retomar();
        c.Etapa.Iniciar(Agora);
        Assert.Equal(2, c.Etapa.Tentativa);
        Assert.Equal(primeira.ChaveDeDeduplicacao, Delegacao.Criar(c.Pedido(), 13, Agora).Delegacao.ChaveDeDeduplicacao);

        // Outro destinatário: mesma unidade (mesma chave), e a repetição só vale para o mesmo agente.
        var outro = new Agente("auditor", null, null, 1, Agora);
        var versaoDoOutro = outro.NovaVersao(ConfiguracaoDoDestinatario(), 1, Agora);
        var paraOutro = new Cenario(ConfiguracaoDoSolicitante(["auditor", "revisor"]));
        var a = Delegacao.Criar(paraOutro.Pedido(), 11, Agora).Delegacao;
        var b = Delegacao.Criar(paraOutro.Pedido() with { Destinatario = outro, VersaoDestinataria = versaoDoOutro }, 12, Agora).Delegacao;
        Assert.Equal(a.ChaveDeDeduplicacao, b.ChaveDeDeduplicacao);
        Assert.Same(a, a.ConferirRepeticao(paraOutro.Destinatario.Id));
        Assert.Equal(MotivoDaRecusaDeDelegacao.UnidadeJaAtribuida,
            Assert.Throws<DelegacaoRecusadaException>(() => a.ConferirRepeticao(outro.Id)).Motivo);

        // Outro objetivo, outra entrada, outro contrato ou outra etapa: outra unidade.
        Assert.NotEqual(primeira.ChaveDeDeduplicacao, Delegacao.Criar(c.Pedido("Outro objetivo"), 14, Agora).Delegacao.ChaveDeDeduplicacao);
        Assert.NotEqual(primeira.ChaveDeDeduplicacao,
            Delegacao.Criar(c.Pedido() with { Entrada = """{"pergunta":"Outra"}""" }, 15, Agora).Delegacao.ChaveDeDeduplicacao);
        var etapa2 = new EtapaDaExecucao(c.Origem, 2, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica, Agora);
        etapa2.TornarPronta([]);
        etapa2.Iniciar(Agora);
        Assert.NotEqual(primeira.ChaveDeDeduplicacao,
            Delegacao.Criar(c.Pedido() with { EtapaDeOrigem = etapa2 }, 16, Agora).Delegacao.ChaveDeDeduplicacao);
        Assert.NotEqual(Delegacao.CalcularChave(c.Origem.Id, 1, "consulta.v1", "a", "b"),
            Delegacao.CalcularChave(c.Origem.Id, 1, "revisao.v1", "a", "b"));
    }

    [Fact]
    public void TransicoesSeguemOFluxoEInvalidasLancam()
    {
        var c = new Cenario();
        var delegacao = Delegacao.Criar(c.Pedido(), 11, Agora).Delegacao;
        Assert.Throws<TransicaoInvalidaException>(() => delegacao.Iniciar(Agora));
        Assert.Throws<TransicaoInvalidaException>(() => delegacao.Falhar("x", Agora));
        Assert.Throws<TransicaoInvalidaException>(() => delegacao.Expirar("x", Agora));
        delegacao.Aceitar(Agora);
        Assert.Throws<TransicaoInvalidaException>(() => delegacao.Aceitar(Agora));
        Assert.Throws<TransicaoInvalidaException>(() => delegacao.Recusar("x", Agora));
        Assert.Throws<TransicaoInvalidaException>(() => delegacao.Responder(RespostaValida, c.VersaoDestinataria.Id, null, null, Agora));
        delegacao.Iniciar(Agora.AddMinutes(1));
        Assert.Equal((EstadoDaDelegacao.EmAndamento, Agora, Agora.AddMinutes(1)),
            (delegacao.Estado, delegacao.AceitaEm, delegacao.IniciadaEm));
        delegacao.Expirar("prazo", Agora.AddMinutes(61));
        Assert.Equal((EstadoDaDelegacao.Expirada, "prazo", true), (delegacao.Estado, delegacao.Motivo, delegacao.Terminal));
        Assert.Throws<TransicaoInvalidaException>(() => delegacao.Cancelar("x", Agora));

        var recusada = Delegacao.Criar(c.Pedido(), 12, Agora).Delegacao;
        recusada.Recusar("versão alterada", Agora);
        Assert.True(recusada.Terminal);
        var cancelada = Delegacao.Criar(c.Pedido(), 13, Agora).Delegacao;
        cancelada.Aceitar(Agora);
        cancelada.Cancelar("pedido do dono", Agora);
        Assert.Equal(EstadoDaDelegacao.Cancelada, cancelada.Estado);
    }

    private const string RespostaValida =
        """{"resposta":"Sim, pelo índice parcial.","confianca":"alta","evidencias":[{"tipo":"arquivo","referencia":"src/Migrations/x.cs:10-12"}]}""";

    [Fact]
    public void ResultadoEhValidadoGravadoUmaVezEDepoisImutavel()
    {
        var c = new Cenario();
        var delegacao = Delegacao.Criar(c.Pedido(), 11, Agora).Delegacao;
        delegacao.Aceitar(Agora);
        delegacao.Iniciar(Agora);
        var sessao = Guid.NewGuid();
        // Só a versão destinatária responde, e só com saída válida pelo contrato.
        Assert.Throws<InvalidOperationException>(() => delegacao.Responder(RespostaValida, c.VersaoSolicitante.Id, sessao, null, Agora));
        Assert.Throws<ArgumentException>(() => delegacao.Responder("""{"resposta":"x"}""", c.VersaoDestinataria.Id, sessao, null, Agora));
        Assert.Throws<ArgumentException>(() => delegacao.Responder(
            """{"resposta":"x","confianca":"alta","evidencias":[{"tipo":"arquivo","referencia":"/etc/passwd"}]}""",
            c.VersaoDestinataria.Id, sessao, null, Agora));
        Assert.Throws<ArgumentException>(() => delegacao.Responder(
            """{"resposta":"x","confianca":"alta","evidencias":[{"tipo":"arquivo","referencia":"src/../../segredo"}]}""",
            c.VersaoDestinataria.Id, sessao, null, Agora));
        Assert.Throws<ArgumentException>(() => delegacao.Responder(RespostaValida, c.VersaoDestinataria.Id, sessao, "xyz", Agora));
        Assert.Equal(EstadoDaDelegacao.EmAndamento, delegacao.Estado);

        delegacao.Responder(RespostaValida, c.VersaoDestinataria.Id, sessao, null, Agora.AddMinutes(5));
        Assert.Equal(EstadoDaDelegacao.Respondida, delegacao.Estado);
        Assert.Equal((c.VersaoDestinataria.Id, sessao, Agora.AddMinutes(5)),
            (delegacao.IdVersaoDoAutorDoResultado!.Value, delegacao.IdSessaoDoResultado!.Value, delegacao.EncerradaEm!.Value));
        Assert.Equal(ContratoDeDelegacao.Hash(delegacao.Resultado!), delegacao.HashDoResultado);
        Assert.Contains("\"pendencias\":[]", delegacao.Resultado);

        // Imutável: nem outra resposta nem outra transição. A mesma entrega (chaves em outra ordem) é reconhecida.
        var gravado = delegacao.Resultado;
        Assert.Throws<TransicaoInvalidaException>(() => delegacao.Responder(
            """{"resposta":"Não.","confianca":"baixa"}""", c.VersaoDestinataria.Id, sessao, null, Agora));
        Assert.Throws<TransicaoInvalidaException>(() => delegacao.Cancelar("x", Agora));
        Assert.Equal(gravado, delegacao.Resultado);
        Assert.True(delegacao.MesmoResultado(
            """{"evidencias":[{"referencia":"src/Migrations/x.cs:10-12","tipo":"arquivo"}],"confianca":"alta","resposta":"Sim, pelo índice parcial."}"""));
        Assert.False(delegacao.MesmoResultado("""{"resposta":"Não.","confianca":"baixa"}"""));
    }

    [Fact]
    public void RevisaoValeSoParaOArtefatoPedidoEExigeItensAoPedirCorrecao()
    {
        var c = new Cenario();
        var artefato = new string('a', 64);
        var delegacao = Delegacao.Criar(c.Pedido() with
        {
            Contrato = "revisao.v1", Entrada = $$"""{"artefato":"{{artefato}}","foco":"índices"}"""
        }, 11, Agora).Delegacao;
        delegacao.Aceitar(Agora);
        delegacao.Iniciar(Agora);
        const string correcao = """{"veredito":"requer_correcoes","resumo":"Falta índice.","itens":["Criar o índice parcial."]}""";
        Assert.Throws<InvalidOperationException>(() => delegacao.Responder(correcao, c.VersaoDestinataria.Id, null, new string('b', 64), Agora));
        Assert.Throws<InvalidOperationException>(() => delegacao.Responder(correcao, c.VersaoDestinataria.Id, null, null, Agora));
        Assert.Throws<ArgumentException>(() => delegacao.Responder(
            """{"veredito":"requer_correcoes","resumo":"Falta índice."}""", c.VersaoDestinataria.Id, null, artefato, Agora));
        Assert.Throws<ArgumentException>(() => delegacao.Responder(
            """{"veredito":"talvez","resumo":"?"}""", c.VersaoDestinataria.Id, null, artefato, Agora));
        delegacao.Responder(correcao, c.VersaoDestinataria.Id, null, artefato, Agora);
        Assert.Equal(artefato, delegacao.HashDoArtefatoLido);
    }

    [Fact]
    public void CatalogoEhFechadoComSchemasDeclarados()
    {
        Assert.Equal(["consulta.v1", "revisao.v1"], ContratosDeDelegacao.Todos.Select(c => c.Identificador));
        Assert.False(ContratosDeDelegacao.TentarObter("consulta", out _));
        Assert.False(ContratosDeDelegacao.TentarObter("consulta.v2", out _));
        var schema = ContratosDeDelegacao.Consulta.SchemaDeSaida();
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(["resposta", "confianca"], schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["alta", "media", "baixa"],
            schema["properties"]!["confianca"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["artefato"], ContratosDeDelegacao.Revisao.SchemaDeEntrada()["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal([CapacidadeDoAgente.Revisar], ContratosDeDelegacao.Revisao.CapacidadesAceitas);
    }
}
