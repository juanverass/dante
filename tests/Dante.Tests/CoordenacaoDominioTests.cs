using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Domain.Grupos;

namespace Dante.Tests;

// #310 (AD-75), PR 1: domínio da coordenação por papel. Grupo no modo Coordenador, execução Coordenada com snapshot
// congelado, validação pura do plano (tabela de recusas), forma canônica e hash, revisões (máximo 3 versões), gate do
// plano com finalidade própria, materialização em etapas topológicas e despacho só pelo plano aprovado e pelo snapshot.
public sealed class CoordenacaoDominioTests
{
    internal static readonly DateTimeOffset Agora = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    internal const string Contexto = """{"modo":"repositorio","alias":"@dante"}""";

    internal static ConfiguracaoDoAgente Leitor(params CapacidadeDoAgente[] capacidades) =>
        new(AgentKind.Codex, AgentPermissionProfile.Plan, AgentPermissionProfile.Plan, false, capacidades: capacidades,
            repositorios: ["@dante"]);

    // Grupo "pesquisa" no modo coordenador: coord (planejar) coordena; pesq (pesquisar) e rev (revisar) recebem
    // unidades; analista (só planejar) não tem capacidade para consulta.v1. Ninguém escreve.
    internal sealed class Cenario
    {
        public Cenario(long numero = 50)
        {
            Coordenador = Agente("coord", CapacidadeDoAgente.Planejar);
            Pesquisador = Agente("pesq", CapacidadeDoAgente.Pesquisar);
            Revisor = Agente("rev", CapacidadeDoAgente.Revisar);
            Analista = Agente("analista", CapacidadeDoAgente.Planejar);
            Grupo = new GrupoDeAgentes("pesquisa", null, 1001, Agora);
            VersaoDoGrupo = Grupo.NovaVersao(ConfiguracaoDoGrupo(), 1001, Agora);
            Ativacao = Grupo.Ativar(VersaoDoGrupo, VersaoDoGrupo.HashDaConfiguracao, 1001, Agora);
            Execucao = new Execucao(numero, TipoDeExecucao.Coordenada, "telegram", 1001, Contexto, "Investigar o índice", Agora,
                Coordenador.Versao.Id, VersaoDoGrupo.Id);
            Participantes =
            [
                Participante("analista", Analista), Participante("coord", Coordenador), Participante("pesq", Pesquisador),
                Participante("rev", Revisor)
            ];
            Coordenacao = new CoordenacaoDaExecucao(Execucao, Grupo.Id, Grupo.Nome, VersaoDoGrupo.Id, VersaoDoGrupo.Numero,
                VersaoDoGrupo.HashDaConfiguracao, "coord", Participantes, "Investigar o índice", false,
                RoteiroDaExecucao.HashDoObjetivo(Execucao.Id, "Investigar o índice"));
            EtapaDeAprovacao = new EtapaDaExecucao(Execucao, 1, TipoDeEtapa.AprovacaoHumana, false, PoliticaDeRetomada.Automatica,
                Agora);
            Execucao.Iniciar(Agora);
        }

        public (Agente Agente, VersaoDoAgente Versao) Coordenador { get; }
        public (Agente Agente, VersaoDoAgente Versao) Pesquisador { get; }
        public (Agente Agente, VersaoDoAgente Versao) Revisor { get; }
        public (Agente Agente, VersaoDoAgente Versao) Analista { get; }
        public GrupoDeAgentes Grupo { get; }
        public VersaoDoGrupo VersaoDoGrupo { get; }
        public AtivacaoDeVersaoDoGrupo Ativacao { get; }
        public Execucao Execucao { get; }
        public IReadOnlyList<ParticipanteDaExecucao> Participantes { get; }
        public CoordenacaoDaExecucao Coordenacao { get; }
        public EtapaDaExecucao EtapaDeAprovacao { get; }

        public IReadOnlyList<VersaoDoAgente> Versoes =>
            [Coordenador.Versao, Pesquisador.Versao, Revisor.Versao, Analista.Versao];

        public IEnumerable<Agente> Agentes => [Coordenador.Agente, Pesquisador.Agente, Revisor.Agente, Analista.Agente];

        public ContextoDoPlano ContextoDoPlano(int delegacoes = 0) =>
            Dante.Domain.Execucoes.ContextoDoPlano.DoSnapshot(Coordenacao, Participantes, Versoes, delegacoes);

        public PlanoValidado Validado(string proposta, int delegacoes = 0)
        {
            var resultado = ValidacaoDoPlano.Validar(proposta, ContextoDoPlano(delegacoes));
            Assert.True(resultado.Valido, string.Join("; ", resultado.Recusas.Select(r => $"{r.Motivo} {r.Unidade}: {r.Mensagem}")));
            return resultado.Plano!;
        }

        public ParticipanteDaExecucao DoPapel(string papel) => Participantes.Single(p => p.Papel == papel);

        // Plano aprovado pelo gate, com o pedido decidido.
        public (PlanoDaCoordenacao Plano, PedidoDeAprovacao Pedido) Aprovado(string? proposta = null)
        {
            var plano = PlanoDaCoordenacao.Inicial(Execucao, Coordenacao, Validado(proposta ?? Proposta), Agora);
            var pedido = PedidoDeAprovacao.DoPlano(Execucao, EtapaDeAprovacao, plano, Coordenacao, Agora);
            pedido.Aprovar("telegram", 1001, plano.Hash[..12], Agora);
            plano.Aprovar(pedido);
            return (plano, pedido);
        }

        public PedidoDeDelegacaoDoPlano Despacho(PlanoDaCoordenacao plano, EtapaDaExecucao etapa, string papel,
            int delegacoes = 0)
        {
            var destino = papel switch { "pesq" => Pesquisador, "rev" => Revisor, "analista" => Analista, _ => Coordenador };
            return new PedidoDeDelegacaoDoPlano(Execucao, etapa, plano, Coordenacao, DoPapel("coord"), Coordenador.Agente,
                Coordenador.Versao, DoPapel(papel), destino.Agente, destino.Versao, ["@dante"], delegacoes, delegacoes);
        }

        private ConfiguracaoDoGrupo ConfiguracaoDoGrupo() => new(ModoDeCoordenacao.Coordenador,
            [
                new ParticipanteDoGrupo(Coordenador.Agente.Id, "coord", "Planeja e consolida.", false),
                new ParticipanteDoGrupo(Pesquisador.Agente.Id, "pesq", "Pesquisa no código.", false),
                new ParticipanteDoGrupo(Revisor.Agente.Id, "rev", "Revisa conclusões.", false),
                new ParticipanteDoGrupo(Analista.Agente.Id, "analista", null, false)
            ], [], new PoliticaDoGrupo(0, 1, ["@dante"]), "coord");

        private ParticipanteDaExecucao Participante(string papel, (Agente Agente, VersaoDoAgente Versao) agente) =>
            new(Execucao, papel, agente.Agente.Handle, agente.Versao.Id, agente.Versao.Numero, agente.Versao.HashDaConfiguracao,
                false, null);

        private static (Agente, VersaoDoAgente) Agente(string handle, CapacidadeDoAgente capacidade)
        {
            var agente = new Agente(handle, null, null, 1001, Agora);
            return (agente, agente.NovaVersao(Leitor(capacidade), 1001, Agora));
        }
    }

    // Duas unidades independentes e uma terceira que depende das duas, declaradas fora da ordem topológica.
    internal const string Proposta = """
        {"unidades":[
          {"id":"sintese","papel":"rev","contrato":"consulta.v1","objetivo":"Conferir as conclusões","entrada":{"pergunta":"As conclusões se sustentam?"},"dependeDe":["indice","consulta"]},
          {"id":"indice","papel":"pesq","contrato":"consulta.v1","objetivo":"Levantar os índices","entrada":{"pergunta":"Quais índices existem em execucoes?"},"criterios":"Citar a migration."},
          {"id":"consulta","papel":"pesq","contrato":"consulta.v1","objetivo":"Achar a consulta por estado","entrada":{"pergunta":"Onde se filtra por estado?"}}
        ]}
        """;

    private static IReadOnlyList<RecusaDoPlano> Recusas(Cenario c, string proposta, int delegacoes = 0)
    {
        var resultado = ValidacaoDoPlano.Validar(proposta, c.ContextoDoPlano(delegacoes));
        Assert.False(resultado.Valido);
        Assert.Null(resultado.Plano);
        return resultado.Recusas;
    }

    private static string Unidade(string campos) => "{\"unidades\":[" + campos + "]}";

    private const string Valida =
        """{"id":"u1","papel":"pesq","contrato":"consulta.v1","objetivo":"Responder","entrada":{"pergunta":"P?"}}""";

    // ---- grupo no modo coordenador ----

    [Fact]
    public void GrupoCoordenadoTemCoordenadorSemRoteiroENinguemEscreve()
    {
        var c = new Cenario();
        var configuracao = c.VersaoDoGrupo.Configuracao!;
        Assert.Equal((ModoDeCoordenacao.Coordenador, "coord"), (configuracao.Modo, configuracao.PapelDoCoordenador));
        Assert.Empty(configuracao.Roteiro);
        Assert.True(c.VersaoDoGrupo.Integra);
        Assert.Contains("\"formato\":\"2\",\"coordenador\":\"coord\"", configuracao.JsonCanonico());

        ParticipanteDoGrupo P(string papel, bool escreve = false) => new(Guid.NewGuid(), papel, null, escreve);
        ConfiguracaoDoGrupo Grupo(IEnumerable<ParticipanteDoGrupo> participantes, string? coordenador = "coord",
            int rodadas = 0, IEnumerable<PassoDoRoteiro>? roteiro = null, int paralelismo = 1) =>
            new(ModoDeCoordenacao.Coordenador, participantes, roteiro ?? [], new PoliticaDoGrupo(rodadas, paralelismo),
                coordenador);

        Assert.Throws<ArgumentException>(() => Grupo([P("coord"), P("pesq")], coordenador: null));
        Assert.Throws<ArgumentException>(() => Grupo([P("coord"), P("pesq")], coordenador: "outro"));
        Assert.Throws<ArgumentException>(() => Grupo([P("coord")]));
        Assert.Contains("#311", Assert.Throws<ArgumentException>(() => Grupo([P("coord"), P("pesq", escreve: true)])).Message);
        Assert.Throws<ArgumentException>(() => Grupo([P("coord"), P("pesq")], rodadas: 1));
        Assert.Throws<ArgumentException>(() => Grupo([P("coord"), P("pesq")], paralelismo: 2));
        Assert.Throws<ArgumentException>(() => Grupo([P("coord"), P("pesq")],
            roteiro: [new PassoDoRoteiro(TipoDePassoDoRoteiro.AprovacaoHumana)]));
        // O roteiro nunca tem coordenador, e o JSON dele continua no formato 1.
        Assert.Throws<ArgumentException>(() => new ConfiguracaoDoGrupo(ModoDeCoordenacao.Roteiro, [P("dev", true), P("rev")],
            [new(TipoDePassoDoRoteiro.Implementar, "dev"), new(TipoDePassoDoRoteiro.Revisar, "rev"),
             new(TipoDePassoDoRoteiro.AprovacaoHumana)], new PoliticaDoGrupo(0), "dev"));
        var roteiro = new ConfiguracaoDoGrupo(ModoDeCoordenacao.Roteiro, [P("dev", true), P("rev")],
            [new(TipoDePassoDoRoteiro.Implementar, "dev"), new(TipoDePassoDoRoteiro.Revisar, "rev"),
             new(TipoDePassoDoRoteiro.AprovacaoHumana)], new PoliticaDoGrupo(0));
        Assert.StartsWith("{\"formato\":\"1\",\"modo\":\"Roteiro\"", roteiro.JsonCanonico());
        Assert.Null(roteiro.PapelDoCoordenador);
    }

    [Fact]
    public void CoordenadaApontaParaOGrupoEParaOCoordenadorCongelado()
    {
        var c = new Cenario();
        Assert.Equal((c.VersaoDoGrupo.Id, c.Coordenador.Versao.Id), (c.Execucao.IdVersaoDoGrupo, c.Execucao.IdVersaoDoAgente));
        Assert.Equal("E50", c.Execucao.Codigo);
        Assert.Throws<ArgumentException>(() => new Execucao(51, TipoDeExecucao.Coordenada, "telegram", 1, "{}", null, Agora,
            idVersaoDoGrupo: Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new Execucao(51, TipoDeExecucao.Coordenada, "telegram", 1, "{}", null, Agora,
            Guid.NewGuid()));
        // O snapshot exige o coordenador entre os participantes, na versão para que a execução aponta, e ninguém escrevendo.
        Assert.Throws<ArgumentException>(() => new CoordenacaoDaExecucao(c.Execucao, c.Grupo.Id, "pesquisa", c.VersaoDoGrupo.Id,
            1, c.VersaoDoGrupo.HashDaConfiguracao, "chefe", c.Participantes, "x", false, new string('a', 64)));
        var escreve = new ParticipanteDaExecucao(c.Execucao, "dev", "dev", Guid.NewGuid(), 1, new string('b', 64), true, null);
        Assert.Throws<ArgumentException>(() => new CoordenacaoDaExecucao(c.Execucao, c.Grupo.Id, "pesquisa", c.VersaoDoGrupo.Id,
            1, c.VersaoDoGrupo.HashDaConfiguracao, "coord", [.. c.Participantes, escreve], "x", false, new string('a', 64)));
        var outraVersao = new ParticipanteDaExecucao(c.Execucao, "coord", "coord", Guid.NewGuid(), 2, new string('c', 64), false, null);
        Assert.Throws<InvalidOperationException>(() => new CoordenacaoDaExecucao(c.Execucao, c.Grupo.Id, "pesquisa",
            c.VersaoDoGrupo.Id, 1, c.VersaoDoGrupo.HashDaConfiguracao, "coord",
            [outraVersao, c.DoPapel("pesq")], "x", false, new string('a', 64)));
        Assert.True(c.Coordenacao.SnapshotConfere(c.Participantes));
        Assert.False(c.Coordenacao.SnapshotConfere(c.Participantes.Where(p => p.Papel != "rev")));
    }

    // ---- validação do plano ----

    [Fact]
    public void PlanoValidoEhCanonicoTopologicoEOHashIndependeDaFormaDaProposta()
    {
        var c = new Cenario();
        var plano = c.Validado(Proposta);
        Assert.Equal(["indice", "consulta", "sintese"], plano.Unidades.Select(u => u.Id));
        Assert.Equal(["consulta", "indice"], plano.Unidades[2].DependeDe);
        Assert.Equal("Citar a migration.", plano.Unidades[0].Criterios);
        Assert.Equal(ValidacaoDoPlano.Hash(plano.Conteudo), plano.Hash);
        Assert.StartsWith("{\"formato\":\"1\",\"unidades\":[{\"contrato\":\"consulta.v1\",\"criterios\":", plano.Conteudo);

        // Mesma intenção com chaves em outra ordem, espaços, CRLF, papel em maiúsculas, dependências repetidas e entrada
        // com as chaves trocadas: mesmo conteúdo canônico, mesmo hash.
        var reescrita = """
            {"unidades":[
              {"dependeDe":["consulta","indice","consulta"],"entrada":{"pergunta":"As conclusões se sustentam?"},"objetivo":"  Conferir as conclusões\r\n","contrato":"consulta.v1","papel":"REV","id":"sintese","escrita":false},
              {"contrato":"consulta.v1","id":"indice","papel":"pesq","objetivo":"Levantar os índices","criterios":"Citar a migration.","entrada":{"referencias":[],"pergunta":"Quais índices existem em execucoes?"}},
              {"id":"consulta","papel":"pesq","contrato":"consulta.v1","objetivo":"Achar a consulta por estado","entrada":{"pergunta":"Onde se filtra por estado?"},"criterios":null,"dependeDe":[]}
            ]}
            """;
        var outra = c.Validado(reescrita);
        Assert.Equal(plano.Conteudo, outra.Conteudo);
        Assert.Equal(plano.Hash, outra.Hash);
        // A forma gravada é relida sem perda.
        var relido = ValidacaoDoPlano.LerCanonico(plano.Conteudo);
        Assert.Equal((plano.Conteudo, plano.Hash), (relido.Conteudo, relido.Hash));
        static object Forma(UnidadeDoPlano u) =>
            (u.Id, u.Papel, u.Contrato, u.Objetivo, u.Entrada, u.Criterios, string.Join(",", u.DependeDe));
        Assert.Equal(plano.Unidades.Select(Forma), relido.Unidades.Select(Forma));
    }

    public static TheoryData<string, MotivoDaRecusaDoPlano> PlanosInvalidos => new()
    {
        { "", MotivoDaRecusaDoPlano.FormaInvalida },
        { "não é json", MotivoDaRecusaDoPlano.FormaInvalida },
        { "[]", MotivoDaRecusaDoPlano.FormaInvalida },
        { """{"unidades":{}}""", MotivoDaRecusaDoPlano.FormaInvalida },
        // O hash nunca vem do agente: um campo a mais (inclusive "hash") é recusado.
        { """{"unidades":[],"hash":"abc"}""", MotivoDaRecusaDoPlano.FormaInvalida },
        { """{"unidades":[]}""", MotivoDaRecusaDoPlano.PlanoVazio },
        { Unidade("""{"id":"u1","papel":"pesq","contrato":"consulta.v1","objetivo":"x","entrada":{"pergunta":"P"},"hash":"x"}"""), MotivoDaRecusaDoPlano.FormaInvalida },
        { Unidade("""{"id":"U 1","papel":"pesq","contrato":"consulta.v1","objetivo":"x","entrada":{"pergunta":"P"}}"""), MotivoDaRecusaDoPlano.IdentificadorInvalido },
        { Unidade(Valida + "," + Valida), MotivoDaRecusaDoPlano.IdentificadorRepetido },
        { Unidade("""{"id":"u1","papel":"fantasma","contrato":"consulta.v1","objetivo":"x","entrada":{"pergunta":"P"}}"""), MotivoDaRecusaDoPlano.PapelInexistente },
        { Unidade("""{"id":"u1","papel":"coord","contrato":"consulta.v1","objetivo":"x","entrada":{"pergunta":"P"}}"""), MotivoDaRecusaDoPlano.UnidadeDoCoordenador },
        { Unidade("""{"id":"u1","papel":"pesq","contrato":"tarefa.v1","objetivo":"x","entrada":{"pergunta":"P"}}"""), MotivoDaRecusaDoPlano.ContratoInvalido },
        { Unidade("""{"id":"u1","papel":"rev","contrato":"revisao.v1","objetivo":"x","entrada":{"artefato":"aa"}}"""), MotivoDaRecusaDoPlano.ContratoNaoPermitido },
        { Unidade("""{"id":"u1","papel":"analista","contrato":"consulta.v1","objetivo":"x","entrada":{"pergunta":"P"}}"""), MotivoDaRecusaDoPlano.CapacidadeInsuficiente },
        { Unidade("""{"id":"u1","papel":"pesq","contrato":"consulta.v1","objetivo":"x","entrada":{"pergunta":"P","caminho":"/etc"}}"""), MotivoDaRecusaDoPlano.EntradaInvalida },
        { Unidade("""{"id":"u1","papel":"pesq","contrato":"consulta.v1","objetivo":"x","entrada":"P"}"""), MotivoDaRecusaDoPlano.EntradaInvalida },
        { Unidade("""{"id":"u1","papel":"pesq","contrato":"consulta.v1","objetivo":"   ","entrada":{"pergunta":"P"}}"""), MotivoDaRecusaDoPlano.ObjetivoInvalido },
        { Unidade("""{"id":"u1","papel":"pesq","contrato":"consulta.v1","objetivo":"x","entrada":{"pergunta":"P"},"criterios":"__CRITERIOS__"}"""), MotivoDaRecusaDoPlano.CriteriosInvalidos },
        { Unidade("""{"id":"u1","papel":"pesq","contrato":"consulta.v1","objetivo":"x","entrada":{"pergunta":"P"},"dependeDe":["u9"]}"""), MotivoDaRecusaDoPlano.DependenciaInexistente },
        { Unidade("""{"id":"u1","papel":"pesq","contrato":"consulta.v1","objetivo":"x","entrada":{"pergunta":"P"},"dependeDe":["u1"]}"""), MotivoDaRecusaDoPlano.Ciclo },
        { Unidade("""{"id":"u1","papel":"pesq","contrato":"consulta.v1","objetivo":"x","entrada":{"pergunta":"P"},"dependeDe":["u2"]},{"id":"u2","papel":"rev","contrato":"consulta.v1","objetivo":"y","entrada":{"pergunta":"Q"},"dependeDe":["u3"]},{"id":"u3","papel":"pesq","contrato":"consulta.v1","objetivo":"z","entrada":{"pergunta":"R"},"dependeDe":["u1"]}"""), MotivoDaRecusaDoPlano.Ciclo },
        { Unidade("""{"id":"u1","papel":"pesq","contrato":"consulta.v1","objetivo":"x","entrada":{"pergunta":"P"},"escrita":true}"""), MotivoDaRecusaDoPlano.RequerGateway },
        { Unidade("""{"id":"u1","papel":"pesq","contrato":"consulta.v1","objetivo":"x","entrada":{"pergunta":"P"},"escrita":"sim"}"""), MotivoDaRecusaDoPlano.FormaInvalida }
    };

    [Theory]
    [MemberData(nameof(PlanosInvalidos))]
    public void PlanoInvalidoEhRecusadoComOMotivoESemPlano(string proposta, MotivoDaRecusaDoPlano motivo)
    {
        var c = new Cenario();
        var recusas = Recusas(c, proposta.Replace("__CRITERIOS__", new string('c', ValidacaoDoPlano.TamanhoMaximoDosCriterios + 1)));
        Assert.Contains(recusas, r => r.Motivo == motivo);
    }

    [Fact]
    public void OrcamentoDeQuatroUnidadesValeParaAExecucaoInteira()
    {
        var c = new Cenario();
        string Plano(int n) => Unidade(string.Join(",", Enumerable.Range(1, n).Select(i =>
            "{\"id\":\"u" + i + "\",\"papel\":\"pesq\",\"contrato\":\"consulta.v1\",\"objetivo\":\"x" + i +
            "\",\"entrada\":{\"pergunta\":\"P" + i + "\"}}")));
        Assert.Equal(4, c.Validado(Plano(4)).Unidades.Count);
        Assert.Contains(Recusas(c, Plano(5)), r => r.Motivo == MotivoDaRecusaDoPlano.OrcamentoExcedido);
        // Depois de 3 delegações na raiz (por exemplo, numa revisão anterior), só cabe mais uma.
        Assert.Single(c.Validado(Plano(1), delegacoes: 3).Unidades);
        Assert.Contains(Recusas(c, Plano(2), delegacoes: 3), r => r.Motivo == MotivoDaRecusaDoPlano.OrcamentoExcedido);
        Assert.Contains(Recusas(c, Plano(1), delegacoes: 4), r => r.Motivo == MotivoDaRecusaDoPlano.OrcamentoExcedido);
    }

    [Fact]
    public void TodasAsRecusasSaoRelatadasDeUmaVez()
    {
        var c = new Cenario();
        var recusas = Recusas(c, Unidade(
            """{"id":"u1","papel":"coord","contrato":"consulta.v1","objetivo":"x","entrada":{"pergunta":"P"},"escrita":true},""" +
            """{"id":"u2","papel":"analista","contrato":"revisao.v1","objetivo":"","entrada":{"artefato":"x"}}"""));
        Assert.Equal(
            [MotivoDaRecusaDoPlano.RequerGateway, MotivoDaRecusaDoPlano.UnidadeDoCoordenador,
             MotivoDaRecusaDoPlano.ContratoNaoPermitido, MotivoDaRecusaDoPlano.ObjetivoInvalido],
            recusas.Select(r => r.Motivo));
        Assert.Equal(["u1", "u1", "u2", "u2"], recusas.Select(r => r.Unidade));
    }

    [Fact]
    public void FormaCanonicaAdulteradaNaoEhAceita()
    {
        var c = new Cenario();
        var plano = c.Validado(Proposta);
        Assert.Throws<InvalidOperationException>(() => ValidacaoDoPlano.LerCanonico(plano.Conteudo.Replace("\"formato\":\"1\"", "\"formato\":\"9\"")));
        Assert.Throws<InvalidOperationException>(() => ValidacaoDoPlano.LerCanonico(plano.Conteudo.Replace(",\"objetivo\"", ", \"objetivo\"")));
        Assert.Throws<InvalidOperationException>(() => ValidacaoDoPlano.LerCanonico("{}"));
        // Snapshot adulterado não serve de contexto: é falha de integridade, não recusa do plano.
        var adulterado = c.Participantes.Where(p => p.Papel != "rev").ToArray();
        Assert.Throws<InvalidOperationException>(() => ContextoDoPlano.DoSnapshot(c.Coordenacao, adulterado, c.Versoes, 0));
        Assert.Throws<InvalidOperationException>(() =>
            ContextoDoPlano.DoSnapshot(c.Coordenacao, c.Participantes, c.Versoes.Where(v => v != c.Revisor.Versao).ToArray(), 0));
    }

    // ---- plano, gate e materialização ----

    [Fact]
    public void PlanoNasceAguardandoEAprovacaoValeSoParaOMesmoHash()
    {
        var c = new Cenario();
        var plano = PlanoDaCoordenacao.Inicial(c.Execucao, c.Coordenacao, c.Validado(Proposta), Agora);
        Assert.Equal((1, (Guid?)null, MotivoDoPlano.Inicial, EstadoDoPlano.AguardandoAprovacao, 3),
            (plano.Revisao, plano.IdPlanoAnterior, plano.Motivo, plano.Estado, plano.QuantidadeDeUnidades));
        // Um hash informado junto do plano não é usado: o gravado é o do conteúdo.
        var forjado = c.Validado(Proposta) with { Hash = new string('f', 64) };
        Assert.Equal(ValidacaoDoPlano.Hash(forjado.Conteudo), PlanoDaCoordenacao.Inicial(c.Execucao, c.Coordenacao, forjado, Agora).Hash);

        var pedido = PedidoDeAprovacao.DoPlano(c.Execucao, c.EtapaDeAprovacao, plano, c.Coordenacao, Agora);
        Assert.Equal((FinalidadeDaAprovacao.PlanoSomenteLeitura, plano.Id, plano.Hash, c.Coordenacao.HashDoSnapshot, 1),
            (pedido.Finalidade, pedido.IdPlano, pedido.HashDoArtefato, pedido.HashDoSnapshot, pedido.Rodada));
        // Antes da decisão do gate, o plano não muda; hash errado não aprova; o plano não é "substituído" no gate.
        Assert.Throws<InvalidOperationException>(() => plano.Aprovar(pedido));
        Assert.Throws<InvalidOperationException>(() => pedido.Aprovar("telegram", 1001, new string('0', 12), Agora));
        Assert.Throws<InvalidOperationException>(() => pedido.Substituir("telegram", 1001, new string('1', 64), Agora));
        pedido.Aprovar("telegram", 1001, plano.Hash[..12], Agora);
        plano.Aprovar(pedido);
        Assert.Equal(EstadoDoPlano.Aprovado, plano.Estado);
        Assert.Throws<TransicaoInvalidaException>(() => plano.Aprovar(pedido));

        // O pedido de um plano é só da Coordenada e só de um plano que aguarda decisão.
        Assert.Throws<InvalidOperationException>(() => PedidoDeAprovacao.DoPlano(c.Execucao, c.EtapaDeAprovacao, plano, c.Coordenacao, Agora));
        var outro = PlanoDaCoordenacao.Inicial(c.Execucao, c.Coordenacao, c.Validado(Proposta), Agora);
        var unidade = new EtapaDaExecucao(c.Execucao, 9, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica, Agora);
        Assert.Throws<InvalidOperationException>(() => PedidoDeAprovacao.DoPlano(c.Execucao, unidade, outro, c.Coordenacao, Agora));
        // Um pedido de outro plano, ou com outra decisão, não decide este.
        var recusado = PedidoDeAprovacao.DoPlano(c.Execucao, c.EtapaDeAprovacao, outro, c.Coordenacao, Agora);
        Assert.Throws<InvalidOperationException>(() => outro.Aprovar(pedido));
        recusado.Recusar("telegram", 1001, "não", Agora);
        Assert.Throws<InvalidOperationException>(() => outro.Aprovar(recusado));
        outro.Recusar(recusado);
        Assert.Equal(EstadoDoPlano.Recusado, outro.Estado);
    }

    [Fact]
    public void MaterializaUnidadesEmOrdemTopologicaComOsParticipantesDoSnapshot()
    {
        var c = new Cenario();
        var pendente = PlanoDaCoordenacao.Inicial(c.Execucao, c.Coordenacao, c.Validado(Proposta), Agora);
        Assert.Throws<InvalidOperationException>(() => pendente.Materializar(c.Execucao, c.Coordenacao, c.Participantes, 2, Agora));

        var (plano, _) = c.Aprovado();
        var (etapas, dependencias) = plano.Materializar(c.Execucao, c.Coordenacao, c.Participantes, 2, Agora);
        Assert.Equal([2, 3, 4], etapas.Select(e => e.Numero));
        Assert.Equal(["indice", "consulta", "sintese"], etapas.Select(e => e.Unidade));
        Assert.All(etapas, e =>
        {
            Assert.Equal((TipoDeEtapa.UnidadeDoPlano, false, PoliticaDeRetomada.Automatica, plano.Id),
                (e.Tipo, e.Escrita, e.PoliticaDeRetomada, e.IdPlano));
            Assert.Null(e.Passo);
        });
        Assert.Equal([c.Pesquisador.Versao.Id, c.Pesquisador.Versao.Id, c.Revisor.Versao.Id], etapas.Select(e => e.IdVersaoDoAgente!.Value));
        Assert.Equal(["pesq", "pesq", "rev"], etapas.Select(e => e.Papel));
        Assert.Equal(2, dependencias.Count);
        Assert.All(dependencias, d => Assert.Equal(etapas[2].Id, d.IdEtapa));
        Assert.Equal(new[] { etapas[0].Id, etapas[1].Id }.Order(), dependencias.Select(d => d.IdEtapaRequerida).Order());
        // A unidade da etapa é lida do plano gravado.
        Assert.Equal("Conferir as conclusões", plano.Unidade(etapas[2]).Objetivo);
        // A dependente só fica pronta depois das requeridas.
        Assert.Throws<InvalidOperationException>(() => etapas[2].TornarPronta(etapas.Take(2)));
        // Snapshot adulterado não materializa.
        Assert.Throws<InvalidOperationException>(() =>
            plano.Materializar(c.Execucao, c.Coordenacao, c.Participantes.Where(p => p.Papel != "rev").ToArray(), 2, Agora));
    }

    // ---- revisões do plano ----

    [Fact]
    public void ReplanejaSoPorUnidadeSemRespostaOuPedidoHumanoAteTresVersoesSemApagarHistorico()
    {
        var c = new Cenario();
        var (plano, _) = c.Aprovado();
        var (etapas, _) = plano.Materializar(c.Execucao, c.Coordenacao, c.Participantes, 2, Agora);
        var novo = c.Validado(Unidade(Valida));

        // Sem unidade sem resposta, não há o que replanejar; nem com unidade em curso.
        Assert.Throws<InvalidOperationException>(() =>
            PlanoDaCoordenacao.Revisar(c.Execucao, plano, MotivoDoPlano.UnidadeSemResposta, novo, etapas, null, Agora));
        etapas[0].TornarPronta([]);
        etapas[0].Iniciar(Agora);
        Assert.Throws<InvalidOperationException>(() =>
            PlanoDaCoordenacao.Revisar(c.Execucao, plano, MotivoDoPlano.UnidadeSemResposta, novo, etapas, null, Agora));
        etapas[0].Falhar("Delegação expirada.", null, Agora);
        // Inicial não é motivo de revisão; todas as etapas do anterior precisam ser informadas.
        Assert.Throws<ArgumentException>(() =>
            PlanoDaCoordenacao.Revisar(c.Execucao, plano, MotivoDoPlano.Inicial, novo, etapas, null, Agora));
        Assert.Throws<ArgumentException>(() =>
            PlanoDaCoordenacao.Revisar(c.Execucao, plano, MotivoDoPlano.UnidadeSemResposta, novo, etapas.Take(2).ToArray(), null, Agora));

        var segunda = PlanoDaCoordenacao.Revisar(c.Execucao, plano, MotivoDoPlano.UnidadeSemResposta, novo, etapas, null, Agora);
        Assert.Equal((2, plano.Id, MotivoDoPlano.UnidadeSemResposta, EstadoDoPlano.AguardandoAprovacao),
            (segunda.Revisao, segunda.IdPlanoAnterior, segunda.Motivo, segunda.Estado));
        Assert.Equal(EstadoDoPlano.Substituido, plano.Estado);
        // A falha fica como estava; as pendentes ficam ignoradas, com o motivo.
        Assert.Equal([EstadoDaEtapa.Falhou, EstadoDaEtapa.Ignorada, EstadoDaEtapa.Ignorada], etapas.Select(e => e.Estado));
        Assert.Equal("Unidade substituída pela revisão 2 do plano.", etapas[1].Motivo);
        Assert.Throws<InvalidOperationException>(() =>
            PlanoDaCoordenacao.Revisar(c.Execucao, plano, MotivoDoPlano.UnidadeSemResposta, novo, etapas, null, Agora));

        // Pedido humano: só com a correção pedida no gate deste plano.
        var pedido = PedidoDeAprovacao.DoPlano(c.Execucao, c.EtapaDeAprovacao, segunda, c.Coordenacao, Agora);
        Assert.Throws<InvalidOperationException>(() =>
            PlanoDaCoordenacao.Revisar(c.Execucao, segunda, MotivoDoPlano.PedidoHumano, novo, [], pedido, Agora));
        pedido.SolicitarCorrecao("telegram", 1001, segunda.Hash, "Inclua a consulta por estado.", Agora);
        var terceira = PlanoDaCoordenacao.Revisar(c.Execucao, segunda, MotivoDoPlano.PedidoHumano, novo, [], pedido, Agora);
        Assert.Equal((3, EstadoDoPlano.Substituido), (terceira.Revisao, segunda.Estado));

        // Terceira versão é a última: a inicial e duas substitutas.
        var pedidoDaTerceira = PedidoDeAprovacao.DoPlano(c.Execucao, c.EtapaDeAprovacao, terceira, c.Coordenacao, Agora);
        pedidoDaTerceira.SolicitarCorrecao("telegram", 1001, terceira.Hash, "De novo.", Agora);
        Assert.Contains("3 versões", Assert.Throws<InvalidOperationException>(() =>
            PlanoDaCoordenacao.Revisar(c.Execucao, terceira, MotivoDoPlano.PedidoHumano, novo, [], pedidoDaTerceira, Agora)).Message);
    }

    // ---- despacho ----

    private static (PlanoDaCoordenacao Plano, IReadOnlyList<EtapaDaExecucao> Etapas) EmCurso(Cenario c)
    {
        var (plano, _) = c.Aprovado();
        var (etapas, _) = plano.Materializar(c.Execucao, c.Coordenacao, c.Participantes, 2, Agora);
        etapas[0].TornarPronta([]);
        etapas[0].Iniciar(Agora);
        return (plano, etapas);
    }

    private static MotivoDaRecusaDeDelegacao Recusa(PedidoDeDelegacaoDoPlano pedido) =>
        Assert.Throws<DelegacaoRecusadaException>(() => Delegacao.CriarDoPlano(pedido, 60, Agora)).Motivo;

    [Fact]
    public void UnidadeAprovadaViraConsultaDoParticipanteCongeladoSemPodeConsultar()
    {
        var c = new Cenario();
        var (plano, etapas) = EmCurso(c);
        Assert.Empty(c.Coordenador.Versao.PodeConsultar);
        var (delegacao, filha) = Delegacao.CriarDoPlano(c.Despacho(plano, etapas[0], "pesq"), 60, Agora);

        Assert.Equal((TipoDeExecucao.Delegada, c.Execucao.Id, c.Execucao.Id, 1),
            (filha.Tipo, filha.IdExecucaoPai, filha.IdExecucaoRaiz, filha.Profundidade));
        Assert.Equal(c.Pesquisador.Versao.Id, filha.IdVersaoDoAgente);
        Assert.Equal((c.Execucao.Id, etapas[0].Id, c.Coordenador.Versao.Id, c.Pesquisador.Versao.Id),
            (delegacao.IdExecucaoOrigem, delegacao.IdEtapaOrigem, delegacao.IdVersaoSolicitante, delegacao.IdVersaoDestinataria));
        var unidade = plano.Unidade(etapas[0]);
        Assert.Equal(("consulta.v1", unidade.Entrada, unidade.Objetivo, unidade.Criterios, false),
            (delegacao.Contrato, delegacao.Entrada, delegacao.ObjetivoRedigido, delegacao.Criterios, delegacao.Escrita));
        Assert.Equal([c.Coordenador.Agente.Id], delegacao.CadeiaDeAgentes);
        Assert.Equal((30, 60), (delegacao.MinutosParaIniciar, delegacao.MinutosDeDuracao));
    }

    [Fact]
    public void DespachoForaDoPlanoAprovadoOuDoSnapshotEhRecusado()
    {
        var c = new Cenario();
        var (plano, etapas) = EmCurso(c);
        var valido = c.Despacho(plano, etapas[0], "pesq");

        // Unidade que não está em curso, ou de plano não aprovado.
        Assert.Equal(MotivoDaRecusaDeDelegacao.OrigemInvalida, Recusa(valido with { EtapaDaUnidade = etapas[1] }));
        var aguardando = PlanoDaCoordenacao.Inicial(c.Execucao, c.Coordenacao, c.Validado(Proposta), Agora);
        Assert.Equal(MotivoDaRecusaDeDelegacao.PlanoNaoAprovado, Recusa(valido with { Plano = aguardando }));
        // Outro destinatário que não o do papel da unidade, mesmo que seja participante.
        Assert.Equal(MotivoDaRecusaDeDelegacao.ForaDoSnapshot, Recusa(c.Despacho(plano, etapas[0], "rev")));
        // Uma versão mais nova do agente (por exemplo, com mais capacidades) não é a congelada: nunca amplia.
        var nova = c.Pesquisador.Agente.NovaVersao(Leitor(CapacidadeDoAgente.Pesquisar, CapacidadeDoAgente.Revisar), 1001, Agora);
        Assert.Equal(MotivoDaRecusaDeDelegacao.ForaDoSnapshot, Recusa(valido with { VersaoDestinataria = nova }));
        // O coordenador precisa ser o congelado.
        Assert.Equal(MotivoDaRecusaDeDelegacao.ForaDoSnapshot,
            Recusa(valido with { ParticipanteCoordenador = c.DoPapel("pesq") }));
        // Desativar o agente só restringe: o despacho é recusado.
        c.Pesquisador.Agente.Desativar(Agora);
        Assert.Equal(MotivoDaRecusaDeDelegacao.DestinatarioIndisponivel, Recusa(valido));
        c.Pesquisador.Agente.Reativar(Agora);
        // Repositório fora da interseção das versões congeladas; orçamento global de 4 por execução.
        Assert.Equal(MotivoDaRecusaDeDelegacao.RepositorioForaDaIntersecao,
            Recusa(valido with { RepositoriosDaOrigem = ["@outro"] }));
        Assert.Equal(MotivoDaRecusaDeDelegacao.OrcamentoExcedido, Recusa(c.Despacho(plano, etapas[0], "pesq", delegacoes: 4)));
        // Execução que não está em andamento.
        c.Execucao.Cancelar("parou", Agora);
        Assert.Equal(MotivoDaRecusaDeDelegacao.OrigemInvalida, Recusa(valido));
    }

    [Fact]
    public void PlanoSubstituidoNaoDespachaMais()
    {
        var c = new Cenario();
        var (plano, etapas) = EmCurso(c);
        etapas[0].Falhar("sem resposta", null, Agora);
        PlanoDaCoordenacao.Revisar(c.Execucao, plano, MotivoDoPlano.UnidadeSemResposta, c.Validado(Unidade(Valida)), etapas,
            null, Agora);
        var ignorada = etapas[1];
        Assert.Equal(EstadoDaEtapa.Ignorada, ignorada.Estado);
        Assert.Throws<TransicaoInvalidaException>(() => ignorada.TornarPronta([]));
        Assert.Equal(MotivoDaRecusaDeDelegacao.OrigemInvalida, Recusa(c.Despacho(plano, etapas[0], "pesq")));
    }
}
