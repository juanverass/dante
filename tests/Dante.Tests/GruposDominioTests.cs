using System.Reflection;
using Dante.Domain.Execucoes;
using Dante.Domain.Grupos;

namespace Dante.Tests;

// #308 (AD-69): invariantes do grupo, da versão imutável, do roteiro determinístico, da política e da ativação humana
// vinculada ao hash, sem banco.
public sealed class GruposDominioTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Dev = Guid.NewGuid();
    private static readonly Guid Rev = Guid.NewGuid();

    private static readonly PassoDoRoteiro[] ComCorrecao =
    [
        new(TipoDePassoDoRoteiro.Implementar, "dev"), new(TipoDePassoDoRoteiro.Revisar, "rev"),
        new(TipoDePassoDoRoteiro.Corrigir, "dev"), new(TipoDePassoDoRoteiro.AprovacaoHumana)
    ];

    private static ConfiguracaoDoGrupo Configuracao(int rodadas = 2, IEnumerable<PassoDoRoteiro>? roteiro = null,
        IEnumerable<ParticipanteDoGrupo>? participantes = null, IReadOnlyList<string>? repositorios = null,
        int paralelismo = 1, int? duracao = null) =>
        new(ModoDeCoordenacao.Roteiro,
            participantes ?? [new(Dev, "dev", "Implementa.", true), new(Rev, "rev", "Revisa o diff.", false)],
            roteiro ?? ComCorrecao, new PoliticaDoGrupo(rodadas, paralelismo, repositorios, duracao));

    [Fact]
    public void HashCanonicoIndependeDaOrdemDeEntradaEMudaComQualquerCampo()
    {
        var a = Configuracao(participantes: [new(Rev, "REV ", "Revisa o diff.", false), new(Dev, "dev", "Implementa.", true)],
            repositorios: ["@b", "@A", "@b"]);
        var b = Configuracao(repositorios: ["@a", "@b"]);
        Assert.Equal(a.Hash(), b.Hash());
        Assert.Equal(["dev", "rev"], a.Participantes.Select(p => p.Papel));
        Assert.Equal(["@a", "@b"], a.Politica.RepositoriosPermitidos);
        Assert.Matches("^[0-9a-f]{64}$", a.Hash());
        var variantes = new[]
        {
            Configuracao(rodadas: 3),
            Configuracao(duracao: 30),
            Configuracao(repositorios: ["@a"]),
            Configuracao(participantes: [new(Dev, "dev", "Outra coisa.", true), new(Rev, "rev", "Revisa o diff.", false)]),
            Configuracao(participantes: [new(Guid.NewGuid(), "dev", "Implementa.", true), new(Rev, "rev", "Revisa o diff.", false)]),
            Configuracao(rodadas: 0, roteiro: [ComCorrecao[0], ComCorrecao[1], ComCorrecao[3]])
        };
        Assert.All(variantes, variante => Assert.NotEqual(b.Hash(), variante.Hash()));
        Assert.Equal(variantes.Length, variantes.Select(variante => variante.Hash()).Distinct().Count());
    }

    [Theory]
    [InlineData("implementar:dev,aprovar")]
    [InlineData("revisar:rev,implementar:dev,aprovar")]
    [InlineData("implementar:dev,revisar:rev")]
    [InlineData("implementar:dev,revisar:rev,corrigir:dev,corrigir:dev,aprovar")]
    [InlineData("implementar:dev,revisar:rev,aprovar,corrigir:dev")]
    [InlineData("implementar:dev,implementar:dev,revisar:rev,aprovar")]
    public void RoteiroForaDaFormaSuportadaERecusado(string roteiro)
    {
        var passos = roteiro.Split(',').Select(texto =>
        {
            var partes = texto.Split(':');
            var tipo = partes[0] switch
            {
                "implementar" => TipoDePassoDoRoteiro.Implementar,
                "revisar" => TipoDePassoDoRoteiro.Revisar,
                "corrigir" => TipoDePassoDoRoteiro.Corrigir,
                _ => TipoDePassoDoRoteiro.AprovacaoHumana
            };
            return new PassoDoRoteiro(tipo, partes.Length > 1 ? partes[1] : null);
        }).ToArray();
        Assert.Contains("Roteiro não suportado",
            Assert.Throws<ArgumentException>(() => Configuracao(roteiro: passos)).Message);
    }

    [Fact]
    public void PapeisEEscritaSaoValidadosContraORoteiro()
    {
        // Papel repetido, inválido, inexistente no roteiro ou sem passo.
        Assert.Contains("mais de uma vez", Assert.Throws<ArgumentException>(() => Configuracao(participantes:
            [new(Dev, "dev", null, true), new(Rev, "DEV", null, false)])).Message);
        Assert.Contains("Papel inválido", Assert.Throws<ArgumentException>(() => Configuracao(participantes:
            [new(Dev, "@dev", null, true), new(Rev, "rev", null, false)])).Message);
        Assert.Contains("não está no grupo", Assert.Throws<ArgumentException>(() => Configuracao(participantes:
            [new(Dev, "dev", null, true), new(Rev, "revisor", null, false)])).Message);
        Assert.Contains("não executa nenhum passo", Assert.Throws<ArgumentException>(() => Configuracao(participantes:
            [new(Dev, "dev", null, true), new(Rev, "rev", null, false), new(Rev, "extra", null, false)])).Message);
        // Quem implementa ou corrige precisa de escrita; quem revisa não pode tê-la.
        Assert.Contains("precisa de escrita", Assert.Throws<ArgumentException>(() => Configuracao(participantes:
            [new(Dev, "dev", null, false), new(Rev, "rev", null, false)])).Message);
        Assert.Contains("não pode ter escrita", Assert.Throws<ArgumentException>(() => Configuracao(participantes:
            [new(Dev, "dev", null, true), new(Rev, "rev", null, true)])).Message);
        Assert.Contains("precisa de escrita", Assert.Throws<ArgumentException>(() => Configuracao(
            participantes: [new(Dev, "dev", null, true), new(Rev, "rev", null, false), new(Dev, "fix", null, false)],
            roteiro: [ComCorrecao[0], ComCorrecao[1], new(TipoDePassoDoRoteiro.Corrigir, "fix"), ComCorrecao[3]])).Message);
        // A aprovação humana não tem papel; os demais passos têm.
        Assert.Contains("não é executada por um papel", Assert.Throws<ArgumentException>(() => Configuracao(
            roteiro: [ComCorrecao[0], ComCorrecao[1], ComCorrecao[2], new(TipoDePassoDoRoteiro.AprovacaoHumana, "dev")])).Message);
        Assert.Contains("precisa de um papel", Assert.Throws<ArgumentException>(() => Configuracao(
            roteiro: [new(TipoDePassoDoRoteiro.Implementar), ComCorrecao[1], ComCorrecao[2], ComCorrecao[3]])).Message);
        Assert.Contains("ao menos um participante", Assert.Throws<ArgumentException>(() => Configuracao(participantes: [])).Message);
        Assert.Contains("Responsabilidades passam", Assert.Throws<ArgumentException>(() => Configuracao(participantes:
            [new(Dev, "dev", new string('x', 1001), true), new(Rev, "rev", null, false)])).Message);
    }

    [Fact]
    public void PoliticaLimitaRodadasEExigeExecucaoSequencial()
    {
        Assert.Contains("de 1 a 5", Assert.Throws<ArgumentException>(() => Configuracao(rodadas: 0)).Message);
        Assert.Contains("de 1 a 5", Assert.Throws<ArgumentException>(() => Configuracao(rodadas: 6)).Message);
        Assert.Contains("use 0", Assert.Throws<ArgumentException>(() =>
            Configuracao(rodadas: 1, roteiro: [ComCorrecao[0], ComCorrecao[1], ComCorrecao[3]])).Message);
        Assert.Contains("sequencial", Assert.Throws<ArgumentException>(() => Configuracao(paralelismo: 2)).Message);
        Assert.Contains("Duração máxima", Assert.Throws<ArgumentException>(() => Configuracao(duracao: 0)).Message);
        Assert.Contains("@alias", Assert.Throws<ArgumentException>(() => Configuracao(repositorios: ["dante"])).Message);
        Assert.False(Configuracao(rodadas: 0, roteiro: [ComCorrecao[0], ComCorrecao[1], ComCorrecao[3]]).TemCorrecao);
        Assert.Equal(5, Configuracao(rodadas: 5).Politica.RodadasDeRevisaoMaximas);
    }

    [Fact]
    public void VersaoNasceDoGrupoNumeradaEImutavelComHashIntegro()
    {
        var grupo = new GrupoDeAgentes("engenharia", "Time de código", 7, Agora);
        var v1 = grupo.NovaVersao(Configuracao(), 7, Agora);
        var v2 = grupo.NovaVersao(Configuracao(rodadas: 3), 8, Agora.AddMinutes(1));
        Assert.Equal((1, 2), (v1.Numero, v2.Numero));
        Assert.Equal((v2.Id, 2), (grupo.IdVersaoAtual, grupo.NumeroDaVersaoAtual));
        Assert.Null(grupo.IdVersaoAtiva);
        Assert.True(v1.Integra);
        Assert.Equal(v1.HashDaConfiguracao, v1.Configuracao!.Hash());
        Assert.All(v1.Participantes, participante => Assert.Equal(v1.Id, participante.IdVersaoDoGrupo));
        // Nenhuma propriedade pública de versão, participante ou decisão tem setter acessível de fora.
        foreach (var tipo in new[] { typeof(VersaoDoGrupo), typeof(ParticipanteDaVersaoDoGrupo), typeof(AtivacaoDeVersaoDoGrupo) })
            Assert.All(tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                propriedade => Assert.True(propriedade.SetMethod is null || !propriedade.SetMethod.IsPublic, propriedade.Name));
        // Listas congeladas não voltam a ser arrays alteráveis.
        Assert.False(v1.Roteiro is PassoDoRoteiro[]);
        Assert.False(v1.RepositoriosPermitidos is string[]);
    }

    [Fact]
    public void AtivacaoExigeOHashDaVersaoEUmGrupoAtivo()
    {
        var grupo = new GrupoDeAgentes("engenharia", null, 7, Agora);
        var v1 = grupo.NovaVersao(Configuracao(), 7, Agora);
        Assert.Contains("hash confirmado", Assert.Throws<InvalidOperationException>(() =>
            grupo.Ativar(v1, v1.HashDaConfiguracao[..11], 9, Agora)).Message);
        Assert.Contains("hash confirmado", Assert.Throws<InvalidOperationException>(() =>
            grupo.Ativar(v1, new string('0', 64), 9, Agora)).Message);
        var ativacao = grupo.Ativar(v1, v1.HashDaConfiguracao[..12].ToUpperInvariant(), 9, Agora);
        Assert.Equal((v1.Id, 1, v1.HashDaConfiguracao, 9L), (ativacao.IdVersaoDoGrupo, ativacao.NumeroDaVersao,
            ativacao.HashAprovado, ativacao.IdDoAutor));
        Assert.Equal((v1.Id, (int?)1), (grupo.IdVersaoAtiva, grupo.NumeroDaVersaoAtiva));
        Assert.Contains("já está ativa", Assert.Throws<InvalidOperationException>(() =>
            grupo.Ativar(v1, v1.HashDaConfiguracao, 9, Agora)).Message);
        // Nova versão não troca a ativa sozinha.
        var v2 = grupo.NovaVersao(Configuracao(rodadas: 1), 7, Agora);
        Assert.Equal(v1.Id, grupo.IdVersaoAtiva);
        // Versão de outro grupo e grupo desativado são recusados.
        var outro = new GrupoDeAgentes("outro", null, 7, Agora);
        Assert.Throws<InvalidOperationException>(() => outro.Ativar(v2, v2.HashDaConfiguracao, 9, Agora));
        grupo.Desativar(Agora);
        Assert.Throws<InvalidOperationException>(() => grupo.Ativar(v2, v2.HashDaConfiguracao, 9, Agora));
    }

    [Fact]
    public void VersaoAdulteradaNaoEIntegraENaoEAtivada()
    {
        var grupo = new GrupoDeAgentes("engenharia", null, 7, Agora);
        var v1 = grupo.NovaVersao(Configuracao(), 7, Agora);
        typeof(VersaoDoGrupo).GetProperty(nameof(VersaoDoGrupo.RodadasDeRevisaoMaximas))!.SetValue(v1, 4);
        Assert.False(v1.Integra);
        Assert.Contains("não confere", Assert.Throws<InvalidOperationException>(() =>
            grupo.Ativar(v1, v1.HashDaConfiguracao, 9, Agora)).Message);
        // Conteúdo gravado que não passa mais na validação também não é íntegro.
        typeof(VersaoDoGrupo).GetProperty(nameof(VersaoDoGrupo.ParalelismoMaximo))!.SetValue(v1, 3);
        Assert.Null(v1.Configuracao);
        Assert.False(v1.Integra);
    }

    [Fact]
    public void CicloDeVidaENomeReservadoSeguemOAgente()
    {
        Assert.Contains("reservado", Assert.Throws<ArgumentException>(() => new GrupoDeAgentes("criar", null, 1, Agora)).Message);
        Assert.Throws<ArgumentException>(() => new GrupoDeAgentes("Engenharia", null, 1, Agora));
        Assert.Throws<ArgumentException>(() => new GrupoDeAgentes("@eng", null, 1, Agora));
        var grupo = new GrupoDeAgentes("eng", null, 1, Agora);
        Assert.Throws<TransicaoInvalidaException>(() => grupo.Arquivar(Agora));
        grupo.Desativar(Agora);
        grupo.Reativar(Agora);
        grupo.Desativar(Agora);
        grupo.Arquivar(Agora);
        Assert.Throws<TransicaoInvalidaException>(() => grupo.Reativar(Agora));
        Assert.Throws<InvalidOperationException>(() => grupo.NovaVersao(Configuracao(), 1, Agora));
        Assert.Throws<InvalidOperationException>(() => grupo.Descrever("x", Agora));
    }
}
