using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dante.Domain.Execucoes;

namespace Dante.Tests;

// #311 (AD-72), PR 1: aprovação de ação protegida no domínio. Catálogo fechado, payload canônico com hash calculado
// aqui, decisão única só do dono da raiz e antes de expirar, execução única só com o payload atual igual ao aprovado,
// Incerto terminal, sem repetição.
public sealed class AprovacaoDeAcaoDominioTests
{
    internal const string Canal = "telegram";
    internal const long Dono = 1001;
    internal static readonly DateTimeOffset Agora = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    internal static readonly string Artefato = new('a', 64);
    internal static readonly string Base = new('b', 40);

    internal static string Parametros(string? artefato = null, string mensagem = "feat: entrega aprovada\n\nCorpo.",
        string branch = "dante/e12-roteiro") => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["mensagem"] = mensagem,
        ["branch"] = branch,
        ["base"] = Base,
        ["autor_nome"] = "Pessoa Dona",
        ["autor_email"] = "dona@example.com",
        ["artefato"] = artefato ?? Artefato
    });

    internal static PayloadDaAcao Payload(Execucao execucao, string? artefato = null, string alvo = "@dante",
        string[]? capacidades = null, SolicitanteDaAcao? solicitante = null, Guid? idEtapa = null) =>
        PayloadDaAcao.Montar(CatalogoDeAcoes.EntregarArtefatoDoRoteiro, execucao.Id, idEtapa, alvo, capacidades ?? [],
            solicitante ?? SolicitanteDaAcao.Dono(execucao.Canal, execucao.IdDoUsuarioNoCanal), Parametros(artefato));

    private static Execucao Raiz(long dono = Dono) => new(1, TipoDeExecucao.Avulsa, Canal, dono, "{}", null, Agora);

    private static AprovacaoDeAcao Pedido(Execucao? raiz = null, TimeSpan? validade = null)
    {
        raiz ??= Raiz();
        return AprovacaoDeAcao.Solicitar(Payload(raiz), raiz, raiz, null, Agora, validade);
    }

    [Fact]
    public void CatalogoEFechadoENegaODesconhecido()
    {
        Assert.Same(CatalogoDeAcoes.EntregarArtefatoDoRoteiro, CatalogoDeAcoes.Obter("dante.entregar_artefato_do_roteiro"));
        Assert.Null(CatalogoDeAcoes.Obter("dante.Entregar_artefato_do_roteiro"));
        Assert.Null(CatalogoDeAcoes.Obter("dante.push"));
        Assert.Null(CatalogoDeAcoes.Obter(null));
        Assert.Equal(CatalogoDeAcoes.Todas.Count, CatalogoDeAcoes.Todas.Select(a => a.Nome).Distinct().Count());
        Assert.All(CatalogoDeAcoes.Todas, a => Assert.Matches("^[a-z][a-z0-9_]*\\.[a-z][a-z0-9_]*$", a.Nome));
        // As ferramentas atuais do dante_coordenacao: política Nunca, comportamento inalterado.
        Assert.Equal(["consultar_agente", "entregar_resultado", "entregar_sintese", "estado_da_execucao", "propor_plano"],
            CatalogoDeAcoes.Todas.Where(a => a.Nome.StartsWith(CatalogoDeAcoes.PrefixoDoCoordenacao, StringComparison.Ordinal))
                .Select(a => a.Nome[CatalogoDeAcoes.PrefixoDoCoordenacao.Length..]).Order(StringComparer.Ordinal));
        Assert.All(CatalogoDeAcoes.Todas.Where(a => a.Nome.StartsWith(CatalogoDeAcoes.PrefixoDoCoordenacao, StringComparison.Ordinal)),
            a => Assert.False(a.ExigeAprovacao(modoExigeAprovacao: true)));
        Assert.Equal([CatalogoDeAcoes.EntregarArtefatoDoRoteiro], CatalogoDeAcoes.Protegidas);
        var entrega = CatalogoDeAcoes.EntregarArtefatoDoRoteiro;
        Assert.Equal((ClasseDeRisco.EscritaInterna, PoliticaDeAprovacao.Sempre, true),
            (entrega.Risco, entrega.Politica, entrega.ExigeRepositorio));
        Assert.True(entrega.ExigeAprovacao(modoExigeAprovacao: false));
    }

    [Fact]
    public void PoliticaConformeModoSegueOQueQuemConheceOModoInforma()
    {
        var definicao = new DefinicaoDeAcao("teste.conforme_modo", ClasseDeRisco.Leitura, PoliticaDeAprovacao.ConformeModo,
            false, "x", []);
        Assert.True(definicao.Protegida);
        Assert.True(definicao.ExigeAprovacao(true));
        Assert.False(definicao.ExigeAprovacao(false));
        // Fora do catálogo, mesmo com a mesma forma: nenhum payload é montado.
        Assert.Throws<ArgumentException>(() => PayloadDaAcao.Montar(definicao, Guid.NewGuid(), null, null, [],
            SolicitanteDaAcao.Dono(Canal, Dono), "{}"));
    }

    [Fact]
    public void PayloadTemFormaCanonicaEHashDoServidorIndependenteDaOrdemENormalizacao()
    {
        var raiz = Raiz();
        var payload = Payload(raiz, capacidades: ["git.commit", "coordenacao"]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload.Conteudo))), payload.Hash);
        using var documento = JsonDocument.Parse(payload.Conteudo);
        var json = documento.RootElement;
        Assert.Equal(["acao", "alvo", "capacidades", "escopo", "etapa", "execucao", "formato", "parametros", "solicitante"],
            json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["artefato", "autor_email", "autor_nome", "base", "branch", "mensagem"],
            json.GetProperty("parametros").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["coordenacao", "git.commit"], json.GetProperty("capacidades").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(raiz.Id.ToString(), json.GetProperty("execucao").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("etapa").ValueKind);
        Assert.Equal("UmaVez", json.GetProperty("escopo").GetString());
        Assert.Equal(["canal", "tipo", "usuario"], json.GetProperty("solicitante").EnumerateObject().Select(p => p.Name));
        // Sem espaço fora dos valores: a forma é compacta.
        Assert.DoesNotContain("\": ", payload.Conteudo);
        Assert.DoesNotContain(", \"", payload.Conteudo);

        // Mesma intenção em outra ordem de chaves, com \r\n, espaços nas pontas e outra forma Unicode: mesmo hash.
        var outraOrdem = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["artefato"] = Artefato,
            ["autor_email"] = " dona@example.com ",
            ["autor_nome"] = "Pessoa Dona",
            ["base"] = Base,
            ["branch"] = "dante/e12-roteiro",
            ["mensagem"] = "feat: entrega aprovada\r\n\r\nCorpo.  "
        });
        var igual = PayloadDaAcao.Montar(CatalogoDeAcoes.EntregarArtefatoDoRoteiro, raiz.Id, null, "@dante",
            ["coordenacao", "git.commit"], SolicitanteDaAcao.Dono(Canal, Dono), outraOrdem);
        Assert.Equal(payload.Hash, igual.Hash);
        Assert.Equal("feat: entrega aprovada\n\nCorpo.", PayloadDaAcao.Parametro(payload.Conteudo, "mensagem"));
        // NFD e NFC do mesmo nome: mesmo payload.
        string ComNome(string nome)
        {
            var p = JsonSerializer.Deserialize<Dictionary<string, string>>(Parametros())!;
            p["autor_nome"] = nome;
            return PayloadDaAcao.Montar(CatalogoDeAcoes.EntregarArtefatoDoRoteiro, raiz.Id, null, "@dante", [],
                SolicitanteDaAcao.Dono(Canal, Dono), JsonSerializer.Serialize(p)).Hash;
        }
        Assert.Equal(ComNome("José".Normalize(NormalizationForm.FormC)), ComNome("José".Normalize(NormalizationForm.FormD)));
    }

    [Fact]
    public void QualquerCampoRelevanteMudaOHash()
    {
        var raiz = Raiz();
        var original = Payload(raiz).Hash;
        var outraRaiz = Raiz();
        var versao = Guid.NewGuid();
        string[] variantes =
        [
            Payload(raiz, artefato: new string('c', 64)).Hash,
            Payload(raiz, alvo: "@outro").Hash,
            Payload(raiz, capacidades: ["git.commit"]).Hash,
            Payload(raiz, idEtapa: Guid.NewGuid()).Hash,
            Payload(outraRaiz).Hash,
            Payload(raiz, solicitante: SolicitanteDaAcao.Dono(Canal, 999)).Hash,
            Payload(raiz, solicitante: SolicitanteDaAcao.Agente(versao, new string('d', 64))).Hash,
            Payload(raiz, solicitante: SolicitanteDaAcao.Agente(versao, new string('e', 64))).Hash,
            PayloadDaAcao.Montar(CatalogoDeAcoes.EntregarArtefatoDoRoteiro, raiz.Id, null, "@dante", [],
                SolicitanteDaAcao.Dono(Canal, Dono), Parametros(mensagem: "outra mensagem")).Hash,
            PayloadDaAcao.Montar(CatalogoDeAcoes.EntregarArtefatoDoRoteiro, raiz.Id, null, "@dante", [],
                SolicitanteDaAcao.Dono(Canal, Dono), Parametros(branch: "dante/outra")).Hash
        ];
        Assert.DoesNotContain(original, variantes);
        Assert.Equal(variantes.Length, variantes.Distinct().Count());
    }

    [Theory]
    [InlineData("{\"artefato\":\"x\"}")]
    [InlineData("[]")]
    [InlineData("nao e json")]
    [InlineData("")]
    public void ParametrosForaDaFormaSaoRecusados(string json)
    {
        var raiz = Raiz();
        Assert.Throws<ArgumentException>(() => PayloadDaAcao.Montar(CatalogoDeAcoes.EntregarArtefatoDoRoteiro, raiz.Id, null,
            "@dante", [], SolicitanteDaAcao.Dono(Canal, Dono), json));
    }

    [Theory]
    [InlineData("artefato", "ABCDEF")]
    [InlineData("artefato", "gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("base", "123")]
    [InlineData("branch", "../main")]
    [InlineData("branch", "a..b")]
    [InlineData("branch", "x.lock")]
    [InlineData("branch", "-x")]
    [InlineData("branch", "a b")]
    [InlineData("branch", "x/")]
    [InlineData("autor_email", "sem-arroba")]
    [InlineData("autor_email", "a <b@c.d>")]
    [InlineData("autor_nome", "linha\nquebrada")]
    [InlineData("autor_nome", "")]
    [InlineData("mensagem", "com \u0007 controle")]
    public void ValorForaDaFormaDoCatalogoERecusado(string campo, string valor)
    {
        var raiz = Raiz();
        var parametros = JsonSerializer.Deserialize<Dictionary<string, string>>(Parametros())!;
        parametros[campo] = valor;
        var erro = Assert.Throws<ArgumentException>(() => PayloadDaAcao.Montar(CatalogoDeAcoes.EntregarArtefatoDoRoteiro,
            raiz.Id, null, "@dante", [], SolicitanteDaAcao.Dono(Canal, Dono), JsonSerializer.Serialize(parametros)));
        Assert.Contains(campo, erro.Message);
    }

    [Fact]
    public void CampoDesconhecidoRepetidoAusenteOuHashInformadoSaoRecusados()
    {
        var raiz = Raiz();
        var parametros = JsonSerializer.Deserialize<Dictionary<string, string>>(Parametros())!;
        PayloadDaAcao Montar(string json) => PayloadDaAcao.Montar(CatalogoDeAcoes.EntregarArtefatoDoRoteiro, raiz.Id, null,
            "@dante", [], SolicitanteDaAcao.Dono(Canal, Dono), json);
        Assert.Contains("hash", Assert.Throws<ArgumentException>(() =>
            Montar(JsonSerializer.Serialize(new Dictionary<string, string>(parametros) { ["hash"] = new string('a', 64) }))).Message);
        parametros.Remove("base");
        Assert.Contains("base", Assert.Throws<ArgumentException>(() => Montar(JsonSerializer.Serialize(parametros))).Message);
        Assert.Contains("repetido", Assert.Throws<ArgumentException>(() =>
            Montar(Parametros()[..^1] + ",\"base\":\"" + Base + "\"}")).Message);
        // Alvo obrigatório e na forma de alias; capacidades válidas e sem repetição.
        Assert.Throws<ArgumentException>(() => PayloadDaAcao.Montar(CatalogoDeAcoes.EntregarArtefatoDoRoteiro, raiz.Id, null,
            null, [], SolicitanteDaAcao.Dono(Canal, Dono), Parametros()));
        Assert.Throws<ArgumentException>(() => Payload(raiz, alvo: "/srv/projects/dante"));
        Assert.Throws<ArgumentException>(() => Payload(raiz, capacidades: ["git", "git"]));
        Assert.Throws<ArgumentException>(() => Payload(raiz, capacidades: ["Com Espaço"]));
        Assert.Throws<ArgumentException>(() => Payload(raiz, idEtapa: Guid.Empty));
        // Ação de política Nunca não tem payload a aprovar.
        Assert.Throws<ArgumentException>(() => PayloadDaAcao.Montar(CatalogoDeAcoes.ConsultarAgente, raiz.Id, null, null, [],
            SolicitanteDaAcao.Dono(Canal, Dono), "{}"));
        Assert.Throws<ArgumentException>(() => SolicitanteDaAcao.Agente(Guid.NewGuid(), "curto"));
    }

    [Fact]
    public void PedidoNasceDoPayloadDoServidorPendenteComValidadePadraoESoDoDonoDaRaiz()
    {
        var raiz = Raiz();
        var pedido = Pedido(raiz);
        Assert.Equal((EstadoDaAprovacaoDeAcao.Pendente, Agora + TimeSpan.FromHours(24), EscopoDaAprovacao.UmaVez),
            (pedido.Estado, pedido.ExpiraEm, pedido.Escopo));
        Assert.Equal((raiz.Id, raiz.Id, Canal, Dono, "@dante"),
            (pedido.IdExecucao, pedido.IdExecucaoRaiz, pedido.CanalDoDono, pedido.IdDoDono, pedido.Alvo));
        Assert.Equal((CatalogoDeAcoes.EntregarArtefatoDoRoteiro.Nome, ClasseDeRisco.EscritaInterna), (pedido.Acao, pedido.Risco));
        Assert.Equal(Payload(raiz).Hash, pedido.Hash);
        Assert.Null(pedido.IdVersaoDoSolicitante);
        Assert.Equal(TimeSpan.FromMinutes(5), AprovacaoDeAcao.Solicitar(Payload(raiz), raiz, raiz, null, Agora,
            TimeSpan.FromMinutes(5)).ExpiraEm - Agora);
        Assert.Throws<ArgumentOutOfRangeException>(() => AprovacaoDeAcao.Solicitar(Payload(raiz), raiz, raiz, null, Agora,
            TimeSpan.Zero));
        // Payload de outra execução, raiz que não é a da execução, etapa de outra execução: recusados.
        var outra = Raiz();
        Assert.Throws<InvalidOperationException>(() => AprovacaoDeAcao.Solicitar(Payload(outra), raiz, raiz, null, Agora));
        Assert.Throws<InvalidOperationException>(() => AprovacaoDeAcao.Solicitar(Payload(raiz), raiz, outra, null, Agora));
        var etapaDeOutra = new EtapaDaExecucao(outra, 1, TipoDeEtapa.ProcessoAvulso, false, PoliticaDeRetomada.Nunca, Agora);
        Assert.Throws<InvalidOperationException>(() => AprovacaoDeAcao.Solicitar(
            Payload(raiz, idEtapa: etapaDeOutra.Id), raiz, raiz, etapaDeOutra, Agora));
        Assert.Throws<InvalidOperationException>(() => AprovacaoDeAcao.Solicitar(
            Payload(raiz, idEtapa: Guid.NewGuid()), raiz, raiz, null, Agora));
        // Um "dono" que não é o da raiz não pede em nome próprio.
        Assert.Throws<InvalidOperationException>(() => AprovacaoDeAcao.Solicitar(
            Payload(raiz, solicitante: SolicitanteDaAcao.Dono(Canal, 999)), raiz, raiz, null, Agora));
        // Solicitante agente: versão e hash gravados; o dono continua o da raiz.
        var versao = Guid.NewGuid();
        var doAgente = AprovacaoDeAcao.Solicitar(Payload(raiz, solicitante: SolicitanteDaAcao.Agente(versao, new string('d', 64))),
            raiz, raiz, null, Agora);
        Assert.Equal((versao, new string('d', 64), Dono), (doAgente.IdVersaoDoSolicitante, doAgente.HashDaVersaoDoSolicitante,
            doAgente.IdDoDono));
    }

    [Fact]
    public void SoODonoAprovaComOHashExibidoAntesDeExpirarEUmaVezSo()
    {
        var pedido = Pedido();
        Assert.Throws<InvalidOperationException>(() => pedido.Aprovar(Canal, 999, pedido.Hash, Agora));
        Assert.Throws<InvalidOperationException>(() => pedido.Aprovar("outro", Dono, pedido.Hash, Agora));
        Assert.Throws<InvalidOperationException>(() => pedido.Aprovar(Canal, Dono, pedido.Hash[..11], Agora));
        Assert.Throws<InvalidOperationException>(() => pedido.Aprovar(Canal, Dono, new string('f', 64), Agora));
        Assert.Throws<InvalidOperationException>(() => pedido.Aprovar(Canal, Dono, null, Agora));
        Assert.Throws<InvalidOperationException>(() => pedido.Aprovar(Canal, Dono, pedido.Hash, pedido.ExpiraEm));
        Assert.Equal(EstadoDaAprovacaoDeAcao.Pendente, pedido.Estado);

        pedido.Aprovar(Canal, Dono, pedido.Hash[..12].ToUpperInvariant(), Agora.AddMinutes(1));
        Assert.Equal((EstadoDaAprovacaoDeAcao.Aprovada, Canal, Dono, Agora.AddMinutes(1)),
            (pedido.Estado, pedido.CanalDoDecisor, pedido.IdDoDecisor, pedido.DecididaEm));
        Assert.Null(pedido.EncerradaEm);
        // Segunda decisão (replay da aprovação ou recusa depois): recusada sem mudar nada.
        Assert.Throws<TransicaoInvalidaException>(() => pedido.Aprovar(Canal, Dono, pedido.Hash, Agora.AddMinutes(2)));
        Assert.Throws<TransicaoInvalidaException>(() => pedido.Recusar(Canal, Dono, "não", Agora.AddMinutes(2)));
        Assert.Equal(Agora.AddMinutes(1), pedido.DecididaEm);
    }

    [Fact]
    public void RecusaDoDonoEncerraSemAutorizarNada()
    {
        var pedido = Pedido();
        Assert.Throws<InvalidOperationException>(() => pedido.Recusar(Canal, 999, "não", Agora));
        pedido.Recusar(Canal, Dono, "não agora", Agora.AddMinutes(1));
        Assert.Equal((EstadoDaAprovacaoDeAcao.Recusada, "não agora", Agora.AddMinutes(1)),
            (pedido.Estado, pedido.Motivo, pedido.EncerradaEm));
        Assert.True(pedido.Terminal);
        Assert.Throws<TransicaoInvalidaException>(() => pedido.IniciarExecucao(Payload(Raiz()), Agora));
        Assert.Throws<TransicaoInvalidaException>(() => pedido.Cancelar("x", Agora));
    }

    [Fact]
    public void ExpiraPendenteOuAprovadaSoDepoisDoPrazo()
    {
        var pendente = Pedido();
        Assert.Throws<InvalidOperationException>(() => pendente.Expirar(pendente.ExpiraEm.AddTicks(-1)));
        pendente.Expirar(pendente.ExpiraEm);
        Assert.Equal((EstadoDaAprovacaoDeAcao.Expirada, pendente.ExpiraEm), (pendente.Estado, pendente.EncerradaEm));
        Assert.Throws<TransicaoInvalidaException>(() => pendente.Aprovar(Canal, Dono, pendente.Hash, Agora));

        var aprovada = Pedido();
        aprovada.Aprovar(Canal, Dono, aprovada.Hash, Agora);
        aprovada.Expirar(aprovada.ExpiraEm.AddHours(1));
        Assert.Equal(EstadoDaAprovacaoDeAcao.Expirada, aprovada.Estado);
        Assert.NotNull(aprovada.DecididaEm);
    }

    [Fact]
    public void ExecucaoUnicaSoComOPayloadAtualIgualAoAprovadoEAntesDoPrazo()
    {
        var raiz = Raiz();
        var pedido = AprovacaoDeAcao.Solicitar(Payload(raiz), raiz, raiz, null, Agora);
        // Sem aprovação, não começa.
        Assert.Throws<TransicaoInvalidaException>(() => pedido.IniciarExecucao(Payload(raiz), Agora));
        pedido.Aprovar(Canal, Dono, pedido.Hash, Agora);
        Assert.Equal(InicioDaExecucaoDaAcao.Iniciada, pedido.IniciarExecucao(Payload(raiz), Agora.AddMinutes(1)));
        Assert.Equal((EstadoDaAprovacaoDeAcao.EmExecucao, Agora.AddMinutes(1)), (pedido.Estado, pedido.IniciadaEm));
        // Segunda execução da mesma aprovação: recusada.
        Assert.Throws<TransicaoInvalidaException>(() => pedido.IniciarExecucao(Payload(raiz), Agora.AddMinutes(2)));
        pedido.Concluir("commit " + new string('1', 40), Agora.AddMinutes(2));
        Assert.Equal((EstadoDaAprovacaoDeAcao.Executada, "commit " + new string('1', 40)), (pedido.Estado, pedido.Resultado));
        Assert.True(pedido.Terminal);
        Assert.Throws<TransicaoInvalidaException>(() => pedido.IniciarExecucao(Payload(raiz), Agora.AddMinutes(3)));
    }

    [Theory]
    [InlineData("artefato")]
    [InlineData("capacidade")]
    [InlineData("versao")]
    [InlineData("alvo")]
    public void PayloadAtualDiferenteCancelaAAprovacaoSemExecutar(string mudanca)
    {
        var raiz = Raiz();
        var versao = Guid.NewGuid();
        var solicitante = SolicitanteDaAcao.Agente(versao, new string('d', 64));
        var pedido = AprovacaoDeAcao.Solicitar(Payload(raiz, solicitante: solicitante, capacidades: ["git.commit"]), raiz, raiz,
            null, Agora);
        pedido.Aprovar(Canal, Dono, pedido.Hash, Agora);
        // Os fatos de agora: o conteúdo da worktree mudou, a concessão foi revogada, o agente ganhou versão nova ou o alvo
        // é outro. Qualquer um invalida a aprovação.
        var atual = mudanca switch
        {
            "artefato" => Payload(raiz, artefato: new string('c', 64), solicitante: solicitante, capacidades: ["git.commit"]),
            "capacidade" => Payload(raiz, solicitante: solicitante, capacidades: []),
            "versao" => Payload(raiz, solicitante: SolicitanteDaAcao.Agente(Guid.NewGuid(), new string('d', 64)),
                capacidades: ["git.commit"]),
            _ => Payload(raiz, alvo: "@outro", solicitante: solicitante, capacidades: ["git.commit"])
        };
        Assert.Equal(InicioDaExecucaoDaAcao.PayloadAlterado, pedido.IniciarExecucao(atual, Agora.AddMinutes(1)));
        Assert.Equal(EstadoDaAprovacaoDeAcao.Cancelada, pedido.Estado);
        Assert.Null(pedido.IniciadaEm);
        Assert.Contains("diferente do aprovado", pedido.Motivo);
    }

    [Fact]
    public void AprovacaoVencidaExpiraNoInicioEmVezDeExecutar()
    {
        var raiz = Raiz();
        var pedido = AprovacaoDeAcao.Solicitar(Payload(raiz), raiz, raiz, null, Agora, TimeSpan.FromMinutes(10));
        pedido.Aprovar(Canal, Dono, pedido.Hash, Agora);
        Assert.Equal(InicioDaExecucaoDaAcao.Expirada, pedido.IniciarExecucao(Payload(raiz), Agora.AddMinutes(10)));
        Assert.Equal(EstadoDaAprovacaoDeAcao.Expirada, pedido.Estado);
        Assert.Null(pedido.IniciadaEm);
    }

    [Fact]
    public void RevogacaoCancelaPendenteOuAprovadaComMotivo()
    {
        var pendente = Pedido();
        Assert.Throws<ArgumentException>(() => pendente.Cancelar(" ", Agora));
        pendente.Cancelar("Agente desativado.", Agora.AddMinutes(1));
        Assert.Equal((EstadoDaAprovacaoDeAcao.Cancelada, "Agente desativado."), (pendente.Estado, pendente.Motivo));
        Assert.Null(pendente.DecididaEm);

        var aprovada = Pedido();
        aprovada.Aprovar(Canal, Dono, aprovada.Hash, Agora);
        aprovada.Cancelar("Execução cancelada pelo dono.", Agora.AddMinutes(1));
        Assert.Equal(EstadoDaAprovacaoDeAcao.Cancelada, aprovada.Estado);
        Assert.Throws<TransicaoInvalidaException>(() => aprovada.IniciarExecucao(Payload(Raiz()), Agora.AddMinutes(2)));

        // Em execução já não se revoga: o resultado é Executada, Falhou ou Incerto.
        var emExecucao = EmExecucao();
        Assert.Throws<TransicaoInvalidaException>(() => emExecucao.Cancelar("x", Agora.AddMinutes(2)));
    }

    [Fact]
    public void IncertoETerminalSemRepeticaoERelataOEstadoObservado()
    {
        var pedido = EmExecucao();
        Assert.Throws<ArgumentException>(() => pedido.MarcarIncerta("", null, Agora.AddMinutes(2)));
        pedido.MarcarIncerta("Worker reiniciado durante a execução.", "HEAD da branch: " + new string('2', 40),
            Agora.AddMinutes(2));
        Assert.Equal((EstadoDaAprovacaoDeAcao.Incerto, "Worker reiniciado durante a execução.", "HEAD da branch: " + new string('2', 40)),
            (pedido.Estado, pedido.Motivo, pedido.Resultado));
        Assert.True(pedido.Terminal);
        foreach (var para in Enum.GetValues<EstadoDaAprovacaoDeAcao>())
            Assert.False(AprovacaoDeAcao.PodeTransitar(EstadoDaAprovacaoDeAcao.Incerto, para));
        Assert.Throws<TransicaoInvalidaException>(() => pedido.Concluir("x", Agora.AddMinutes(3)));
        Assert.Throws<TransicaoInvalidaException>(() => pedido.Falhar("x", Agora.AddMinutes(3)));
    }

    [Fact]
    public void FalhaComprovadaEncerraComMotivo()
    {
        var pedido = EmExecucao();
        Assert.Throws<ArgumentException>(() => pedido.Falhar(" ", Agora.AddMinutes(2)));
        pedido.Falhar("Worktree ausente.", Agora.AddMinutes(2));
        Assert.Equal((EstadoDaAprovacaoDeAcao.Falhou, "Worktree ausente."), (pedido.Estado, pedido.Motivo));
        Assert.True(pedido.Terminal);
    }

    [Fact]
    public void TabelaDeTransicoesEAMesmaDoTriggerDoBanco()
    {
        var permitidas = Enum.GetValues<EstadoDaAprovacaoDeAcao>()
            .SelectMany(de => Enum.GetValues<EstadoDaAprovacaoDeAcao>().Where(para => AprovacaoDeAcao.PodeTransitar(de, para))
                .Select(para => ((int)de, (int)para)))
            .ToHashSet();
        // A lista literal do trigger proteger_aprovacao_de_acao (migration AprovacoesDeAcao).
        Assert.Equal(new HashSet<(int, int)> { (0, 1), (0, 2), (0, 3), (0, 4), (1, 5), (1, 3), (1, 4), (5, 6), (5, 7), (5, 8) },
            permitidas);
    }

    internal static AprovacaoDeAcao EmExecucao()
    {
        var raiz = Raiz();
        var pedido = AprovacaoDeAcao.Solicitar(Payload(raiz), raiz, raiz, null, Agora);
        pedido.Aprovar(Canal, Dono, pedido.Hash, Agora);
        Assert.Equal(InicioDaExecucaoDaAcao.Iniciada, pedido.IniciarExecucao(Payload(raiz), Agora.AddMinutes(1)));
        return pedido;
    }
}
