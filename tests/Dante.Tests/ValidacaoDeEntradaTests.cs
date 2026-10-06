using Dante.Application.BuscaDoBrain;
using Dante.Application.Comum;
using Dante.Application.Conhecimentos;
using Dante.Application.ConstrucaoDeContexto;
using Dante.Application.ConversaDoBrain;
using Dante.Application.DocumentosFonte;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Projetos;
using Dante.Application.QualidadeDoBrain;
using Dante.Domain.Conhecimentos;

namespace Dante.Tests;

// #205: validação de entrada é pura e testável sem repository, unit of work ou banco.
public sealed class ValidacaoDeEntradaTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void FaixaAceitaOsLimitesInclusivos(int valor) => ValidacaoDeEntrada.ExigirFaixa(valor, 1, 100, "limite");

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void FaixaRecusaForaDosLimitesComONomeDoParametro(int valor) =>
        Assert.Equal("limite", Assert.Throws<ArgumentOutOfRangeException>(() => ValidacaoDeEntrada.ExigirFaixa(valor, 1, 100, "limite")).ParamName);

    [Fact]
    public void IdEEscopoRecusamGuidVazio()
    {
        var erro = Assert.Throws<ArgumentException>(() => ValidacaoDeEntrada.ExigirId(Guid.Empty, "Obrigatório.", "filtro"));
        Assert.Equal("filtro", erro.ParamName);
        Assert.StartsWith("Obrigatório.", erro.Message);
        ValidacaoDeEntrada.ExigirId(Guid.NewGuid(), "Obrigatório.");
        ValidacaoDeEntrada.ExigirEscopo(Guid.NewGuid(), null);
        ValidacaoDeEntrada.ExigirEscopo(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal("Escopo inválido.", Assert.Throws<ArgumentException>(() => ValidacaoDeEntrada.ExigirEscopo(Guid.Empty, null)).Message);
        Assert.Equal("Escopo inválido.", Assert.Throws<ArgumentException>(() => ValidacaoDeEntrada.ExigirEscopo(Guid.NewGuid(), Guid.Empty)).Message);
    }

    [Fact]
    public void PesquisasDeEspacoEProjetoExigemDonoELimite()
    {
        EspacoDeConhecimentoValidator.ValidarPesquisa(new(Guid.NewGuid()));
        ProjetoValidator.ValidarPesquisa(new(Guid.NewGuid()));
        Assert.Equal("filtro", Assert.Throws<ArgumentException>(() => EspacoDeConhecimentoValidator.ValidarPesquisa(new(Guid.Empty))).ParamName);
        Assert.Equal("filtro", Assert.Throws<ArgumentException>(() => ProjetoValidator.ValidarPesquisa(new(Guid.Empty))).ParamName);
        Assert.Equal("Limite", Assert.Throws<ArgumentOutOfRangeException>(() =>
            EspacoDeConhecimentoValidator.ValidarPesquisa(new(Guid.NewGuid(), Limite: 101))).ParamName);
        Assert.Equal("Limite", Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProjetoValidator.ValidarPesquisa(new(Guid.NewGuid(), Limite: 0))).ParamName);
    }

    [Fact]
    public void PesquisaDeConhecimentoValidaEscopoClassificacaoLimiteValidadeETag()
    {
        var valido = new ConhecimentoSearchDto { IdEspacoDeConhecimento = Guid.NewGuid(), Tag = "  tag  " };
        ConhecimentoValidator.ValidarPesquisa(valido);
        AssertMensagem("Escopo de pesquisa inválido.", () => ConhecimentoValidator.ValidarPesquisa(valido with { IdProjeto = Guid.Empty }));
        AssertMensagem("Escopo de pesquisa inválido.", () => ConhecimentoValidator.ValidarPesquisa(valido with { IdProjeto = Guid.NewGuid(), SomenteSemProjeto = true }));
        AssertMensagem("Classificação de pesquisa inválida.", () => ConhecimentoValidator.ValidarPesquisa(valido with { Tipo = (TipoDeConhecimento)999 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConhecimentoValidator.ValidarPesquisa(valido with { Limite = 101 }));
        AssertMensagem("Instante de validade inválido.", () => ConhecimentoValidator.ValidarPesquisa(valido with { ValidoEm = default(DateTimeOffset) }));
        AssertMensagem("Tag de pesquisa inválida.", () => ConhecimentoValidator.ValidarPesquisa(valido with { Tag = new string('a', 101) }));
        AssertMensagem("Tag de pesquisa inválida.", () => ConhecimentoValidator.ValidarPesquisa(valido with { Tag = "a\u0001b" }));
    }

    [Fact]
    public void BuscaRecusaFiltrosEstruturalmenteInvalidos()
    {
        var valido = new BuscaDoBrainSearchDto { Texto = "deploy" };
        BuscaDoBrainValidator.ValidarBusca(valido);
        BuscaDoBrainValidator.ValidarBusca(new BuscaDoBrainSearchDto { IdConhecimento = Guid.NewGuid() });
        var agora = DateTimeOffset.UtcNow;
        foreach (var invalido in new[]
        {
            valido with { Limite = 0 }, valido with { Deslocamento = 10001 }, valido with { Texto = "" },
            valido with { Texto = new string('a', 2001) }, valido with { IdConhecimento = Guid.Empty },
            valido with { Tags = [" "] }, valido with { Tipos = [(TipoDeConhecimento)999] },
            valido with { CriadoDesde = agora, CriadoAte = agora }, valido with { Sensibilidade = (Sensibilidade)999 }
        })
            AssertMensagem("Filtro de busca inválido.", () => BuscaDoBrainValidator.ValidarBusca(invalido));
    }

    [Fact]
    public void PedidosDeContextoEConversaTemLimitesEstruturais()
    {
        ConstrucaoDeContextoValidator.ValidarPedido(new PedidoDeContextoDto { Mensagem = "retomar" });
        AssertMensagem("Limites do contexto inválidos.", () => ConstrucaoDeContextoValidator.ValidarPedido(new PedidoDeContextoDto { Mensagem = " " }));
        AssertMensagem("Limites do contexto inválidos.", () =>
            ConstrucaoDeContextoValidator.ValidarPedido(new PedidoDeContextoDto { Mensagem = "retomar", OrcamentoDeTokens = 63 }));
        AssertMensagem("Limites do contexto inválidos.", () =>
            ConstrucaoDeContextoValidator.ValidarPedido(new PedidoDeContextoDto { Mensagem = "retomar", ProfundidadeDeRelacoes = 4 }));
        ConversaDoBrainValidator.ValidarPedido(new PedidoDeConversaDto("conversa", "texto", "msg:1"));
        AssertMensagem("Conversa inválida.", () => ConversaDoBrainValidator.ValidarPedido(new PedidoDeConversaDto(" ", "texto", "msg:1")));
        AssertMensagem("Conversa inválida.", () =>
            ConversaDoBrainValidator.ValidarPedido(new PedidoDeConversaDto("conversa", new string('a', 10001), "msg:1")));
    }

    [Fact]
    public void FormatoDeFonteEConsolidacaoSaoValidadosPelaForma()
    {
        DocumentoFonteValidator.ValidarFormato("markdown");
        DocumentoFonteValidator.ValidarFormato("texto");
        AssertMensagem("Formato não suportado.", () => DocumentoFonteValidator.ValidarFormato("pdf"));
        var destino = new RevisaoEsperadaDto(Guid.NewGuid(), 1);
        var duplicata = new RevisaoEsperadaDto(Guid.NewGuid(), 1);
        ManutencaoDoBrainValidator.ValidarConsolidacao(destino, [duplicata]);
        const string mensagem = "Consolidação exige duplicatas distintas e limite de 20.";
        AssertMensagem(mensagem, () => ManutencaoDoBrainValidator.ValidarConsolidacao(destino, []));
        AssertMensagem(mensagem, () => ManutencaoDoBrainValidator.ValidarConsolidacao(destino, [duplicata, duplicata]));
        AssertMensagem(mensagem, () => ManutencaoDoBrainValidator.ValidarConsolidacao(destino, [destino]));
        AssertMensagem(mensagem, () => ManutencaoDoBrainValidator.ValidarConsolidacao(destino,
            Enumerable.Range(0, 21).Select(_ => new RevisaoEsperadaDto(Guid.NewGuid(), 1)).ToArray()));
    }

    // Validators são estáticos e sem estado: não recebem repository, unit of work nem contexto de banco.
    [Fact]
    public void ValidatorsSaoEstaticosSemEstadoEFicamNaFeature()
    {
        var validators = typeof(ValidacaoDeEntrada).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Validator", StringComparison.Ordinal) || t == typeof(ValidacaoDeEntrada)).ToArray();
        Assert.Equal(9, validators.Length);
        Assert.All(validators, t =>
        {
            Assert.True(t.IsAbstract && t.IsSealed && !t.IsPublic, $"{t.Name} deve ser internal static.");
            Assert.Empty(t.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public));
            Assert.DoesNotContain(t.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .SelectMany(m => m.GetParameters()), p => p.ParameterType.IsInterface && !p.ParameterType.IsGenericType);
        });
    }

    private static void AssertMensagem(string mensagem, Action acao) =>
        Assert.Equal(mensagem, Assert.Throws<ArgumentException>(acao).Message);
}
