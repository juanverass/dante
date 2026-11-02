using System.Text.Json;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;

namespace Dante.Tests;

public sealed class NaturezaDoConhecimentoTests
{
    private static readonly DateTimeOffset Agora = DateTimeOffset.UtcNow.AddMinutes(-5);
    internal static ProvenienciaDoConhecimento Prova => new(Guid.NewGuid(), "registro explícito", "mensagem:1", trechoDaFonte: "evidência");
    internal static Conhecimento Novo(NaturezaDoConteudo natureza, Guid? espaco = null, string texto = "conteúdo") =>
        new(espaco ?? Guid.NewGuid(), null, TipoDeConhecimento.Fato, texto, null, StatusDoConhecimento.Inferido,
            1, Sensibilidade.Pessoal, null, null, [], Prova, Agora, natureza);

    public static IEnumerable<object[]> Matriz =>
        from origem in Enum.GetValues<NaturezaDoConteudo>()
        from revisao in Enum.GetValues<NaturezaDoConteudo>()
        select new object[] { origem, revisao };

    [Theory]
    [MemberData(nameof(Matriz))]
    public void ConfirmacaoUnicaExigeOrigemERevisaoDeAutoriaDoUsuario(NaturezaDoConteudo origem, NaturezaDoConteudo revisao)
    {
        var item = Novo(origem == NaturezaDoConteudo.Desconhecida ? NaturezaDoConteudo.DitoPeloUsuario : origem);
        if (revisao != NaturezaDoConteudo.Desconhecida)
            item.Corrigir(1, TipoDeConhecimento.Solucao, "nova revisão", null, null, Sensibilidade.Pessoal, null, null, [], Prova, Agora, revisao);
        // Simula somente a reidratação de legado: a API de novas escritas não aceita Desconhecida.
        typeof(Conhecimento).GetProperty(nameof(Conhecimento.NaturezaDeOrigem))!.SetValue(item, origem);
        typeof(Conhecimento).GetProperty(nameof(Conhecimento.Natureza))!.SetValue(item, revisao);
        var antes = item.Historico.ToArray();
        var permitido = origem is NaturezaDoConteudo.DitoPeloUsuario or NaturezaDoConteudo.FonteSelecionada &&
            revisao is NaturezaDoConteudo.DitoPeloUsuario or NaturezaDoConteudo.FonteSelecionada;
        if (permitido)
        {
            item.Confirmar(item.Revisao, Prova, Agora);
            Assert.Equal(StatusDoConhecimento.Confirmado, item.Status);
            Assert.Equal(revisao, item.Historico[^1].Natureza);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => item.Confirmar(item.Revisao, Prova, Agora));
            Assert.Equal(StatusDoConhecimento.Inferido, item.Status);
            Assert.Equal(antes, item.Historico);
        }
        Assert.Equal(origem, item.NaturezaDeOrigem);
        Assert.Equal(antes, item.Historico.Take(antes.Length));
    }

    [Theory]
    [InlineData(NaturezaDoConteudo.DitoPeloUsuario, StatusDoConhecimento.Confirmado)]
    [InlineData(NaturezaDoConteudo.FonteSelecionada, StatusDoConhecimento.Confirmado)]
    [InlineData(NaturezaDoConteudo.ConclusaoDoAgente, StatusDoConhecimento.Inferido)]
    public void PromocaoRepassaNaturezaEUsaAMesmaRegra(NaturezaDoConteudo natureza, StatusDoConhecimento status)
    {
        var candidato = new CandidatoDeConhecimento(Guid.NewGuid(), null, TipoDeConhecimento.Nota, "texto", Sensibilidade.Pessoal,
            natureza, ModoDeCaptura.Explicita, "pedido", Prova, Agora);
        var item = candidato.Promover(1, Prova, Agora.AddMinutes(1));
        Assert.Equal(natureza, item.NaturezaDeOrigem); Assert.Equal(natureza, item.Natureza);
        Assert.Equal(status, item.Status); Assert.All(item.Historico, h => Assert.Equal(natureza, h.Natureza));
        if (natureza == NaturezaDoConteudo.ConclusaoDoAgente)
            Assert.Throws<InvalidOperationException>(() => item.Confirmar(item.Revisao, Prova, Agora.AddMinutes(2)));
    }

    [Fact]
    public void CorrecaoHumanaNaoReescreveOrigemDoAgenteNemPermiteConfirmar()
    {
        var item = Novo(NaturezaDoConteudo.ConclusaoDoAgente); var antes = item.Historico.ToArray();
        item.Corrigir(1, TipoDeConhecimento.Fato, "afirmação do usuário", null, null, Sensibilidade.Pessoal, null, null, [], Prova, Agora, NaturezaDoConteudo.DitoPeloUsuario);
        Assert.Equal(NaturezaDoConteudo.ConclusaoDoAgente, item.NaturezaDeOrigem);
        Assert.Equal(NaturezaDoConteudo.DitoPeloUsuario, item.Natureza);
        Assert.Throws<InvalidOperationException>(() => item.Confirmar(2, Prova, Agora));
        Assert.Equal(antes, item.Historico.Take(1)); Assert.Equal(StatusDoConhecimento.Inferido, item.Status);
    }

    [Fact]
    public void CorrecaoDoAgenteDevolveConfirmadoOuTemporarioAInferidoSemAlterarOrigem()
    {
        var item = Novo(NaturezaDoConteudo.DitoPeloUsuario); item.Confirmar(1, Prova, Agora);
        item.Corrigir(2, item.Tipo, "nova hipótese", null, null, Sensibilidade.Pessoal, null, null, [], Prova, Agora, NaturezaDoConteudo.ConclusaoDoAgente);
        Assert.Equal(StatusDoConhecimento.Inferido, item.Status);
        Assert.Equal(NaturezaDoConteudo.DitoPeloUsuario, item.NaturezaDeOrigem);
        Assert.Equal(NaturezaDoConteudo.ConclusaoDoAgente, item.Historico[^1].Natureza);
        Assert.Throws<InvalidOperationException>(() => item.Confirmar(3, Prova, Agora));
        var temporario = new Conhecimento(Guid.NewGuid(), null, TipoDeConhecimento.Nota, "temporário", null, StatusDoConhecimento.Temporario,
            null, Sensibilidade.Pessoal, null, null, [], Prova, Agora, NaturezaDoConteudo.DitoPeloUsuario);
        temporario.Corrigir(1, temporario.Tipo, "hipótese", null, null, temporario.Sensibilidade, null, null, [], Prova, Agora, NaturezaDoConteudo.ConclusaoDoAgente);
        Assert.Equal(StatusDoConhecimento.Inferido, temporario.Status);
        Assert.Equal(NaturezaDoConteudo.DitoPeloUsuario, temporario.NaturezaDeOrigem);
    }

    [Fact]
    public void ConsolidacaoIncorporaProvenienciaSemPromoverAgenteOuAlterarNaturezasHistoricas()
    {
        var destino = Novo(NaturezaDoConteudo.FonteSelecionada); destino.Confirmar(1, Prova, Agora);
        var duplicata = Novo(NaturezaDoConteudo.ConclusaoDoAgente, destino.IdEspacoDeConhecimento);
        var historicoDestino = destino.Historico.ToArray(); var historicoDuplicata = duplicata.Historico.ToArray();
        destino.IncorporarFontesDe([duplicata], 2, Prova, Agora);
        duplicata.SubstituirPor(destino, 1, Prova, Agora);
        Assert.Equal(StatusDoConhecimento.Confirmado, destino.Status);
        Assert.Equal(NaturezaDoConteudo.FonteSelecionada, destino.NaturezaDeOrigem);
        Assert.Equal(NaturezaDoConteudo.FonteSelecionada, destino.Natureza);
        Assert.Equal(historicoDestino, destino.Historico.Take(historicoDestino.Length));
        Assert.Equal(historicoDuplicata, duplicata.Historico.Take(1));
        Assert.Equal(NaturezaDoConteudo.ConclusaoDoAgente, duplicata.NaturezaDeOrigem);
        Assert.Equal(StatusDoConhecimento.Substituido, duplicata.Status);
        Assert.All(duplicata.Historico, h => Assert.Equal(NaturezaDoConteudo.ConclusaoDoAgente, h.Natureza));
    }

    [Theory]
    [InlineData(NaturezaDoConteudo.Desconhecida)]
    [InlineData((NaturezaDoConteudo)99)]
    public void EscritasNovasRecusamNaturezaDesconhecidaOuInvalidaSemMutacaoParcial(NaturezaDoConteudo natureza)
    {
        Assert.Throws<ArgumentException>(() => Novo(natureza));
        var item = Novo(NaturezaDoConteudo.DitoPeloUsuario); var antes = item.Historico.ToArray();
        Assert.Throws<ArgumentException>(() => item.Corrigir(1, item.Tipo, "outro", null, null, item.Sensibilidade, null, null, [], Prova, Agora, natureza));
        Assert.Equal(antes, item.Historico);
        Assert.Throws<ArgumentException>(() => new CandidatoDeConhecimento(Guid.NewGuid(), null, TipoDeConhecimento.Fato, "texto", Sensibilidade.Pessoal,
            natureza, ModoDeCaptura.Explicita, "pedido", Prova, Agora));
    }

    [Fact]
    public void HistoricoAntigoSemNaturezaELeituraComoNaoRegistrada()
    {
        var revisao = Novo(NaturezaDoConteudo.DitoPeloUsuario).Historico[0];
        var json = JsonSerializer.SerializeToNode(revisao)!.AsObject(); json.Remove("Natureza");
        var antiga = JsonSerializer.Deserialize<RevisaoDoConhecimento>(json.ToJsonString())!;
        Assert.Null(antiga.Natureza); Assert.Equal(revisao.Conteudo, antiga.Conteudo);
        Assert.False(JsonSerializer.SerializeToNode(antiga)!.AsObject().ContainsKey("Natureza"));
    }
}
