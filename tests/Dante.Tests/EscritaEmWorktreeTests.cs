using Dante.Application.Agentes;
using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Agentes;
using Dante.Worker;
using Dante.Worker.Execucoes;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dante.Tests;

// #307 (AD-71) sem banco: reserva no domínio, guarda de escrita com worktrees, pedido explícito de escrita, política por
// provider e a sessão que só recebe worktrees. Claude não escreve: recusado na montagem, no SessionRegistry e no driver.
public sealed class EscritaEmWorktreeTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void ReservaDeWorktreeTemCicloDeVidaFechadoENuncaEOCheckoutPrincipal()
    {
        var (execucao, etapa) = Tarefa();
        var reserva = new ReservaDeWorktree(execucao, etapa, "@dante", true, "/repos/dante", "/w/dante/E1", "dante/dev/e1",
            Sha.ToUpperInvariant(), "host", Agora);
        Assert.Equal((EstadoDaReservaDeWorktree.Reservada, true, Sha), (reserva.Estado, reserva.Ativa, reserva.ShaBase));
        reserva.MarcarPronta(Agora);
        Assert.True(reserva.Ativa);
        Assert.Throws<TransicaoInvalidaException>(() => reserva.MarcarRemovida(Agora));
        reserva.Liberar(Agora);
        Assert.False(reserva.Ativa);
        reserva.Preservar("Preservada: há alterações não commitadas.", Agora);
        Assert.True(reserva.Final);
        Assert.Throws<TransicaoInvalidaException>(() => reserva.Liberar(Agora));

        Assert.Throws<ArgumentException>(() => new ReservaDeWorktree(execucao, etapa, "@dante", true, "/repos/dante",
            "/repos/dante", "dante/dev/e1", Sha, "host", Agora));
        Assert.Throws<ArgumentException>(() => new ReservaDeWorktree(execucao, etapa, "@dante", true, "/repos/dante",
            "relativo/E1", "dante/dev/e1", Sha, "host", Agora));
        Assert.Throws<ArgumentException>(() => new ReservaDeWorktree(execucao, etapa, "@dante", true, "/repos/dante",
            "/w/dante/E1", "dante/dev/e1", "HEAD", "host", Agora));
        // Só etapa de escrita de execução nova.
        var leitura = new EtapaDaExecucao(execucao, 2, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica, Agora);
        Assert.Throws<InvalidOperationException>(() => new ReservaDeWorktree(execucao, leitura, "@dante", true, "/repos/dante",
            "/w/dante/E1", "dante/dev/e1", Sha, "host", Agora));
        var avulsa = new Execucao(9, TipoDeExecucao.Avulsa, "telegram", 1, "{}", null, Agora);
        var etapaAvulsa = new EtapaDaExecucao(avulsa, 1, TipoDeEtapa.ProcessoAvulso, true, PoliticaDeRetomada.ComConfirmacao, Agora);
        Assert.Throws<InvalidOperationException>(() => new ReservaDeWorktree(avulsa, etapaAvulsa, "@dante", true,
            "/repos/dante", "/w/dante/E9", "dante/dev/e9", Sha, "host", Agora));

        var falha = new ReservaDeWorktree(execucao, etapa, "@b", false, "/repos/b", "/w/b/E1", "dante/dev/e1", Sha, "host", Agora);
        falha.Falhar("diretório ocupado", Agora);
        Assert.Equal((EstadoDaReservaDeWorktree.Falhou, false, true), (falha.Estado, falha.Ativa, falha.Final));
    }

    [Fact]
    public void GuardaDeEscritaExigeTodasAsWorktreesProntasDaPropriaExecucao()
    {
        var (execucao, etapa) = Tarefa();
        var principal = Worktree(execucao.Id, "@dante", true, "/w/dante/E1", "/repos/dante");
        var vinculado = Worktree(execucao.Id, "@back", false, "/w/back/E1", "/repos/back");
        GuardaDeEscrita.Exigir(execucao, etapa, AgentPermissionProfile.Auto, [principal, vinculado]);

        Assert.Throws<EscritaSemWorktreeException>(() => GuardaDeEscrita.Exigir(execucao, etapa, AgentPermissionProfile.Auto,
            [principal with { IdExecucao = Guid.NewGuid() }]));
        Assert.Throws<EscritaSemWorktreeException>(() => GuardaDeEscrita.Exigir(execucao, etapa, AgentPermissionProfile.Auto,
            [principal with { Estado = EstadoDaReservaDeWorktree.Reservada }]));
        Assert.Throws<EscritaSemWorktreeException>(() => GuardaDeEscrita.Exigir(execucao, etapa, AgentPermissionProfile.Auto,
            [vinculado]));
        Assert.Throws<EscritaSemWorktreeException>(() => GuardaDeEscrita.Exigir(execucao, etapa, AgentPermissionProfile.Auto,
            [principal, vinculado with { Principal = true }]));
        // Uma worktree apontando para o checkout principal (o próprio ou o de outro repositório da execução).
        Assert.Throws<EscritaSemWorktreeException>(() => GuardaDeEscrita.Exigir(execucao, etapa, AgentPermissionProfile.Auto,
            [principal with { Caminho = "/repos/dante" }]));
        Assert.Throws<EscritaSemWorktreeException>(() => GuardaDeEscrita.Exigir(execucao, etapa, AgentPermissionProfile.Auto,
            [principal, vinculado with { Caminho = "/repos/dante" }]));
        Assert.Throws<InvalidOperationException>(() => GuardaDeEscrita.Exigir(execucao, etapa, AgentPermissionProfile.Plan,
            [principal]));
    }

    [Fact]
    public void PedidoDeEscritaExigeImplementarPerfilPadraoAcimaDePlanRepositorioECodex()
    {
        var codex = AgentesDominioTests.Versao(new ConfiguracaoDoAgente(AgentKind.Codex, AgentPermissionProfile.Auto,
            AgentPermissionProfile.Auto, true, "gpt", "high", "implemente", [CapacidadeDoAgente.Implementar], repositorios: ["*"]));
        var contexto = JobExecutionContext.Repository("@dante", "/repos/dante",
            [new RepositorioCadastrado("@back", "/repos/back")]);
        var plano = MontagemDaTarefa.MontarEscrita("dev", codex, contexto, "  corrija o bug  ");
        Assert.True(plano.Escrita && plano.EscritaValida && !plano.SomenteLeitura);
        Assert.Equal((AgentPermissionProfile.Auto, "corrija o bug"), (plano.Perfil, plano.Pedido));
        var etapa = Assert.Single(plano.Etapas);
        Assert.Equal((true, PoliticaDeRetomada.ComConfirmacao), (etapa.Escrita, etapa.PoliticaDeRetomada));
        var (execucao, etapas) = MontagemDaTarefa.CriarExecucao(plano, 5, "telegram", 1, Agora);
        Assert.True(Assert.Single(etapas).Escrita);
        Assert.Equal("E5", execucao.Codigo);

        // Workspace geral, agente sem implementar, perfil padrão plan e Claude: nada é montado.
        Assert.Throws<ArgumentException>(() => MontagemDaTarefa.MontarEscrita("dev", codex, JobExecutionContext.General("/g"), "x"));
        var semImplementar = AgentesDominioTests.Versao(new ConfiguracaoDoAgente(AgentKind.Codex, AgentPermissionProfile.Auto,
            AgentPermissionProfile.Auto, true, capacidades: [CapacidadeDoAgente.Revisar], repositorios: ["*"]));
        Assert.Contains("implementar", Assert.Throws<ArgumentException>(() =>
            MontagemDaTarefa.MontarEscrita("dev", semImplementar, contexto, "x")).Message);
        var padraoPlan = AgentesDominioTests.Versao(new ConfiguracaoDoAgente(AgentKind.Codex, AgentPermissionProfile.Plan,
            AgentPermissionProfile.Auto, true, capacidades: [CapacidadeDoAgente.Implementar], repositorios: ["*"]));
        Assert.Contains("não escala", Assert.Throws<ArgumentException>(() =>
            MontagemDaTarefa.MontarEscrita("dev", padraoPlan, contexto, "x")).Message);
        var claude = AgentesDominioTests.Versao(new ConfiguracaoDoAgente(AgentKind.Claude, AgentPermissionProfile.Auto,
            AgentPermissionProfile.Auto, true, capacidades: [CapacidadeDoAgente.Implementar], repositorios: ["*"]));
        Assert.Equal(ConfinamentoDeEscrita.RecusaDoClaude, Assert.Throws<ArgumentException>(() =>
            MontagemDaTarefa.MontarEscrita("dev", claude, contexto, "x")).Message);

        // Planos adulterados não viram execução: escrita em plan, perfil diferente do padrão, retomada automática.
        foreach (var adulterado in new[]
                 {
                     plano with { Perfil = AgentPermissionProfile.Plan },
                     plano with { Perfil = AgentPermissionProfile.Manual },
                     plano with { Etapas = [new EtapaPlanejada(1, TipoDeEtapa.TurnoDeAgente, true, PoliticaDeRetomada.Automatica)] },
                     plano with { Contexto = JobExecutionContext.General("/g") },
                     plano with { Escrita = false }
                 })
            Assert.Throws<EscritaSemWorktreeException>(() => MontagemDaTarefa.CriarExecucao(adulterado, 6, "telegram", 1, Agora));
    }

    // A sessão de escrita recebe só worktrees: o principal vira diretório de trabalho e cada vinculado, raiz gravável.
    [Fact]
    public void SessaoDeEscritaRecebeSoAsWorktreesNuncaOsCheckoutsPrincipais()
    {
        var codex = AgentesDominioTests.Versao(new ConfiguracaoDoAgente(AgentKind.Codex, AgentPermissionProfile.Auto,
            AgentPermissionProfile.Auto, true, "gpt", "high", capacidades: [CapacidadeDoAgente.Implementar], repositorios: ["*"]));
        var plano = MontagemDaTarefa.MontarEscrita("dev", codex, JobExecutionContext.Repository("@dante", "/repos/dante",
            [new RepositorioCadastrado("@back", "/repos/back")]), "x");
        var id = Guid.NewGuid();
        WorktreeDaExecucaoDto[] worktrees =
        [
            Worktree(id, "@dante", true, "/w/dante/E1", "/repos/dante"),
            Worktree(id, "@back", false, "/w/back/E1", "/repos/back")
        ];
        var pedido = SolicitacaoDaTarefa.Para(plano, 1, null, worktrees);
        Assert.Equal(("/w/dante/E1", "@dante", AgentPermissionProfile.Auto), (pedido.Context.WorkingDirectory,
            pedido.Context.RepositoryAlias, pedido.Profile));
        Assert.Equal(["/w/back/E1"], pedido.Context.RepositoriosVinculados.Select(v => v.Caminho));
        Assert.True(pedido.RegisteredAgent!.EscritaConfinada);
        // Os checkouts principais só seguem para a CLI declará-los não confiáveis; nunca como diretório.
        Assert.Equal(["/repos/dante", "/repos/back"], pedido.RegisteredAgent.CheckoutsDaEscrita);
        Assert.Equal(AgentPermissionProfile.Auto, pedido.RegisteredAgent.TetoDaExecucao);
        Assert.Contains(MontagemDaTarefa.AvisoDeEscrita, pedido.RegisteredAgent.Instrucoes);

        Assert.Throws<EscritaSemWorktreeException>(() => SolicitacaoDaTarefa.Para(plano, 1));
        Assert.Throws<EscritaSemWorktreeException>(() => SolicitacaoDaTarefa.Para(plano, 1, null, [worktrees[0]]));
        Assert.Throws<EscritaSemWorktreeException>(() => SolicitacaoDaTarefa.Para(plano, 1, null,
            [worktrees[0], worktrees[1] with { Caminho = "/repos/back" }]));
        Assert.Throws<EscritaSemWorktreeException>(() => SolicitacaoDaTarefa.Para(plano, 1, null,
            [worktrees[0] with { Estado = EstadoDaReservaDeWorktree.Reservada }, worktrees[1]]));
        Assert.Throws<EscritaSemWorktreeException>(() => SolicitacaoDaTarefa.Para(plano, 1, null,
            [worktrees[0], worktrees[1] with { Alias = "@outro" }]));
        // Leitura não aceita worktree e continua em plan, sem escrita confinada.
        var leitura = MontagemDaTarefa.Montar("dev", codex, JobExecutionContext.Repository("@dante", "/repos/dante"), "x");
        Assert.Throws<InvalidOperationException>(() => SolicitacaoDaTarefa.Para(leitura, 1, null, worktrees));
        var pedidoDeLeitura = SolicitacaoDaTarefa.Para(leitura, 1);
        Assert.Equal((AgentPermissionProfile.Plan, false), (pedidoDeLeitura.Profile, pedidoDeLeitura.RegisteredAgent!.EscritaConfinada));
    }

    [Fact]
    public async Task SessionRegistryRecusaEscritaConfinadaSemObservadorEmClaudeOuEmPlan()
    {
        var drivers = new FakeSessionDriverFactory();
        await using var sessions = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance,
            new TelegramDeliveryService(new TarefaPelaFilaEndToEndTests.BotDeTeste(), NullLogger<TelegramDeliveryService>.Instance));
        var codex = AgentesDominioTests.Versao(new ConfiguracaoDoAgente(AgentKind.Codex, AgentPermissionProfile.Auto,
            AgentPermissionProfile.Auto, true, capacidades: [CapacidadeDoAgente.Implementar], repositorios: ["*"]));
        var contexto = JobExecutionContext.Repository("@dante", Path.GetTempPath());
        SessionStartRequest Pedido(AgentKind driver, AgentPermissionProfile perfil, bool comObservador) =>
            new(1, driver, contexto, null, perfil, AgentModelSelection.CliDefault,
                RegisteredAgent: new AgenteDaSessao("dev", driver == AgentKind.Codex ? codex
                    : AgentesDominioTests.Versao(new ConfiguracaoDoAgente(AgentKind.Claude, AgentPermissionProfile.Auto,
                        AgentPermissionProfile.Auto, true, capacidades: [CapacidadeDoAgente.Implementar], repositorios: ["*"])))
                { EscritaConfinada = true },
                Observer: comObservador ? new ObservadorDaTarefa(sessions, 1) : null);

        Assert.Contains("etapa de uma execução", (await sessions.StartAsync(Pedido(AgentKind.Codex, AgentPermissionProfile.Auto, false))).Error);
        Assert.Equal(ConfinamentoDeEscrita.RecusaDoClaude,
            (await sessions.StartAsync(Pedido(AgentKind.Claude, AgentPermissionProfile.Auto, true))).Error);
        Assert.False((await sessions.StartAsync(Pedido(AgentKind.Codex, AgentPermissionProfile.Plan, true))).Accepted);
        Assert.Empty(drivers.Created);

        var aceita = await sessions.StartAsync(Pedido(AgentKind.Codex, AgentPermissionProfile.Auto, true));
        Assert.True(aceita.Accepted, aceita.Error);
        var opcoes = Assert.Single(drivers.Created).StartOptions!;
        Assert.True(opcoes.EscritaConfinada);
        Assert.Empty(opcoes.ToolServers ?? []);
    }

    // O driver do Claude não tem confinamento de filesystem: a sessão de escrita é recusada antes de existir processo.
    [Fact]
    public async Task DriverDoClaudeRecusaEscritaConfinadaSemIniciarProcesso()
    {
        var launcher = new LauncherQueNaoDeveSerChamado();
        await using var driver = new ClaudeSessionDriver(launcher);
        var recusa = await Assert.ThrowsAsync<AgentProtocolException>(() => driver.StartAsync(new AgentSessionStartOptions(
            Path.GetTempPath(), Profile: AgentPermissionProfile.Auto, EscritaConfinada: true)));
        Assert.Equal(ConfinamentoDeEscrita.RecusaDoClaude, recusa.Message);
        Assert.Equal(0, launcher.Chamadas);
        Assert.False(ConfinamentoDeEscrita.Suporta(AgentKind.Claude));
        Assert.True(ConfinamentoDeEscrita.Suporta(AgentKind.Codex));
    }

    // Limite global operacional = 1 até a ativação autorizada da AD-63: a composição recusa qualquer valor maior.
    [Fact]
    public void LimiteGlobalAcimaDeUmEhRecusadoNaComposicao()
    {
        var padrao = WorkerComposition.OpcoesDeWorktrees(new ConfigurationBuilder().Build());
        Assert.Equal((1, 1), (padrao.LimiteGlobal, padrao.LimitePorRepositorio));
        Assert.Contains("AD-63", Assert.Throws<ArgumentException>(() => WorkerComposition.OpcoesDeWorktrees(Configuracao(
            ("Orquestracao:Worktrees:LimiteGlobal", "2")))).Message);
        Assert.Throws<ArgumentException>(() => WorkerComposition.OpcoesDeWorktrees(Configuracao(
            ("Orquestracao:Worktrees:Raiz", "relativa"))));
        Assert.Throws<ArgumentException>(() => WorkerComposition.OpcoesDeWorktrees(Configuracao(
            ("Orquestracao:Worktrees:LimitePorRepositorio", "0"))));
        // A capacidade existe no código (testes de contenção usam N), só não é ativável pelo host.
        new OpcoesDeWorktrees { LimiteGlobal = 3, LimitePorRepositorio = 2 }.Validar(paralelismoAutorizado: true);
        Assert.Throws<ArgumentException>(() => new OpcoesDeWorktrees { LimiteGlobal = 1, LimitePorRepositorio = 2 }
            .Validar(paralelismoAutorizado: true));
    }

    internal static (Execucao Execucao, EtapaDaExecucao Etapa) Tarefa()
    {
        var execucao = new Execucao(1, TipoDeExecucao.Tarefa, "telegram", 1, "{}", "x", Agora, Guid.NewGuid());
        return (execucao, new EtapaDaExecucao(execucao, 1, TipoDeEtapa.TurnoDeAgente, true, PoliticaDeRetomada.ComConfirmacao, Agora));
    }

    private static WorktreeDaExecucaoDto Worktree(Guid idExecucao, string alias, bool principal, string caminho,
        string repositorio) => new(Guid.NewGuid(), idExecucao, alias, principal, caminho, repositorio,
        "dante/dev/e1", Sha, EstadoDaReservaDeWorktree.Pronta, null);

    private static IConfiguration Configuracao(params (string Chave, string Valor)[] valores) =>
        new ConfigurationBuilder().AddInMemoryCollection(valores.Select(v => KeyValuePair.Create(v.Chave, (string?)v.Valor))).Build();

    private sealed class LauncherQueNaoDeveSerChamado : IInteractiveAgentProcessLauncher
    {
        public int Chamadas;

        public Task<InteractiveAgentProcess> StartAsync(AgentProcessRequest request, Func<string, string>? redactOutput = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Chamadas);
            throw new InvalidOperationException("O Claude não poderia ter iniciado processo.");
        }
    }
}
