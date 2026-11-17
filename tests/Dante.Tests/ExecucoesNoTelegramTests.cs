using System.Threading.Channels;
using Dante.Application.Agentes;
using Dante.Worker.Jobs;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

// #305 sem banco: o isolamento dos jobs legados entre usuários da allowlist (/status, /cancel J…) e as respostas dos
// comandos de acompanhamento quando o ledger não está habilitado. O registro em memória do job é quem recusa o acesso
// cruzado, não só o adaptador.
public sealed class ExecucoesNoTelegramTests
{
    private const long Dono = 123;
    private const long Outro = 456;

    [Fact]
    public async Task JobDeOutroUsuarioNaoApareceNoStatusNemPodeSerCancelado()
    {
        var api = new BotDeDoisUsuarios();
        var runner = new RunnerQueEspera();
        var jobs = new JobRegistry();
        using var service = Servico(api, runner, jobs);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enviar(Dono, "/claude analise");
            Assert.Equal((Dono, "Claude iniciado. Job ID: J000001 (General)."), await api.ProximaAsync());
            await runner.Iniciado.Task.WaitAsync(TimeSpan.FromSeconds(10));

            api.Enviar(Outro, "/status");
            Assert.Equal((Outro, "Nenhum job registrado."), await api.ProximaAsync());
            // Indistinguível de um job inexistente, e nada é cancelado.
            api.Enviar(Outro, "/cancel J000001");
            Assert.Equal((Outro, "Job J000001 não encontrado ou já encerrado."), await api.ProximaAsync());
            api.Enviar(Outro, "/cancel J000999");
            Assert.Equal((Outro, "Job J000999 não encontrado ou já encerrado."), await api.ProximaAsync());
            Assert.False(runner.Cancelado);
            Assert.Equal(JobStatus.Running, Assert.Single(jobs.GetVisible(Dono)).Status);
            Assert.Empty(jobs.GetVisible(Outro));
            Assert.False(jobs.TryCancel("J000001", Outro, out _));

            api.Enviar(Dono, "/status");
            var status = await api.ProximaAsync();
            Assert.Equal(Dono, status.Chat);
            Assert.Contains("J000001 Claude General: Running", status.Texto);
            // Sem o ledger habilitado, o /status não ganha linha de execuções.
            Assert.DoesNotContain("Execuções:", status.Texto);

            api.Enviar(Dono, "/cancel J000001 mudei de ideia");
            Assert.Equal((Dono, "Cancelamento solicitado para J000001 (General)."), await api.ProximaAsync());
            await runner.Encerrado.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(runner.Cancelado);
            var fim = await api.ProximaAsync();
            Assert.Equal(Dono, fim.Chat);
            Assert.Contains("J000001", fim.Texto);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // A proteção está no registro: a sobrecarga do usuário nunca alcança job de outro dono nem job sem dono registrado; a
    // do sistema (posse perdida, encerramento) continua alcançando qualquer job.
    [Fact]
    public void RegistroDeJobsSoEntregaEProtegeOsJobsDoDono()
    {
        var jobs = new JobRegistry();
        var contexto = Dante.Application.Contextos.JobExecutionContext.General("/tmp/g");
        var meu = jobs.Create("Claude", contexto, CancellationToken.None, ownerId: Dono).Snapshot;
        var fila = jobs.CreateQueued(10, "Codex", contexto, CancellationToken.None, ownerId: Outro)!.Value.Snapshot;
        var semDono = jobs.Create("Claude", contexto, CancellationToken.None).Snapshot;
        Assert.Equal([meu.Id], jobs.GetVisible(Dono).Select(j => j.Id));
        Assert.Equal([fila.Id], jobs.GetVisible(Outro).Select(j => j.Id));
        Assert.Equal(3, jobs.GetVisible().Count);
        Assert.Equal(Dono, meu.OwnerId);

        Assert.False(jobs.TryCancel(fila.Id, Dono, out _));
        Assert.False(jobs.TryCancel(semDono.Id, Dono, out _));
        Assert.False(jobs.GetVisible(Outro).Single().CancellationRequested);
        Assert.True(jobs.TryCancel(fila.Id, Outro, out var cancelado));
        Assert.True(cancelado!.CancellationRequested);
        Assert.True(jobs.TryCancel(semDono.Id, out _));
    }

    [Fact]
    public async Task SemLedgerOsComandosNovosInformamQueNaoEstaoHabilitados()
    {
        var api = new BotDeDoisUsuarios();
        using var service = Servico(api, new RunnerQueEspera(), new JobRegistry());
        await service.StartAsync(CancellationToken.None);
        try
        {
            foreach (var comando in new[] { "/execucoes", "/execucao E12", "/aguardando", "/cancel E12 motivo" })
            {
                api.Enviar(Dono, comando);
                Assert.Equal((Dono, TelegramExecucoes.Desabilitado), await api.ProximaAsync());
            }
            api.Enviar(Dono, "/cancel");
            Assert.Equal((Dono, "Uso: /cancel <J…|E…> [motivo]"), await api.ProximaAsync());
            // Um J… sem ledger continua só no registro em memória, como antes.
            api.Enviar(Dono, "/cancel J000042");
            Assert.Equal((Dono, "Job J000042 não encontrado ou já encerrado."), await api.ProximaAsync());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static TelegramPollingService Servico(BotDeDoisUsuarios api, RunnerQueEspera runner, JobRegistry jobs)
    {
        var options = Options.Create(new TelegramOptions
        {
            BotToken = "test-token",
            AllowedUserIds = $"{Dono},{Outro}",
            AgentWorkingDirectory = "/tmp/agent-work"
        });
        return new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), runner, runner, jobs,
            NullLogger<TelegramPollingService>.Instance);
    }

    private sealed class BotDeDoisUsuarios : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<(long Chat, string Texto)> mensagens = Channel.CreateUnbounded<(long, string)>();
        private long proximo;

        // Chat privado: o id do chat é o do usuário.
        public void Enviar(long usuario, string texto) => updates.Writer.TryWrite(new TelegramUpdate(
            Interlocked.Increment(ref proximo), new TelegramMessage(new TelegramChat(usuario), texto, new TelegramUser(usuario),
                proximo)));

        public async Task<(long Chat, string Texto)> ProximaAsync() =>
            await mensagens.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken) =>
            [await updates.Reader.ReadAsync(cancellationToken)];

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            mensagens.Writer.TryWrite((chatId, text));
            return Task.CompletedTask;
        }
    }

    // Fica rodando até o job ser cancelado.
    private sealed class RunnerQueEspera : ICodexRunner, IClaudeRunner
    {
        public TaskCompletionSource Iniciado { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Encerrado { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelado { get; private set; }

        public async Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Application.Anexos.Attachment>? attachments = null,
            IReadOnlyList<string>? additionalDirectories = null)
        {
            Iniciado.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelado = true;
            }
            finally
            {
                Encerrado.TrySetResult();
            }
            return new AgentProcessResult(AgentProcessStatus.Cancelled, "", "", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }
    }
}
