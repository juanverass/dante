using Dante.Application.Agentes;
using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;

namespace Dante.Tests;

// #301 (AD-68): identidade, ciclo de vida, versões imutáveis com hash estável e a garantia de que execução nova não
// escreve sem worktree reservada.
public sealed class AgentesDominioTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("revisor")]
    [InlineData("dev-2")]
    [InlineData("ab")]
    [InlineData("a234567890123456789012345678901")]
    public void HandleValidoEhMinusculoSemArrobaECurto(string handle)
    {
        Assert.True(Agente.HandleValido(handle));
        Assert.Equal(handle, new Agente(handle, null, null, 1, Agora).Handle);
    }

    [Theory]
    [InlineData("@revisor")]
    [InlineData("Revisor")]
    [InlineData("r")]
    [InlineData("1revisor")]
    [InlineData("-revisor")]
    [InlineData("revisor_pr")]
    [InlineData("revisor pr")]
    [InlineData("a2345678901234567890123456789012")]
    [InlineData("")]
    public void HandleInvalidoEhRecusado(string handle)
    {
        Assert.False(Agente.HandleValido(handle));
        Assert.Throws<ArgumentException>(() => new Agente(handle, null, null, 1, Agora));
    }

    // Embutidos e palavras que os comandos já interpretam na posição do agente nunca são handles registrados.
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("plan")]
    [InlineData("auto")]
    [InlineData("manual")]
    [InlineData("approval")]
    [InlineData("full")]
    [InlineData("default")]
    public void HandleReservadoEhRecusado(string handle)
    {
        Assert.False(Agente.HandleValido(handle));
        Assert.Contains("reservado", Assert.Throws<ArgumentException>(() => new Agente(handle, null, null, 1, Agora)).Message);
    }

    [Fact]
    public void CicloDeVidaSoAceitaAsTransicoesDoDiagrama()
    {
        var agente = Novo();
        Assert.Throws<TransicaoInvalidaException>(() => agente.Reativar(Agora));
        Assert.Throws<TransicaoInvalidaException>(() => agente.Arquivar(Agora));
        agente.Desativar(Agora);
        Assert.Equal(EstadoDoAgente.Desativado, agente.Estado);
        Assert.Throws<TransicaoInvalidaException>(() => agente.Desativar(Agora));
        agente.Reativar(Agora);
        Assert.Equal(EstadoDoAgente.Ativo, agente.Estado);
        agente.Desativar(Agora);
        agente.Arquivar(Agora);
        Assert.Equal(EstadoDoAgente.Arquivado, agente.Estado);
        Assert.Throws<TransicaoInvalidaException>(() => agente.Reativar(Agora));
        Assert.Throws<TransicaoInvalidaException>(() => agente.Desativar(Agora));
        Assert.Throws<InvalidOperationException>(() => agente.NovaVersao(Configuracao(), 1, Agora));
        Assert.Throws<InvalidOperationException>(() => agente.Apresentar("x", null, Agora));
    }

    [Fact]
    public void CadaEdicaoCriaVersaoNovaSemTocarNaAnterior()
    {
        var agente = Novo();
        var v1 = agente.NovaVersao(Configuracao(instrucoes: "revise"), 7, Agora);
        var v2 = agente.NovaVersao(Configuracao(instrucoes: "revise com rigor"), 8, Agora.AddMinutes(1));
        Assert.Equal((1, 2), (v1.Numero, v2.Numero));
        Assert.Equal((v2.Id, 2), (agente.IdVersaoAtual, agente.NumeroDaVersaoAtual));
        Assert.Equal("revise", v1.Instrucoes);
        Assert.Equal((7L, 8L), (v1.IdDoAutor, v2.IdDoAutor));
        Assert.NotEqual(v1.HashDaConfiguracao, v2.HashDaConfiguracao);
        Assert.True(v1.Integra && v2.Integra);
        // Nenhum membro público de VersaoDoAgente tem setter acessível: imutável fora do provider.
        Assert.All(typeof(VersaoDoAgente).GetProperties(), propriedade =>
            Assert.True(propriedade.SetMethod is null || !propriedade.SetMethod.IsPublic, propriedade.Name));
        Assert.Empty(typeof(VersaoDoAgente).GetMethods().Where(metodo => metodo.DeclaringType == typeof(VersaoDoAgente) &&
            !metodo.IsSpecialName));
    }

    [Fact]
    public void HashEhEstavelIndependenteDaOrdemENormalizacao()
    {
        var a = new ConfiguracaoDoAgente(AgentKind.Codex, AgentPermissionProfile.Plan, AgentPermissionProfile.Manual, true,
            "fake-mini", "low", "  linha 1\r\nlinha 2  ",
            [CapacidadeDoAgente.Revisar, CapacidadeDoAgente.Pesquisar, CapacidadeDoAgente.Revisar],
            [FerramentaDoDante.Planilhas, FerramentaDoDante.Brain], ["@b", "@A"], new LimitesDoAgente(2, 1000));
        var b = new ConfiguracaoDoAgente(AgentKind.Codex, AgentPermissionProfile.Plan, AgentPermissionProfile.Manual, true,
            "fake-mini", "low", "linha 1\nlinha 2", [CapacidadeDoAgente.Pesquisar, CapacidadeDoAgente.Revisar],
            [FerramentaDoDante.Brain, FerramentaDoDante.Planilhas], ["@a", "@b"], new LimitesDoAgente(2, 1000));
        Assert.Equal(a.JsonCanonico(), b.JsonCanonico());
        Assert.Equal(a.Hash(), b.Hash());
        // Valor fixo: muda só se o formato canônico mudar (e aí VersaoDoFormato sobe).
        Assert.Equal(
            "{\"capacidades\":[\"Revisar\",\"Pesquisar\"],\"driver\":\"Codex\",\"esforco\":\"low\",\"ferramentas\":[\"Brain\",\"Planilhas\"]," +
            "\"formato\":\"1\",\"instrucoes\":\"linha 1\\nlinha 2\",\"limites\":{\"execucoesSimultaneas\":2,\"minutosPorEtapa\":null," +
            "\"tokensPorExecucao\":1000,\"turnosPorEtapa\":null},\"modelo\":\"fake-mini\",\"perfilMaximo\":\"Manual\"," +
            "\"perfilPadrao\":\"Plan\",\"podeEscrever\":true,\"repositorios\":[\"@a\",\"@b\"]}", a.JsonCanonico());
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(a.JsonCanonico()))), a.Hash());
        Assert.Matches("^[0-9a-f]{64}$", a.Hash());
        Assert.NotEqual(a.Hash(), Configuracao().Hash());
        Assert.NotEqual(Configuracao(modelo: "opus").Hash(), Configuracao(modelo: "sonnet").Hash());
        // "*" absorve os aliases: qualquer repositório é uma concessão só.
        Assert.Equal(["*"], Configuracao(repositorios: ["@a", "*"]).Repositorios);
    }

    // Review Codex: as listas de uma versão não voltam a ser arrays mutáveis nas mãos de quem as lê.
    [Fact]
    public void ListasDaConfiguracaoEDaVersaoNaoSaoMutaveis()
    {
        foreach (var configuracao in new[] { Configuracao(repositorios: ["@dante"]), Configuracao(repositorios: ["*"]) })
        {
            Assert.IsNotType<string[]>(configuracao.Repositorios);
            Assert.IsNotType<CapacidadeDoAgente[]>(configuracao.Capacidades);
            Assert.IsNotType<FerramentaDoDante[]>(configuracao.Ferramentas);
            Assert.Throws<NotSupportedException>(() => ((IList<string>)configuracao.Repositorios)[0] = "*");
        }
        var versao = Novo().NovaVersao(Configuracao(repositorios: ["@dante"]), 1, Agora);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)versao.Repositorios)[0] = "*");
        Assert.Throws<NotSupportedException>(() => ((IList<CapacidadeDoAgente>)versao.Capacidades)[0] = CapacidadeDoAgente.Implementar);
        Assert.True(versao.Integra);
    }

    [Fact]
    public void InvariantesDaConfiguracaoSaoValidadas()
    {
        Assert.Throws<ArgumentException>(() => new ConfiguracaoDoAgente(AgentKind.Claude, AgentPermissionProfile.Auto,
            AgentPermissionProfile.Manual, true));
        Assert.Throws<ArgumentException>(() => new ConfiguracaoDoAgente(AgentKind.Claude, AgentPermissionProfile.Plan,
            AgentPermissionProfile.Manual, podeEscrever: false));
        Assert.Throws<ArgumentException>(() => new ConfiguracaoDoAgente(AgentKind.Claude, AgentPermissionProfile.Plan,
            AgentPermissionProfile.Plan, false, capacidades: [CapacidadeDoAgente.Implementar]));
        Assert.Throws<ArgumentException>(() => Configuracao(repositorios: ["dante"]));
        Assert.Throws<ArgumentException>(() => Configuracao(modelo: "-x"));
        Assert.Throws<ArgumentException>(() => Configuracao(modelo: "a b"));
        Assert.Throws<ArgumentException>(() => Configuracao(instrucoes: new string('x', 4001)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Configuracao(limites: new LimitesDoAgente(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Configuracao(limites: new LimitesDoAgente(11)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Configuracao(limites: new LimitesDoAgente(TokensPorExecucao: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfiguracaoDoAgente(AgentKind.Claude,
            AgentPermissionProfile.Plan, AgentPermissionProfile.Plan, false, capacidades: [(CapacidadeDoAgente)99]));
    }

    // Sessão restringe, nunca amplia: plan < manual < auto.
    [Theory]
    [InlineData(AgentPermissionProfile.Plan, AgentPermissionProfile.Plan, true)]
    [InlineData(AgentPermissionProfile.Plan, AgentPermissionProfile.Manual, false)]
    [InlineData(AgentPermissionProfile.Plan, AgentPermissionProfile.Auto, false)]
    [InlineData(AgentPermissionProfile.Manual, AgentPermissionProfile.Plan, true)]
    [InlineData(AgentPermissionProfile.Manual, AgentPermissionProfile.Manual, true)]
    [InlineData(AgentPermissionProfile.Manual, AgentPermissionProfile.Auto, false)]
    [InlineData(AgentPermissionProfile.Auto, AgentPermissionProfile.Auto, true)]
    [InlineData(AgentPermissionProfile.Auto, AgentPermissionProfile.Plan, true)]
    public void PerfilDaSessaoNuncaPassaDoMaximo(AgentPermissionProfile maximo, AgentPermissionProfile pedido, bool permitido)
    {
        var configuracao = new ConfiguracaoDoAgente(AgentKind.Claude, AgentPermissionProfile.Plan, maximo,
            maximo != AgentPermissionProfile.Plan);
        Assert.Equal(permitido, configuracao.PermitePerfil(pedido));
        Assert.False(configuracao.PermitePerfil((AgentPermissionProfile)42));
    }

    [Fact]
    public void ConcessoesDeRepositorioEFerramentaSaoFechadas()
    {
        var geral = Configuracao();
        Assert.False(geral.PermiteRepositorio("@dante"));
        Assert.False(geral.PermiteFerramenta(FerramentaDoDante.Brain));
        var dante = Configuracao(repositorios: ["@dante"]);
        Assert.True(dante.PermiteRepositorio("@DANTE"));
        Assert.False(dante.PermiteRepositorio("@outro"));
        Assert.True(Configuracao(repositorios: ["*"]).PermiteRepositorio("@qualquer"));
        // Vinculados (AD-58) também precisam de concessão: um principal permitido não leva o vinculado junto.
        var contexto = JobExecutionContext.Repository("@dante", "/r/dante", [new RepositorioCadastrado("@front", "/r/front")]);
        Assert.False(MontagemDaTarefa.ReposPermitidos(dante, contexto));
        Assert.Contains("@front", MontagemDaTarefa.RecusaDeRepositorio("revisor", dante, contexto));
        Assert.True(MontagemDaTarefa.ReposPermitidos(Configuracao(repositorios: ["@dante", "@front"]), contexto));
        Assert.True(MontagemDaTarefa.ReposPermitidos(geral, JobExecutionContext.General("/g")));
    }

    [Fact]
    public void EmbutidosSaoMaterializadosDasPreferenciasSemRestringirOQueJaFazem()
    {
        Assert.True(AgentesEmbutidos.TryObter("Claude", out var claude));
        Assert.Equal(AgentKind.Claude, claude);
        Assert.False(AgentesEmbutidos.TryObter("revisor", out _));
        var materializada = AgentesEmbutidos.Materializar(AgentKind.Codex, new AgentModelSelection("fake-mini", "xhigh"),
            AgentPermissionProfile.Auto);
        Assert.Equal((AgentKind.Codex, "fake-mini", "xhigh", AgentPermissionProfile.Auto), (materializada.Driver,
            materializada.Modelo, materializada.Esforco, materializada.PerfilPadrao));
        Assert.True(materializada.PermitePerfil(AgentPermissionProfile.Auto) && materializada.PermiteRepositorio("@x"));
        Assert.All(Enum.GetValues<FerramentaDoDante>(), ferramenta => Assert.True(materializada.PermiteFerramenta(ferramenta)));
        // Preferências diferentes, hash diferente: a sessão registra qual configuração do embutido usou.
        Assert.NotEqual(materializada.Hash(), AgentesEmbutidos.Materializar(AgentKind.Codex, null, AgentPermissionProfile.Auto).Hash());
        Assert.Equal(materializada.Hash(), AgentesEmbutidos.Materializar(AgentKind.Codex,
            new AgentModelSelection("fake-mini", "xhigh"), AgentPermissionProfile.Auto).Hash());
        Assert.True(AgentesEmbutidos.Descrever(AgentKind.Claude).Embutido);
    }

    // #307: etapa de escrita de execução nova existe (roda só numa worktree, conferida por quem executa), mas nunca é
    // retomada automaticamente: escrita não é idempotente.
    [Fact]
    public void TarefaExigeVersaoEEscritaSoComRetomadaPorConfirmacao()
    {
        Assert.Throws<ArgumentException>(() => new Execucao(1, TipoDeExecucao.Tarefa, "telegram", 1, "{}", "x", Agora));
        var tarefa = new Execucao(1, TipoDeExecucao.Tarefa, "telegram", 1, "{}", "x", Agora, Guid.NewGuid());
        Assert.Equal("E1", tarefa.Codigo);
        Assert.True(tarefa.Nova);
        Assert.Throws<InvalidOperationException>(() => new EtapaDaExecucao(tarefa, 1, TipoDeEtapa.TurnoDeAgente, true,
            PoliticaDeRetomada.Automatica, Agora));
        Assert.True(new EtapaDaExecucao(tarefa, 1, TipoDeEtapa.TurnoDeAgente, true, PoliticaDeRetomada.ComConfirmacao, Agora).Escrita);
        Assert.False(new EtapaDaExecucao(tarefa, 1, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica, Agora).Escrita);
        // O job legado continua escrevendo no checkout principal, como hoje.
        var avulsa = new Execucao(2, TipoDeExecucao.Avulsa, "telegram", 1, "{}", null, Agora);
        Assert.True(new EtapaDaExecucao(avulsa, 1, TipoDeEtapa.ProcessoAvulso, true, PoliticaDeRetomada.ComConfirmacao, Agora).Escrita);
    }

    [Fact]
    public void GuardaDoExecutorRecusaEscritaEPerfilAlemDePlanEmExecucaoNova()
    {
        var tarefa = new Execucao(1, TipoDeExecucao.Tarefa, "telegram", 1, "{}", "x", Agora, Guid.NewGuid());
        var leitura = new EtapaDaExecucao(tarefa, 1, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica, Agora);
        GuardaDeEscrita.Exigir(tarefa, leitura, AgentPermissionProfile.Plan);
        Assert.Throws<InvalidOperationException>(() => GuardaDeEscrita.Exigir(tarefa, leitura, AgentPermissionProfile.Manual));
        Assert.Throws<InvalidOperationException>(() => GuardaDeEscrita.Exigir(tarefa, leitura, AgentPermissionProfile.Auto));
        // Etapa de escrita (#307): nunca em plan e nunca sem worktree pronta da própria execução.
        var escrita = new EtapaDaExecucao(tarefa, 2, TipoDeEtapa.TurnoDeAgente, true, PoliticaDeRetomada.ComConfirmacao, Agora);
        Assert.Throws<InvalidOperationException>(() => GuardaDeEscrita.Exigir(tarefa, escrita, AgentPermissionProfile.Plan));
        Assert.Throws<EscritaSemWorktreeException>(() => GuardaDeEscrita.Exigir(tarefa, escrita, AgentPermissionProfile.Auto));
        Assert.Throws<EscritaSemWorktreeException>(() => GuardaDeEscrita.Exigir(tarefa, escrita, AgentPermissionProfile.Auto, []));
        var outra = new Execucao(3, TipoDeExecucao.Tarefa, "telegram", 1, "{}", "x", Agora, Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => GuardaDeEscrita.Exigir(outra, leitura, AgentPermissionProfile.Plan));
        // Avulsa (legado) não muda.
        var avulsa = new Execucao(2, TipoDeExecucao.Avulsa, "telegram", 1, "{}", null, Agora);
        GuardaDeEscrita.Exigir(avulsa, new EtapaDaExecucao(avulsa, 1, TipoDeEtapa.ProcessoAvulso, true,
            PoliticaDeRetomada.ComConfirmacao, Agora), AgentPermissionProfile.Auto);
    }

    // Caso explícito da Issue: versão com escrita e perfil padrão auto ainda gera tarefa em plan, sem etapa de escrita.
    [Fact]
    public void TarefaDeAgenteComEscritaSaiEmPlanESemEtapaDeEscrita()
    {
        var versao = Versao(new ConfiguracaoDoAgente(AgentKind.Claude, AgentPermissionProfile.Auto,
            AgentPermissionProfile.Auto, true, "opus", "high", "implemente", [CapacidadeDoAgente.Implementar],
            repositorios: ["@dante"]));
        var contexto = JobExecutionContext.Repository("@dante", "/r/dante");
        var plano = MontagemDaTarefa.Montar("dev", versao, contexto, "  ajuste o README  ");
        Assert.Equal(AgentPermissionProfile.Plan, plano.Perfil);
        Assert.True(plano.SomenteLeitura);
        var etapa = Assert.Single(plano.Etapas);
        Assert.Equal((TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica), (etapa.Tipo, etapa.Escrita, etapa.PoliticaDeRetomada));
        Assert.Equal("ajuste o README", plano.Pedido);

        var (execucao, etapas) = MontagemDaTarefa.CriarExecucao(plano, 42, "telegram", 123, Agora);
        Assert.Equal((TipoDeExecucao.Tarefa, versao.Id, "E42", "ajuste o README"), (execucao.Tipo, execucao.IdVersaoDoAgente,
            execucao.Codigo, execucao.Objetivo));
        var criada = Assert.Single(etapas);
        Assert.Equal((false, AgentKind.Claude, "opus", "high"), (criada.Escrita, criada.Agente, criada.Modelo, criada.Esforco));
        GuardaDeEscrita.Exigir(execucao, criada, plano.Perfil);
        Assert.Contains("\"alias\":\"@dante\"", execucao.Contexto);

        // Um plano adulterado para escrever não vira execução.
        Assert.Throws<EscritaSemWorktreeException>(() => MontagemDaTarefa.CriarExecucao(
            plano with { Perfil = AgentPermissionProfile.Auto }, 43, "telegram", 123, Agora));
        Assert.Throws<EscritaSemWorktreeException>(() => MontagemDaTarefa.CriarExecucao(
            plano with { Etapas = [new EtapaPlanejada(1, TipoDeEtapa.TurnoDeAgente, true, PoliticaDeRetomada.ComConfirmacao)] },
            44, "telegram", 123, Agora));
    }

    [Fact]
    public void TarefaRecusaEmbutidoRepositorioNaoConcedidoEPedidoVazioERedigeSegredo()
    {
        var versao = Versao(Configuracao(repositorios: ["@dante"]));
        Assert.Throws<ArgumentException>(() => MontagemDaTarefa.Montar("claude", versao, JobExecutionContext.General("/g"), "x"));
        Assert.Contains("não tem concessão", Assert.Throws<ArgumentException>(() => MontagemDaTarefa.Montar("revisor", versao,
            JobExecutionContext.Repository("@outro", "/r/outro"), "x")).Message);
        Assert.Throws<ArgumentException>(() => MontagemDaTarefa.Montar("revisor", versao, JobExecutionContext.General("/g"), " "));
        var plano = MontagemDaTarefa.Montar("revisor", versao, JobExecutionContext.General("/g"),
            "use o token=abcdefghijk para testar");
        Assert.Equal("[conteúdo protegido]", plano.ObjetivoRegistrado);
        Assert.Contains("token=abcdefghijk", plano.Pedido);
        Assert.Equal(Execucao.TamanhoMaximoDoObjetivo, MontagemDaTarefa.Montar("revisor", versao,
            JobExecutionContext.General("/g"), new string('a', 5000)).ObjetivoRegistrado.Length);
    }

    internal static VersaoDoAgenteDto Versao(ConfiguracaoDoAgente configuracao)
    {
        var versao = Novo().NovaVersao(configuracao, 1, Agora);
        return new VersaoDoAgenteDto
        {
            Id = versao.Id, IdAgente = versao.IdAgente, Numero = versao.Numero, Configuracao = versao.Configuracao,
            HashDaConfiguracao = versao.HashDaConfiguracao, CriadaEm = versao.CriadaEm
        };
    }

    private static Agente Novo() => new("revisor", "Revisor", null, 1, Agora);

    internal static ConfiguracaoDoAgente Configuracao(string? modelo = null, string? instrucoes = null,
        IEnumerable<string>? repositorios = null, LimitesDoAgente? limites = null) =>
        new(AgentKind.Claude, AgentPermissionProfile.Plan, AgentPermissionProfile.Plan, false, modelo, null, instrucoes,
            [CapacidadeDoAgente.Revisar], null, repositorios, limites);
}
