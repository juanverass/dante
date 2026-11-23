using Dante.Application;
using Dante.Infrastructure;
using Dante.Infrastructure.Data;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Worker;
using Dante.Worker.Execucoes;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

// #299: o ledger (AD-66) só liga com banco e a chave própria; nos demais casos o Worker compõe exatamente o legado.
public sealed class ComposicaoDaOrquestracaoTests
{
    private const string BancoFicticio = "Host=localhost;Database=nao_conectar";

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "true")]
    [InlineData(BancoFicticio, null)]
    [InlineData(BancoFicticio, "false")]
    [InlineData(BancoFicticio, "sim")]
    public void SemBancoOuSemAChaveOWorkerNaoRegistraOEspelho(string? banco, string? habilitada)
    {
        var configuracao = Configuracao(banco, habilitada);
        var services = new ServiceCollection().AddLogging().AddApplication().AddInfrastructure(configuracao)
            .AddWorker(configuracao);
        Assert.DoesNotContain(services, s => s.ServiceType == typeof(EspelhoDoLedger));
        Assert.DoesNotContain(services, s => s.ImplementationType == typeof(EncerramentoDoLedger));
        // Cadastro de agentes (AD-68): mesma condição; sem ele, /agentes, /agente e /tarefa respondem indisponível.
        Assert.DoesNotContain(services, s => s.ServiceType == typeof(Dante.Worker.Telegram.TelegramAgentes));
        // Sem a fila, /tarefa não tem executor: nada é criado nem executado.
        Assert.DoesNotContain(services, s => s.ServiceType == typeof(ExecutorDeTarefasDaFila));
        Assert.Equal(banco is not null, services.Any(s => s.ServiceType == typeof(OrquestracaoDbContext)));
        Assert.False(WorkerComposition.OrquestracaoHabilitada(configuracao));
    }

    [Fact]
    public void ComBancoEAChaveOEspelhoChegaAosRegistriesSemConectarNemMigrar()
    {
        var configuracao = Configuracao(BancoFicticio, "true");
        var services = new ServiceCollection().AddLogging().AddApplication().AddInfrastructure(configuracao)
            .AddWorker(configuracao);
        Assert.Single(services, s => s.ServiceType == typeof(EspelhoDoLedger) && s.Lifetime == ServiceLifetime.Singleton);
        Assert.Single(services, s => s.ServiceType == typeof(Dante.Worker.Telegram.TelegramAgentes));
        // /tarefa (AD-68): o executor de Tarefa entra na fila ao lado do de jobs; é o único caminho que a executa.
        Assert.Single(services, s => s.ServiceType == typeof(ExecutorDeTarefasDaFila) && s.Lifetime == ServiceLifetime.Singleton);
        // Roteiro de grupo (#308): o terceiro executor, também só pela fila.
        Assert.Single(services, s => s.ServiceType == typeof(ExecutorDeRoteirosDaFila) && s.Lifetime == ServiceLifetime.Singleton);
        // Delegação rastreada (#309): o quarto, também só pela fila.
        Assert.Single(services, s => s.ServiceType == typeof(ExecutorDeDelegacoesDaFila) && s.Lifetime == ServiceLifetime.Singleton);
        // Coordenação por papel (#310): o quinto, também só pela fila.
        Assert.Single(services, s => s.ServiceType == typeof(ExecutorDeCoordenacoesDaFila) && s.Lifetime == ServiceLifetime.Singleton);
        Assert.Equal(5, services.Count(s => s.ServiceType == typeof(IExecutorDeEtapa)));
        // O encerramento do ledger para por último: registrado antes dos demais hosted services.
        var hospedados = services.Where(s => s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .Select(s => s.ImplementationType).ToArray();
        Assert.Equal(typeof(EncerramentoDoLedger), hospedados[0]);
        Assert.Contains(typeof(Dante.Worker.Telegram.TelegramPollingService), hospedados);
        // Mesmo registro do Worker para SessionRegistry, com driver falso: o grafo real tocaria ~/.dante.
        services.AddSingleton<IAgentSessionDriverFactory, FakeSessionDriverFactory>();
        services.AddSingleton<IAgentSessionEventSink, RecordingSessionSink>();
        services.AddSingleton<IAgentToolServers>(new CompositorDeFerramentas([]));
        services.AddSingleton(new Dante.Worker.Attachments.AttachmentStore(Path.Combine(Path.GetTempPath(), "dante-299-" + Guid.NewGuid().ToString("N"))));
        using var provider = services.BuildServiceProvider();
        var espelho = provider.GetRequiredService<EspelhoDoLedger>();
        // O banco fictício não existe: resolver e compor não abre conexão nem aplica migration.
        Assert.Same(espelho, Campo(provider.GetRequiredService<JobRegistry>(), "ledger"));
        Assert.Same(espelho, Campo(provider.GetRequiredService<SessionRegistry>(), "ledger"));
    }

    [Fact]
    public void SemOEspelhoOsRegistriesFicamSemLedger()
    {
        var configuracao = Configuracao(BancoFicticio, null);
        var services = new ServiceCollection().AddLogging().AddApplication().AddInfrastructure(configuracao)
            .AddWorker(configuracao);
        services.AddSingleton<IAgentSessionDriverFactory, FakeSessionDriverFactory>();
        services.AddSingleton<IAgentSessionEventSink, RecordingSessionSink>();
        services.AddSingleton<IAgentToolServers>(new CompositorDeFerramentas([]));
        services.AddSingleton(new Dante.Worker.Attachments.AttachmentStore(Path.Combine(Path.GetTempPath(), "dante-299-" + Guid.NewGuid().ToString("N"))));
        using var provider = services.BuildServiceProvider();
        Assert.Null(Campo(provider.GetRequiredService<JobRegistry>(), "ledger"));
        Assert.Null(Campo(provider.GetRequiredService<SessionRegistry>(), "ledger"));
    }

    [Fact]
    public void ModeloDoLedgerTemSchemaEHistoricoPropriosSemTocarOBrain()
    {
        using var ledger = new OrquestracaoDbContext(OrquestracaoDbContextFactory.Opcoes(BancoFicticio));
        // Ledger (AD-66), fila (AD-67), cadastro de agentes (AD-68), grupos (AD-69) e reservas de worktree (AD-71) no
        // mesmo schema e histórico.
        Assert.Equal(["Agente", "AprovacaoDeAcao", "AtivacaoDeVersaoDoGrupo", "CoordenacaoDaExecucao", "Delegacao", "DependenciaDeEtapa", "EtapaDaExecucao", "EventoDeOrquestracao",
            "Execucao", "GrupoDeAgentes", "ParticipanteDaExecucao", "ParticipanteDaVersaoDoGrupo", "PedidoDeAprovacao",
            "PlanoDaCoordenacao", "ReservaDeWorktree", "RoteiroDaExecucao", "SessaoDeAgente", "SinteseDaCoordenacao", "TrabalhoDaFila", "VereditoRegistrado", "VersaoDoAgente", "VersaoDoGrupo"], ledger.Model.GetEntityTypes().Select(e => e.ClrType.Name).Order());
        Assert.All(ledger.Model.GetEntityTypes(), e =>
        {
            Assert.Equal("orquestracao", e.GetSchema());
            Assert.Equal(e.ClrType.Namespace switch
            {
                "Dante.Domain.Agentes" => "Dante.Infrastructure.Modulos.Agentes",
                "Dante.Domain.Grupos" => "Dante.Infrastructure.Modulos.Grupos",
                _ => "Dante.Infrastructure.Modulos.Execucoes"
            }, typeof(OrquestracaoDbContext).Assembly.GetTypes()
                .Single(t => t.Name == e.ClrType.Name + "DbMapping").Namespace);
            Assert.Equal("xmin", e.FindProperty("Versao")!.GetColumnName());
            Assert.True(e.FindProperty("Versao")!.IsConcurrencyToken);
        });
        Assert.False(ledger.Database.HasPendingModelChanges());
        var script = ledger.GetService<IMigrator>().GenerateScript();
        Assert.Contains("orquestracao.__historico_de_migrations", script.Replace("\"", ""));
        Assert.DoesNotContain("brain_", script);
        Assert.DoesNotContain("CREATE EXTENSION", script);

        using var brain = new DanteDbContext(new DbContextOptionsBuilder<DanteDbContext>().UseNpgsql(BancoFicticio).Options);
        Assert.DoesNotContain(brain.Model.GetEntityTypes(), e => e.ClrType.Namespace is "Dante.Domain.Execucoes" or "Dante.Domain.Agentes" or "Dante.Domain.Grupos");
        Assert.DoesNotContain("orquestracao", brain.GetService<IMigrator>().GenerateScript());
    }

    [Fact]
    public void JobsSemLedgerMantemContadorLocalEReservaNuncaRepeteId()
    {
        var jobs = new JobRegistry();
        Assert.Equal("J000001", jobs.Create("Codex", JobExecutionContext.General("/g"), CancellationToken.None).Snapshot.Id);
        Assert.Equal("J000002", jobs.CreateAsync("Claude", JobExecutionContext.General("/g"), 1, CancellationToken.None)
            .GetAwaiter().GetResult().Snapshot.Id);

        var ids = new SessionIdGenerator();
        Assert.Equal(("S000001", false), ids.NextSessionId(null));
        Assert.Equal(("S000500", true), ids.NextSessionId(500));
        Assert.Equal("S000501", ids.NextSessionId());
        // Reserva atrasada (contador local passou à frente): cai no contador, sem repetir.
        Assert.Equal(("S000502", false), ids.NextSessionId(501));
        Assert.Equal(("S000503", false), ids.NextSessionId(3));
    }

    // Segunda rodada do Codex: com a fila cheia, a descarga do encerramento espera espaço e o consumidor, em vez de
    // retornar com registros aceitos ainda pendentes.
    [Fact]
    public async Task DescargaComFilaCheiaEsperaOsRegistrosAceitos()
    {
        using var provider = new ServiceCollection().AddScoped(typeof(Dante.Application.Execucoes.ILedgerDeExecucoesAppService),
            _ => ApplicationCompositionTests.PortaFalsa.Criar(typeof(Dante.Application.Execucoes.ILedgerDeExecucoesAppService)))
            .BuildServiceProvider();
        await using var espelho = new EspelhoDoLedger(provider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EspelhoDoLedger>.Instance);
        var liberar = new TaskCompletionSource();
        var bloqueado = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gravados = 0;
        espelho.Registrar("bloqueio", async (_, _) => { bloqueado.SetResult(); await liberar.Task; });
        // O consumidor já tirou o bloqueio da fila: a capacidade inteira fica livre para os registros.
        await bloqueado.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < EspelhoDoLedger.Capacidade; i++)
            espelho.Registrar("registro", (_, _) => { Interlocked.Increment(ref gravados); return Task.CompletedTask; });

        var descarga = espelho.DescarregarAsync();
        await Task.Delay(300);
        Assert.False(descarga.IsCompleted);
        liberar.SetResult();
        await descarga.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(EspelhoDoLedger.Capacidade, gravados);
    }

    private static IConfiguration Configuracao(string? banco, string? habilitada) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Dante"] = banco,
            ["Orquestracao:Habilitada"] = habilitada
        }).Build();

    private static object? Campo(object alvo, string nome) => alvo.GetType()
        .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        .Single(f => f.Name == nome || f.Name == $"<{nome}>P").GetValue(alvo);
}
