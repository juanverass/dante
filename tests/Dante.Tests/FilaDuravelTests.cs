using Dante.Application;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure;
using Dante.Infrastructure.Agentes;
using Dante.Worker;
using Dante.Worker.Execucoes;
using Dante.Worker.Jobs;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dante.Tests;

// #300: regras da fila durável (AD-67) que não precisam de banco, e a composição: sem Orquestracao:Habilitada o
// Worker compõe exatamente o legado.
public sealed class FilaDuravelTests
{
    private const string BancoFicticio = "Host=localhost;Database=nao_conectar";

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "true")]
    [InlineData(BancoFicticio, null)]
    [InlineData(BancoFicticio, "false")]
    public void SemAChaveOuSemBancoOWorkerNaoCompoeFilaNemProcessador(string? banco, string? habilitada)
    {
        var configuracao = Configuracao(banco, habilitada);
        var services = new ServiceCollection().AddLogging().AddApplication().AddInfrastructure(configuracao).AddWorker(configuracao);
        Assert.DoesNotContain(services, s => s.ServiceType == typeof(ProcessadorDaFila));
        Assert.DoesNotContain(services, s => s.ServiceType == typeof(ExecutorDeJobsDaFila));
        Assert.DoesNotContain(services, s => s.ServiceType == typeof(IExecutorDeEtapa));
        Assert.DoesNotContain(services, s => s.ServiceType == typeof(OpcoesDaFila));
        // Os hosted services são exatamente os do legado.
        Assert.Equal([typeof(Dante.Worker.Worker), typeof(TelegramPollingService)], services
            .Where(s => s.ServiceType == typeof(IHostedService)).Select(s => s.ImplementationType));
    }

    [Fact]
    public void ComAChaveOProcessadorIniciaAntesDoTelegramEParaDepoisSemConectar()
    {
        var configuracao = Configuracao(BancoFicticio, "true");
        var services = new ServiceCollection().AddLogging().AddApplication().AddInfrastructure(configuracao).AddWorker(configuracao);
        var hospedados = services.Where(s => s.ServiceType == typeof(IHostedService)).ToArray();
        Assert.Equal(4, hospedados.Length);
        Assert.Equal(typeof(EncerramentoDoLedger), hospedados[0].ImplementationType);
        // Registrado por fábrica, logo depois do encerramento do ledger e antes do Worker e do polling.
        Assert.NotNull(hospedados[1].ImplementationFactory);
        Assert.Equal([typeof(Dante.Worker.Worker), typeof(TelegramPollingService)], hospedados[2..].Select(s => s.ImplementationType));
        Assert.Equal(new OpcoesDaFila(), Assert.Single(services, s => s.ServiceType == typeof(OpcoesDaFila)).ImplementationInstance);
        using var provider = services.BuildServiceProvider();
        // Resolver não abre conexão: o banco fictício não existe.
        Assert.Same(provider.GetRequiredService<ProcessadorDaFila>(), hospedados[1].ImplementationFactory!(provider));
        // Um executor por tipo de execução: jobs (Avulsa, #300), tarefas (Tarefa, #301), roteiros de grupo (Roteiro,
        // #308), delegações (Delegada, #309) e coordenações (Coordenada, #310), os mesmos singletons. Resolver os de
        // roteiros, tarefas, delegações e coordenações não exige o registro de sessões (resolvido quando a etapa roda).
        var executores = provider.GetServices<IExecutorDeEtapa>().ToArray();
        Assert.Equal([TipoDeExecucao.Avulsa, TipoDeExecucao.Tarefa, TipoDeExecucao.Roteiro, TipoDeExecucao.Delegada,
            TipoDeExecucao.Coordenada], executores.Select(e => e.Tipo));
        Assert.Same(provider.GetRequiredService<ExecutorDeJobsDaFila>(), executores[0]);
        Assert.Same(provider.GetRequiredService<ExecutorDeTarefasDaFila>(), executores[1]);
        Assert.Same(provider.GetRequiredService<ExecutorDeRoteirosDaFila>(), executores[2]);
        Assert.Same(provider.GetRequiredService<ExecutorDeDelegacoesDaFila>(), executores[3]);
        Assert.Same(provider.GetRequiredService<ExecutorDeCoordenacoesDaFila>(), executores[4]);
    }

    [Fact]
    public void OpcoesDaFilaVemDaConfiguracaoEValoresInvalidosImpedemAInicializacao()
    {
        var opcoes = WorkerComposition.OpcoesDaFila(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Orquestracao:Fila:LeaseSegundos"] = "90",
            ["Orquestracao:Fila:HeartbeatSegundos"] = "30",
            ["Orquestracao:Fila:PollingSegundos"] = "2.5",
            ["Orquestracao:Fila:Slots"] = "2"
        }).Build());
        Assert.Equal((TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2.5), 2),
            (opcoes.Lease, opcoes.Heartbeat, opcoes.Polling, opcoes.Slots));
        var padrao = WorkerComposition.OpcoesDaFila(new ConfigurationBuilder().Build());
        Assert.Equal((TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), 1),
            (padrao.Lease, padrao.Heartbeat, padrao.Polling, padrao.Slots));
        // Heartbeat que não cabe duas vezes no lease perderia a posse numa única falha.
        Assert.Throws<ArgumentException>(() => WorkerComposition.OpcoesDaFila(Fila("LeaseSegundos", "30", "HeartbeatSegundos", "20")));
        Assert.Throws<ArgumentException>(() => WorkerComposition.OpcoesDaFila(Fila("Slots", "0")));
        Assert.Throws<FormatException>(() => WorkerComposition.OpcoesDaFila(Fila("PollingSegundos", "rápido")));
    }

    [Fact]
    public void TrabalhoGuardaSoIdentificadoresEValidaAChave()
    {
        var agora = DateTimeOffset.UtcNow;
        var execucao = ExecucoesDominioTests.Nova(7);
        var etapa = ExecucoesDominioTests.Etapa(execucao, 1);
        var trabalho = TrabalhoDaFila.AvancarExecucao(execucao, etapa, "telegram:1:2", agora);
        Assert.Equal((EstadoDoTrabalho.Pendente, agora, 0, 0L, TrabalhoDaFila.MaximoDeTentativasPadrao),
            (trabalho.Estado, trabalho.DisponivelEm, trabalho.Tentativas, trabalho.Geracao, trabalho.MaximoDeTentativas));
        Assert.Equal($"{{\"execucao\":\"{execucao.Codigo}\",\"etapa\":1,\"tentativa\":1}}", trabalho.Dados);
        Assert.Throws<ArgumentException>(() => new TrabalhoDaFila(TipoDeTrabalho.AvancarExecucao, " ", agora));
        Assert.Throws<ArgumentException>(() => new TrabalhoDaFila(TipoDeTrabalho.AvancarExecucao, new string('x', 201), agora));
        Assert.Throws<ArgumentException>(() => new TrabalhoDaFila(TipoDeTrabalho.AvancarExecucao, "k", agora, idEtapa: Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new TrabalhoDaFila(TipoDeTrabalho.AvancarExecucao, "k", agora, dados: new string('x', 2001)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrabalhoDaFila(TipoDeTrabalho.AvancarExecucao, "k", agora, maximoDeTentativas: 0));
        execucao.Cancelar(null, agora);
        Assert.Throws<InvalidOperationException>(() => TrabalhoDaFila.AvancarExecucao(execucao, etapa, "outra", agora));
    }

    [Fact]
    public void SessaoGuardaBootEUnidadeDeContencao()
    {
        var boot = Guid.NewGuid();
        var sessao = new SessaoDeAgente(1, AgentKind.Claude, "telegram", 1, "{}", AgentPermissionProfile.Manual,
            DateTimeOffset.UtcNow, bootDoWorker: boot, maquinaDoWorker: "homelab");
        sessao.MarcarIniciada("u", null, DateTimeOffset.UtcNow, "dante-agent-0123456789abcdef0123456789abcdef.scope");
        Assert.Equal((boot, "homelab", "dante-agent-0123456789abcdef0123456789abcdef.scope"),
            (sessao.BootDoWorker, sessao.MaquinaDoWorker, sessao.UnidadeDeContencao));
        Assert.Throws<ArgumentException>(() => new SessaoDeAgente(1, AgentKind.Claude, "telegram", 1, "{}",
            AgentPermissionProfile.Manual, DateTimeOffset.UtcNow, bootDoWorker: Guid.Empty, maquinaDoWorker: "homelab"));
        // Boot sem host (ou o contrário) não identifica o processo.
        Assert.Throws<ArgumentException>(() => new SessaoDeAgente(1, AgentKind.Claude, "telegram", 1, "{}",
            AgentPermissionProfile.Manual, DateTimeOffset.UtcNow, bootDoWorker: boot));
        var outra = new SessaoDeAgente(2, AgentKind.Claude, "telegram", 1, "{}", AgentPermissionProfile.Manual, DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => outra.MarcarIniciada("u", null, DateTimeOffset.UtcNow, new string('x', 101)));
        Assert.Equal(EstadoDaSessaoDeAgente.Iniciando, outra.Estado);
    }

    [Fact]
    public async Task SoUnidadesNoFormatoDoDanteSaoListadasOuParadas()
    {
        var saida = """
            dante-agent-0123456789abcdef0123456789abcdef.scope loaded active running Agente DANTE isolado
            ● dante-agent-fedcba9876543210fedcba9876543210.scope loaded failed failed Agente DANTE isolado
            dante-agent-0123456789abcdef0123456789abcdef.scope loaded active running duplicada
            dante-agent-x.scope loaded active running outro
            session-2.scope loaded active running Session 2 of User juan
            """;
        Assert.Equal(["dante-agent-0123456789abcdef0123456789abcdef.scope", "dante-agent-fedcba9876543210fedcba9876543210.scope"],
            UnidadesDeContencaoSystemd.Interpretar(saida));
        Assert.False(UnidadesDeContencaoSystemd.NomeValido("dante-agent-0123456789ABCDEF0123456789abcdef.scope"));
        Assert.False(UnidadesDeContencaoSystemd.NomeValido("dante.service"));
        Assert.False(UnidadesDeContencaoSystemd.NomeValido("dante-agent-0123456789abcdef0123456789abcdef.scope; rm -rf ~"));
        // Recusado antes de qualquer chamada ao gerenciador.
        await Assert.ThrowsAsync<ArgumentException>(() => new UnidadesDeContencaoSystemd().PararAsync("dante.service"));
    }

    private static IConfiguration Fila(params string[] pares)
    {
        var valores = new Dictionary<string, string?>();
        for (var i = 0; i < pares.Length; i += 2) valores["Orquestracao:Fila:" + pares[i]] = pares[i + 1];
        return new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
    }

    private static IConfiguration Configuracao(string? banco, string? habilitada) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Dante"] = banco,
            ["Orquestracao:Habilitada"] = habilitada
        }).Build();
}
