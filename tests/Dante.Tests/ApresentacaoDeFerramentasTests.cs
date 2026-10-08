using System.Collections.Concurrent;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dante.Tests;

public sealed class ApresentacaoDeFerramentasTests
{
    [Theory]
    [InlineData("listar_planilhas", "Buscando suas planilhas...")]
    [InlineData("descrever_planilha", "Analisando a planilha...")]
    [InlineData("ler_intervalo", "Lendo as informações necessárias...")]
    [InlineData("buscar_na_planilha", "Procurando na planilha...")]
    [InlineData("atualizar_celulas", "Atualizando a planilha...")]
    [InlineData("adicionar_linha", "Adicionando as informações...")]
    public async Task TelegramMostraProgressoEFalhaAmigaveisPreservandoIdentidade(string nome, string esperado)
    {
        var api = new ApiDeConversa();
        var log = new LogDeFerramentas();
        var delivery = new TelegramDeliveryService(api, log);
        var session = ApiDeConversa.Sessao();
        delivery.RegisterSession(session.Id, 123, 123, false);
        delivery.SetActiveSession(123, session.Id);
        var codex = ApresentacaoDeFerramentas.DoCodex("c", "dante_planilhas", nome, "dante_planilhas." + nome);
        var claude = ApresentacaoDeFerramentas.DoClaude("a", AgentToolKind.Tool, "mcp__dante_planilhas__" + nome,
            "mcp__dante_planilhas__" + nome + " argumentos privados");
        foreach (var tool in new[] { codex, claude })
        {
            Assert.Equal("dante_planilhas", tool.Server);
            Assert.Equal(nome, tool.ToolName);
            Assert.Contains(nome, tool.Description);
            await delivery.PublishAsync(session, tool with { SessionId = session.Id, TurnId = "T1" }, default);
            await delivery.PublishAsync(session, new ToolCompletedEvent(tool.ItemId, AgentToolKind.Tool, false)
                { SessionId = session.Id, TurnId = "T1" }, default);
        }
        await delivery.PublishAsync(session, new TurnCompletedEvent(AgentTurnOutcome.Completed)
            { SessionId = session.Id, TurnId = "T1" }, default);
        await api.AguardarAsync(delivery, session.Id);
        var text = api.Texto;
        Assert.Contains(esperado, text);
        Assert.Contains(esperado.TrimEnd('.') + " falhou.", text);
        Assert.DoesNotContain(nome, text);
        Assert.DoesNotContain("mcp__", text);
        Assert.DoesNotContain("argumentos privados", text);
        Assert.Contains(log.Mensagens, m => m.Contains("dante_planilhas/" + nome));
        Assert.DoesNotContain(log.Mensagens, m => m.Contains("argumentos privados"));
    }

    [Fact]
    public async Task FerramentaDesconhecidaNaoExpoeDescricaoEComandoMantemTratamentoProprio()
    {
        var api = new ApiDeConversa();
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        var session = ApiDeConversa.Sessao();
        delivery.RegisterSession(session.Id, 123, 123, false);
        delivery.SetActiveSession(123, session.Id);
        await delivery.PublishAsync(session, new ToolStartedEvent("u", AgentToolKind.Tool, "adapter_metodo_tecnico")
            { SessionId = session.Id, TurnId = "T1" }, default);
        await delivery.PublishAsync(session, new ToolStartedEvent("c", AgentToolKind.Command, "/bin/bash -lc 'git status --short'")
            { SessionId = session.Id, TurnId = "T1" }, default);
        await delivery.PublishAsync(session, new MessageCompletedEvent("m", "Dado literal — preservado")
            { SessionId = session.Id, TurnId = "T1" }, default);
        await delivery.PublishAsync(session, new TurnCompletedEvent(AgentTurnOutcome.Completed)
            { SessionId = session.Id, TurnId = "T1" }, default);
        await api.AguardarAsync(delivery, session.Id);
        Assert.Contains("Executando a tarefa...", api.Texto);
        Assert.DoesNotContain("adapter_metodo_tecnico", api.Texto);
        Assert.Contains("git status --short", api.Texto);
        Assert.Contains("Dado literal — preservado", api.Texto);
    }

    [Theory]
    [InlineData("dante_brain", "brain_buscar_conhecimento", "Consultando o Brain...")]
    [InlineData("github", "search", "Buscando informações no GitHub...")]
    [InlineData(null, "Read", "Lendo os arquivos...")]
    public void OutrasCapacidadesUsamIdentidadeEstruturada(string? server, string tool, string esperado) =>
        Assert.Equal(esperado, ApresentacaoDeFerramentas.Para(server, tool));

    private sealed class LogDeFerramentas : ILogger<TelegramDeliveryService>
    {
        public List<string> Mensagens { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Mensagens.Add(formatter(state, exception));
    }
}

internal sealed class ApiDeConversa : ITelegramBotApi
{
    private readonly ConcurrentQueue<string> mensagens = new();
    public string Texto => string.Join('\n', mensagens);
    public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TelegramUpdate>>([]);
    public Task SendMessageAsync(long chatId, string text, CancellationToken ct)
    { mensagens.Enqueue(text); return Task.CompletedTask; }
    public static AgentSessionSnapshot Sessao(string? root = null, AgentKind agente = AgentKind.Codex) =>
        new("S1", agente, 123, Dante.Application.Contextos.JobExecutionContext.General(root ?? "/tmp/general"),
            AgentPermissionProfile.Auto, AgentSessionState.Running, "T1", 0, [], true, DateTimeOffset.UtcNow, null, null);
    public async Task AguardarAsync(TelegramDeliveryService delivery, string id)
    {
        for (var i = 0; i < 100 && delivery.Get(id, 123)?.State != TelegramDeliveryState.Delivered; i++)
            await Task.Delay(50);
        Assert.Equal(TelegramDeliveryState.Delivered, delivery.Get(id, 123)?.State);
    }
}
