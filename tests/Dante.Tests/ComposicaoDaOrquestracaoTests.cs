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
        Assert.Equal(["DependenciaDeEtapa", "EtapaDaExecucao", "EventoDeOrquestracao", "Execucao", "SessaoDeAgente"],
            ledger.Model.GetEntityTypes().Select(e => e.ClrType.Name).Order());
        Assert.All(ledger.Model.GetEntityTypes(), e =>
        {
            Assert.Equal("orquestracao", e.GetSchema());
            Assert.Equal($"Dante.Infrastructure.Modulos.Execucoes", typeof(OrquestracaoDbContext).Assembly.GetTypes()
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
        Assert.DoesNotContain(brain.Model.GetEntityTypes(), e => e.ClrType.Namespace == "Dante.Domain.Execucoes");
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
