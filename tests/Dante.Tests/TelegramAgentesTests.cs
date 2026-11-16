using System.Diagnostics;
using System.Threading.Channels;
using Dante.Application.Agentes;
using Dante.Application.Anexos;
using Dante.Infrastructure.Contextos;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

// #301 (AD-68): agentes registrados de ponta a ponta pelo Telegram, com o caso de uso real sobre portas em memória e
// driver falso. Confere o que chega ao driver (instruções, modelo, perfil, ferramentas), que a versão fica congelada,
// que nada amplia o máximo e que /tarefa nunca executa nem escreve; e que os fluxos legados não mudam.
public sealed class TelegramAgentesTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-agentes-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSessionDriverFactory drivers = new();
    private readonly BotApi api = new();
    private readonly CatalogoDeModelosFixo catalogo = new();
    private readonly CadastroDeAgentesEmMemoria portas = new();
    private readonly JobRegistry jobs = new();
    private readonly RecordingRunner runner = new();
    private readonly List<IAsyncDisposable> descartaveis = [];
    private SessionRegistry? sessions;

    [Fact]
    public async Task CadastroPeloTelegramCriaConsultaVersionaDesativaReativaEArquiva()
    {
        await using var service = await StartAsync();
        Assert.Contains("Agente revisor criado (v1)", await Send(
            "/agente criar revisor driver=claude model=sonnet effort=low capacidades=revisar,pesquisar\nRevise com rigor.\nAponte riscos."));
        Assert.Contains("Já existe um agente revisor", await Send("/agente criar revisor driver=claude"));
        Assert.Contains("@ é sempre repositório", await Send("/agente criar @dev driver=claude"));
        Assert.Contains("reservado", await Send("/agente criar codex driver=claude"));
        Assert.Contains("Informe driver", await Send("/agente criar dev"));
        Assert.Contains("Opção inválida", await Send("/agente criar dev driver=claude perfil=auto"));
        Assert.Contains("não é oferecido", await Send("/agente criar dev driver=codex model=opus"));
        Assert.Contains("credencial", await Send("/agente criar dev driver=claude\nsenha=supersecreta123"));

        var lista = await Send("/agentes");
        Assert.Contains("- claude (embutido", lista);
        Assert.Contains("- revisor v1 — ativo, claude, modo plan (máx. plan)", lista);
        var detalhe = await Send("/agente ver revisor");
        Assert.Contains("Driver: claude; modelo sonnet; esforço low", detalhe);
        Assert.Contains("escrita: não", detalhe);
        Assert.Contains("Capacidades: revisar, pesquisar", detalhe);
        Assert.Contains("Repositórios: nenhum (só o workspace geral)", detalhe);
        Assert.Contains("Instruções:\nRevise com rigor.\nAponte riscos.", detalhe);
        Assert.Contains("Agente embutido", await Send("/agente ver claude"));

        Assert.Contains("agora está na v2", await Send("/agente editar revisor maximo=manual\nNova instrução."));
        Assert.Contains("Nada mudou", await Send("/agente editar revisor\nNova instrução."));
        Assert.Contains("não é editável", await Send("/agente editar codex model=fake-mini"));
        Assert.Contains("sem versão nova", await Send("/agente nome revisor Revisor de PR"));
        Assert.Contains("Versões: v2", await Send("/agente ver revisor"));
        Assert.Contains("Desative o agente revisor antes", await Send("/agente arquivar revisor"));
        Assert.Contains("desativado", await Send("/agente desativar revisor"));
        Assert.Contains("reativado", await Send("/agente reativar revisor"));
        await Send("/agente desativar revisor");
        Assert.Contains("arquivado", await Send("/agente arquivar revisor"));
        Assert.DoesNotContain("revisor", await Send("/agentes"));
        Assert.Contains("revisor v2 — arquivado", await Send("/agentes todos"));
        Assert.Contains("não é reutilizado", await Send("/agente criar revisor driver=claude"));
        Assert.Empty(drivers.Created);
    }

    [Fact]
    public async Task SessionStartComHandleCongelaAVersaoComInstrucoesModeloPerfilEFerramentasDaVersao()
    {
        await using var service = await StartAsync();
        await Send("/agente criar revisor driver=claude model=sonnet effort=low modo=plan maximo=manual ferramentas=brain\nRevise com rigor.");
        var aberta = await Send("/session start revisor");
        Assert.Contains("Sessão S000001 iniciada com o agente revisor v1 (Claude, General), modo plan", aberta);
        Assert.Contains("modelo sonnet, esforço low", aberta);
        var opcoes = drivers.Created[0].StartOptions!;
        Assert.Equal(AgentPermissionProfile.Plan, opcoes.Profile);
        Assert.Equal(new AgentModelSelection("sonnet", "low"), opcoes.ModelSelection);
        Assert.Contains("agente registrado revisor v1", opcoes.ContextInstructions);
        Assert.Contains("não revogam as regras anteriores", opcoes.ContextInstructions);
        Assert.Contains("Instruções do agente:\nRevise com rigor.", opcoes.ContextInstructions);
        // Só as ferramentas concedidas pela versão.
        Assert.Equal(["dante_brain"], opcoes.ToolServers!.Select(servidor => servidor.Name));

        // Editar não alcança a sessão aberta; a próxima sessão usa a versão nova.
        Assert.Contains("v2", await Send("/agente editar revisor model=default effort=default\nSeja breve."));
        api.Enqueue("olá");
        await Eventually(() => drivers.Created[0].Calls.Contains("turn:olá"));
        var ativa = sessions!.GetActive(123)!;
        Assert.Equal(("revisor", 1), (ativa.RegisteredAgent!.Handle, ativa.RegisteredAgent.Versao.Numero));
        Assert.Contains("(agente revisor v1)", await Send("/status"));
        Assert.Contains("agente revisor v2", await Send("/session start revisor"));
        var segunda = drivers.Created[1].StartOptions!;
        Assert.Contains("Seja breve.", segunda.ContextInstructions);
        Assert.DoesNotContain("Revise com rigor.", segunda.ContextInstructions);
        Assert.Equal(AgentModelSelection.CliDefault, segunda.ModelSelection);
        Assert.Contains("Revise com rigor.", drivers.Created[0].StartOptions!.ContextInstructions);
    }

    [Fact]
    public async Task SessaoNuncaAmpliaOMaximoNemORepositorioDaVersao()
    {
        var (registro, _) = Repositorios();
        await using var service = await StartAsync(registro);
        await Send("/agente criar revisor driver=codex maximo=manual repos=@first");
        Assert.Contains("passa do máximo", await Send("/session start revisor auto"));
        Assert.Contains("vêm da versão", await Send("/session start revisor model=fake-mini"));
        Assert.Contains("Escolha um agente só", await Send("/session start revisor claude"));
        Assert.Contains("não tem concessão para @second", await Send("/session start revisor @second"));
        Assert.Empty(drivers.Created);
        Assert.Contains("modo manual", await Send("/session start revisor manual @first"));
        Assert.Equal(AgentPermissionProfile.Manual, drivers.Created[0].StartOptions!.Profile);
        // Nem a troca explícita de modo na sessão passa do máximo (AD-24 + AD-68).
        var troca = await sessions!.ChangeModeAsync(123, null, AgentPermissionProfile.Auto);
        Assert.False(troca.Accepted);
        Assert.Contains("passa do máximo do agente revisor v1 (manual)", troca.Error);
        Assert.DoesNotContain(drivers.Created[0].Calls, chamada => chamada.StartsWith("mode:", StringComparison.Ordinal));
        // E o registry recusa por conta própria, mesmo que o chamador não confira.
        var versao = (await ResolverAsync("revisor")).Versao!;
        var direta = await sessions.StartAsync(new SessionStartRequest(123, AgentKind.Codex,
            JobExecutionContext.General(Path.Combine(root, "general")), Profile: AgentPermissionProfile.Auto,
            RegisteredAgent: new AgenteDaSessao("revisor", versao)));
        Assert.Contains("passa do máximo", direta.Error);
        var outroDriver = await sessions.StartAsync(new SessionStartRequest(123, AgentKind.Claude,
            JobExecutionContext.General(Path.Combine(root, "general")), Profile: AgentPermissionProfile.Plan,
            RegisteredAgent: new AgenteDaSessao("revisor", versao)));
        Assert.Contains("usa Codex", outroDriver.Error);
        // Review Codex: o processo usa o modelo e o esforço da versão, nunca outros com o registro da versão.
        var outroModelo = await sessions.StartAsync(new SessionStartRequest(123, AgentKind.Codex,
            JobExecutionContext.General(Path.Combine(root, "general")), Profile: AgentPermissionProfile.Plan,
            ModelSelection: new AgentModelSelection("fake-mini"), RegisteredAgent: new AgenteDaSessao("revisor", versao)));
        Assert.Contains("diferem da versão", outroModelo.Error);
        Assert.Single(drivers.Created);
    }

    [Fact]
    public async Task LimiteDeSessoesSimultaneasDaVersaoEhRespeitado()
    {
        await using var service = await StartAsync();
        await Send("/agente criar revisor driver=claude simultaneas=1");
        Assert.Contains("iniciada", await Send("/session start revisor"));
        Assert.Contains("limite da versão", await Send("/session start revisor"));
        await Send("/session close");
        Assert.Contains("iniciada", await Send("/session start revisor"));
    }

    [Fact]
    public async Task HandleInexistenteDesativadoOuComArrobaNaoCaiParaOutroAgente()
    {
        await using var service = await StartAsync();
        Assert.Contains("Agente fantasma não cadastrado", await Send("/session start fantasma"));
        Assert.Contains("Repositório @fantasma não cadastrado", await Send("/session start @fantasma"));
        await Send("/agente criar revisor driver=claude");
        // Precedência (AD-27 + AD-68): embutido e modo continuam vencendo; o handle é o que sobra, sem @.
        Assert.Contains("iniciada com Claude (General), modo plan", await Send("/session start plan"));
        Assert.Contains("iniciada com Codex", await Send("/session start codex"));
        Assert.Contains("agente revisor v1", await Send("/session start REVISOR"));
        await Send("/session close");
        await Send("/session close S000002");
        await Send("/session close S000001");
        var antes = drivers.Created.Count;
        Assert.Contains("iniciada", await Send("/session start revisor"));
        var desativado = await Send("/agente desativar revisor");
        Assert.Contains("Continuam até o fim: 1 sessão(ões) ativa(s) (S000004)", desativado);
        // A sessão ativa segue recebendo mensagens; nada novo começa com o agente desativado.
        api.Enqueue("continua");
        await Eventually(() => drivers.Created[antes].Calls.Contains("turn:continua"));
        Assert.Contains("está desativado", await Send("/session start revisor"));
        Assert.Contains("está desativado", await Send("/tarefa revisor revise algo"));
        Assert.Contains("ainda tem 1 sessão", await Send("/agente arquivar revisor"));
        Assert.Equal(antes + 1, drivers.Created.Count);
        Assert.Empty(jobs.GetVisible());
    }

    // Caso explícito da Issue: agente com escrita e perfil padrão auto; a tarefa é somente leitura e nada executa
    // enquanto a fila durável não está ligada.
    [Fact]
    public async Task TarefaEhSempreSomenteLeituraMesmoComAgenteDeEscrita()
    {
        var (registro, primeiro) = Repositorios();
        await using var service = await StartAsync(registro);
        await Send("/agente criar dev driver=claude model=opus modo=auto maximo=auto escrita=sim capacidades=implementar repos=@first\nImplemente.");
        var resposta = await Send("/tarefa dev @first ajuste o README");
        Assert.Contains("Tarefa para o agente dev v1 (@first) validada", resposta);
        Assert.Contains("somente leitura (plan)", resposta);
        Assert.Contains("ainda não está habilitada", resposta);
        Assert.Contains("nada foi executado", resposta);
        Assert.Contains("Uso: /tarefa", await Send("/tarefa dev"));
        Assert.Contains("Uso: /tarefa", await Send("/tarefa dev @first"));
        Assert.Contains("agente registrado", await Send("/tarefa claude faça algo"));
        Assert.Contains("não tem concessão para @second", await Send("/tarefa dev @second leia"));
        Assert.Contains("não cadastrado", await Send("/tarefa fantasma leia"));
        Assert.Empty(drivers.Created);
        Assert.Empty(jobs.GetVisible());
        Assert.Empty(runner.Models);

        // O que o executor da fila vai usar: a sessão da etapa sai em plan, com o aviso de leitura, e nem a troca de
        // modo a tira de plan.
        var versao = (await ResolverAsync("dev")).Versao!;
        var plano = MontagemDaTarefa.Montar("dev", versao, JobExecutionContext.Repository("@first", primeiro), "leia");
        var solicitacao = SolicitacaoDaTarefa.Para(plano, 123);
        Assert.Equal(AgentPermissionProfile.Plan, solicitacao.Profile);
        var aberta = await sessions!.StartAsync(solicitacao);
        Assert.True(aberta.Accepted, aberta.Error);
        var opcoes = drivers.Created.Single().StartOptions!;
        Assert.Equal((AgentPermissionProfile.Plan, primeiro), (opcoes.Profile, opcoes.WorkingDirectory));
        Assert.Contains(MontagemDaTarefa.AvisoDeLeitura, opcoes.ContextInstructions);
        Assert.Contains("Implemente.", opcoes.ContextInstructions);
        Assert.Equal("opus", opcoes.ModelSelection!.Model);
        foreach (var modo in new[] { AgentPermissionProfile.Manual, AgentPermissionProfile.Auto })
            Assert.False((await sessions.ChangeModeAsync(123, aberta.Session!.Id, modo)).Accepted);
        Assert.Throws<Dante.Domain.Execucoes.EscritaSemWorktreeException>(() =>
            SolicitacaoDaTarefa.Para(plano with { Perfil = AgentPermissionProfile.Auto }, 123));
    }

    [Fact]
    public async Task SemCadastroOsComandosNovosFicamIndisponiveisEOsLegadosNaoMudam()
    {
        await using var service = await StartAsync(comCadastro: false);
        Assert.Contains("indisponíveis", await Send("/agentes"));
        Assert.Contains("indisponíveis", await Send("/agente criar dev driver=claude"));
        Assert.Contains("indisponíveis", await Send("/tarefa dev leia"));
        Assert.StartsWith("Uso: /session start", await Send("/session start revisor"));
        Assert.Contains("Agente padrão alterado para Codex", await Send("/agent set codex"));
        await LegadosAsync(AgentKind.Codex);
    }

    [Fact]
    public async Task ComCadastroOsFluxosLegadosContinuamIdenticos()
    {
        await using var service = await StartAsync();
        await Send("/agente criar revisor driver=claude ferramentas=nenhuma\nInstruções que não podem vazar.");
        await LegadosAsync(AgentKind.Claude);
    }

    // Mensagem comum, /session start claude e /claude: sem instruções de agente, com todas as ferramentas, perfil e
    // modelo das preferências, como antes.
    private async Task LegadosAsync(AgentKind padrao)
    {
        api.Enqueue("olá");
        await Eventually(() => drivers.Created.Count == 1 && drivers.Created[0].Calls.Contains("turn:olá"));
        var comum = drivers.Created[0].StartOptions!;
        Assert.Null(comum.ContextInstructions);
        Assert.Equal(AgentPermissionProfile.Manual, comum.Profile);
        Assert.Equal(["dante_brain", "dante_planilhas"], comum.ToolServers!.Select(servidor => servidor.Name));
        Assert.Null(sessions!.GetActive(123)!.RegisteredAgent);
        Assert.Equal(padrao, sessions.GetActive(123)!.Agent);
        Assert.Contains("iniciada com Claude (General), modo plan", await Send("/session start claude plan"));
        Assert.Null(drivers.Created[1].StartOptions!.ContextInstructions);
        Assert.Equal(2, drivers.Created[1].StartOptions!.ToolServers!.Count);
        Assert.Contains("Claude iniciado. Job ID: J000001", await Send("/claude teste"));
        await Eventually(() => runner.Models.Count == 1);
    }

    private async Task<string> Send(string texto)
    {
        api.Enqueue(texto);
        return await api.NextMessageAsync();
    }

    private async Task<ResolucaoDeAgenteDto> ResolverAsync(string handle)
    {
        await using var provider = portas.Provider(catalogo);
        return await provider.GetRequiredService<ICadastroDeAgentesAppService>().ResolverAsync(handle);
    }

    private async Task<ServiceHandle> StartAsync(RepositoryRegistry? registro = null, bool comCadastro = true)
    {
        var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123,456" });
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        sessions = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, delivery,
            toolServers: new Ferramentas());
        TelegramAgentes? agentes = null;
        if (comCadastro)
        {
            var provider = portas.Provider(catalogo);
            descartaveis.Add(provider);
            agentes = new TelegramAgentes(provider.GetRequiredService<IServiceScopeFactory>());
        }
        var service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), runner, runner,
            jobs, NullLogger<TelegramPollingService>.Instance, registro,
            new GeneralWorkspace(Path.Combine(root, "general")), new AssistantSettingsStore(Path.Combine(root, "settings.json")),
            sessions, delivery, catalogo, agentes: agentes);
        await service.StartAsync(CancellationToken.None);
        return new ServiceHandle(service);
    }

    private (RepositoryRegistry Registro, string Primeiro) Repositorios()
    {
        var registro = new RepositoryRegistry(Path.Combine(root, "config", "repositories.json"));
        var primeiro = Git("first");
        registro.Add("@first", primeiro);
        registro.Add("@second", Git("second"));
        return (registro, primeiro);
    }

    private string Git(string nome)
    {
        var caminho = Path.Combine(root, nome);
        Directory.CreateDirectory(caminho);
        using var processo = Process.Start(new ProcessStartInfo("git")
        {
            WorkingDirectory = caminho, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, ArgumentList = { "init", "-q" }
        })!;
        processo.WaitForExit();
        Assert.Equal(0, processo.ExitCode);
        return caminho;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    public async ValueTask DisposeAsync()
    {
        if (sessions is not null) await sessions.DisposeAsync();
        foreach (var descartavel in descartaveis) await descartavel.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class ServiceHandle(TelegramPollingService service) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    // Os dois servidores do D.A.N.T.E. que uma sessão comum recebe hoje.
    private sealed class Ferramentas : IAgentToolServers
    {
        public Task<IReadOnlyList<AgentToolServer>> ForAsync(long ownerUserId, AgentKind agent,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentToolServer>>(
            [
                new("dante_brain", "dante", [], new Dictionary<string, string>(), []),
                new("dante_planilhas", "dante", [], new Dictionary<string, string>(), [])
            ]);
    }

    private sealed class RecordingRunner : ICodexRunner, IClaudeRunner
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string?> models = new();
        public IReadOnlyList<string?> Models => models.ToArray();

        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Attachment>? attachments = null, IReadOnlyList<string>? additionalDirectories = null)
        {
            models.Enqueue(model);
            return Task.FromResult(new AgentProcessResult(AgentProcessStatus.Succeeded, "ok", "", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
    }

    private sealed class BotApi : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
        private long nextId;

        public void Enqueue(string command, long senderId = 123) => updates.Writer.TryWrite(new TelegramUpdate(
            Interlocked.Increment(ref nextId), new TelegramMessage(new TelegramChat(senderId), command,
                new TelegramUser(senderId))));

        public async Task<string> NextMessageAsync() =>
            await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken) =>
            [await updates.Reader.ReadAsync(cancellationToken)];

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            messages.Writer.TryWrite(text);
            return Task.CompletedTask;
        }

        public Task SendChatActionAsync(long chatId, string action, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
