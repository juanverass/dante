using Dante.Domain.Conhecimentos;

namespace Dante.Tests;

public sealed class ConhecimentoTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static ProvenienciaDoConhecimento Origem(string origem = "registro humano") => new(Guid.NewGuid(), origem, "fonte:1", "v1", "trecho selecionado");
    private static Conhecimento Criar(TipoDeConhecimento tipo = TipoDeConhecimento.Fato,
        StatusDoConhecimento status = StatusDoConhecimento.Inferido, Guid? espaco = null, Guid? projeto = null) =>
        new(espaco ?? Guid.NewGuid(), projeto, tipo, " conteúdo ", null, status, .9, Sensibilidade.Pessoal,
            null, null, [" csharp ", "CSharp", "brain"], Origem(), Agora);

    [Theory]
    [InlineData(TipoDeConhecimento.Fato)] [InlineData(TipoDeConhecimento.Decisao)]
    [InlineData(TipoDeConhecimento.Preferencia)] [InlineData(TipoDeConhecimento.Instrucao)]
    [InlineData(TipoDeConhecimento.Nota)] [InlineData(TipoDeConhecimento.Referencia)]
    [InlineData(TipoDeConhecimento.Incidente)] [InlineData(TipoDeConhecimento.Solucao)]
    [InlineData(TipoDeConhecimento.Aprendizado)] [InlineData(TipoDeConhecimento.Procedimento)]
    [InlineData(TipoDeConhecimento.Resumo)] [InlineData(TipoDeConhecimento.Inferencia)]
    public void TiposSaoRepresentaveisSemConfirmacaoImplicita(TipoDeConhecimento tipo)
    {
        var conhecimento = Criar(tipo);
        Assert.NotEqual(Guid.Empty, conhecimento.Id);
        Assert.Equal(tipo, conhecimento.Tipo);
        Assert.Equal(StatusDoConhecimento.Inferido, conhecimento.Status);
        Assert.Equal("conteúdo", conhecimento.Conteudo);
        Assert.Equal(["csharp", "brain"], conhecimento.Tags);
        Assert.Equal(conhecimento.Proveniencia.IdResponsavel, conhecimento.IdAutor);
        Assert.Equal(1, conhecimento.Revisao);
        Assert.Equal(Agora, conhecimento.CriadoEm);
    }

    [Theory]
    [InlineData(StatusDoConhecimento.Confirmado)] [InlineData(StatusDoConhecimento.Substituido)]
    [InlineData(StatusDoConhecimento.Inativo)] [InlineData((StatusDoConhecimento)99)]
    public void CriacaoNaoAceitaEstadosQueExigemOperacaoEspecifica(StatusDoConhecimento status) =>
        Assert.Throws<ArgumentException>(() => Criar(status: status));

    [Fact]
    public void ConfiancaAltaNaoConfirmaInferencia()
    {
        var item = Criar(TipoDeConhecimento.Inferencia);
        Assert.Throws<InvalidOperationException>(() => item.Confirmar(1, Origem(), Agora));
        Assert.Equal(StatusDoConhecimento.Inferido, item.Status);
        Assert.Equal(1, item.Revisao);
    }

    [Fact]
    public void ConfirmacaoECorrecaoPreservamAutoriaEHistorico()
    {
        var item = Criar();
        var origemInicial = item.Proveniencia;
        var confirmador = Origem("confirmação humana");
        item.Confirmar(1, confirmador, Agora.AddMinutes(1));
        Assert.Equal(StatusDoConhecimento.Confirmado, item.Status);
        item.Corrigir(2, TipoDeConhecimento.Decisao, "corrigido", "{\"versao\":2}", null,
            Sensibilidade.Confidencial, null, null, ["novo"], Origem("correção"), Agora.AddMinutes(2));
        Assert.Equal(StatusDoConhecimento.Inferido, item.Status);
        Assert.Equal(3, item.Revisao);
        Assert.Equal(origemInicial.IdResponsavel, item.IdAutor);
        Assert.Equal(origemInicial, item.Historico[0].Proveniencia);
        Assert.Equal("conteúdo", item.Historico[0].Conteudo);
        Assert.Equal(confirmador, item.Historico[1].Proveniencia);
        Assert.Equal(StatusDoConhecimento.Confirmado, item.Historico[1].Status);
        Assert.Equal(Agora, item.CriadoEm);
        Assert.Equal(Agora.AddMinutes(2), item.AtualizadoEm);
        Assert.Equal("corrigido", item.Conteudo);
    }

    [Fact]
    public void ReclassificarInferenciaExigeNovaConfirmacao()
    {
        var item = Criar(TipoDeConhecimento.Inferencia);
        item.Corrigir(1, TipoDeConhecimento.Fato, "comprovado", null, 1, Sensibilidade.Pessoal,
            null, null, [], Origem("evidência"), Agora);
        Assert.Equal(StatusDoConhecimento.Inferido, item.Status);
        item.Confirmar(2, Origem("verificação"), Agora);
        Assert.Equal(StatusDoConhecimento.Confirmado, item.Status);
    }

    [Fact]
    public void CorrecaoInvalidaOuObsoletaNaoAlteraNenhumCampo()
    {
        var item = Criar();
        var antes = item.Historico[0];
        Assert.Throws<ArgumentException>(() => item.Corrigir(1, TipoDeConhecimento.Nota, "novo", "invalido", .1,
            Sensibilidade.Publico, null, null, [], Origem(), Agora));
        Assert.Throws<InvalidOperationException>(() => item.Invalidar(0, Origem(), Agora));
        Assert.Throws<ArgumentException>(() => item.Invalidar(1, Origem(), Agora.AddMinutes(-1)));
        Assert.Equal(antes, Assert.Single(item.Historico));
        Assert.Equal(antes.Conteudo, item.Conteudo);
        Assert.Equal(antes.Tipo, item.Tipo);
    }

    [Fact]
    public void SubstituicaoPreservaOrigemEConteudoDoAnterior()
    {
        var item = Criar();
        var substituto = Criar(espaco: item.IdEspacoDeConhecimento);
        item.SubstituirPor(substituto, 1, Origem("substituição"), Agora);
        Assert.Equal(StatusDoConhecimento.Substituido, item.Status);
        Assert.Equal(substituto.Id, item.IdConhecimentoSubstituto);
        Assert.Equal("conteúdo", item.Conteudo);
        Assert.Equal(2, item.Historico.Count);
        Assert.Equal(StatusDoConhecimento.Inferido, item.Historico[0].Status);
        Assert.False(item.EstaValidoEm(Agora));
        Assert.Throws<InvalidOperationException>(() => item.Confirmar(2, Origem(), Agora));
        Assert.Throws<ArgumentException>(() => substituto.SubstituirPor(item, 1, Origem(), Agora));
    }

    [Fact]
    public void SubstitutoNaoPodeSerProprioNemDeOutroEscopo()
    {
        var item = Criar();
        Assert.Throws<ArgumentException>(() => item.SubstituirPor(item, 1, Origem(), Agora));
        Assert.Throws<ArgumentException>(() => item.SubstituirPor(Criar(), 1, Origem(), Agora));
        Assert.Throws<ArgumentException>(() => item.SubstituirPor(Criar(espaco: item.IdEspacoDeConhecimento, projeto: Guid.NewGuid()), 1, Origem(), Agora));
        Assert.Equal(1, item.Revisao);
    }

    [Fact]
    public void InvalidacaoNaoApagaEImpedeNovasMutacoes()
    {
        var item = Criar(); item.Invalidar(1, Origem(), Agora);
        Assert.Equal(StatusDoConhecimento.Inativo, item.Status);
        Assert.Equal("conteúdo", item.Conteudo);
        Assert.False(item.EstaValidoEm(Agora));
        Assert.Throws<InvalidOperationException>(() => item.Invalidar(2, Origem(), Agora));
    }

    [Fact]
    public void ValidadeTemLimitesIndependentesDoStatus()
    {
        var item = new Conhecimento(Guid.NewGuid(), null, TipoDeConhecimento.Nota, "temporária", null,
            StatusDoConhecimento.Temporario, null, Sensibilidade.Trabalho, Agora, Agora.AddHours(1), [], Origem(), Agora);
        Assert.False(item.EstaValidoEm(Agora.AddTicks(-1)));
        Assert.True(item.EstaValidoEm(Agora));
        Assert.False(item.EstaValidoEm(Agora.AddHours(1)));
        Assert.Equal(StatusDoConhecimento.Temporario, item.Status);
        Assert.True(Criar().EstaValidoEm(Agora.AddYears(10)));
    }

    [Theory]
    [InlineData(-.1)] [InlineData(1.1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void ConfiancaInvalidaERecusada(double valor) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        new Conhecimento(Guid.NewGuid(), null, TipoDeConhecimento.Nota, "texto", null, StatusDoConhecimento.Inferido,
            valor, Sensibilidade.Pessoal, null, null, [], Origem(), Agora));

    [Fact]
    public void ValidaEscopoConteudoJsonTagsClassificacaoEIntervalo()
    {
        Conhecimento Novo(Guid espaco, TipoDeConhecimento tipo = TipoDeConhecimento.Nota, string? conteudo = "texto",
            string? json = null, Sensibilidade sensibilidade = Sensibilidade.Pessoal, DateTimeOffset? desde = null,
            DateTimeOffset? ate = null, string[]? tags = null) => new(espaco, null, tipo, conteudo, json,
                StatusDoConhecimento.Inferido, null, sensibilidade, desde, ate, tags, Origem(), Agora);
        Assert.Throws<ArgumentException>(() => Novo(Guid.Empty));
        Assert.Throws<ArgumentException>(() => Novo(Guid.NewGuid(), tipo: (TipoDeConhecimento)99));
        Assert.Throws<ArgumentException>(() => Novo(Guid.NewGuid(), sensibilidade: (Sensibilidade)99));
        Assert.Throws<ArgumentException>(() => Novo(Guid.NewGuid(), conteudo: " "));
        Assert.Throws<ArgumentException>(() => Novo(Guid.NewGuid(), json: "[]"));
        Assert.Throws<ArgumentException>(() => Novo(Guid.NewGuid(), json: "null"));
        Assert.Throws<ArgumentException>(() => Novo(Guid.NewGuid(), desde: Agora, ate: Agora));
        Assert.Throws<ArgumentException>(() => Novo(Guid.NewGuid(), tags: ["\n"]));
        Assert.Throws<ArgumentException>(() => Novo(Guid.NewGuid(), tags: Enumerable.Range(0, 51).Select(i => i.ToString()).ToArray()));
        Assert.Null(Novo(Guid.NewGuid(), conteudo: null, json: "{\"chave\":1}").Conteudo);
    }

    [Fact]
    public void ColecoesNaoPermitemAlterarVersoesCanonicas()
    {
        var tags = new[] { "original" };
        var item = new Conhecimento(Guid.NewGuid(), null, TipoDeConhecimento.Nota, "texto", null,
            StatusDoConhecimento.Inferido, null, Sensibilidade.Pessoal, null, null, tags, Origem(), Agora);
        tags[0] = "mutado";
        Assert.Equal("original", item.Tags[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)item.Tags)[0] = "mutado");
        Assert.Throws<NotSupportedException>(() => ((IList<RevisaoDoConhecimento>)item.Historico).Clear());
    }

    [Fact]
    public void ProvenienciaExigeResponsavelOrigemEReferenciaParaTrecho()
    {
        Assert.Throws<ArgumentException>(() => new ProvenienciaDoConhecimento(Guid.Empty, "origem"));
        Assert.Throws<ArgumentException>(() => new ProvenienciaDoConhecimento(Guid.NewGuid(), " "));
        Assert.Throws<ArgumentException>(() => new ProvenienciaDoConhecimento(Guid.NewGuid(), "origem", trechoDaFonte: "trecho"));
    }
}
