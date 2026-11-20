using Dante.Application.Execucoes;
using Dante.Worker.Artifacts;
using Dante.Worker.Brain;
using Dante.Worker.Execucoes;
using Dante.Worker.Attachments;
using Dante.Worker.Jobs;
using Dante.Worker.Planilhas;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Configuration;

namespace Dante.Worker;

// Composição do legado que ainda vive no Worker; adapters já migrados são compostos por AddInfrastructure (#167).
internal static class WorkerComposition
{
    public static IServiceCollection AddWorker(this IServiceCollection services, IConfiguration configuration)
    {
        // Ledger de execuções (AD-66): exige banco e a chave própria; a connection string sozinha não o liga. Sem o
        // espelho, JobRegistry e SessionRegistry seguem só em memória, como antes.
        if (OrquestracaoHabilitada(configuration)) services.AddLedgerDeExecucoes().AddFilaDuravel(configuration);
        // Cadastro de agentes (AD-68): mesma condição do ledger; sem ele, /agentes, /agente e /tarefa respondem
        // indisponível e /session start segue só com claude|codex.
        if (OrquestracaoHabilitada(configuration)) services.AddSingleton<TelegramAgentes>();
        // Grupos de agentes (AD-69, #308): mesma condição; sem ela, /grupos e /grupo respondem indisponível.
        if (OrquestracaoHabilitada(configuration)) services.AddSingleton<TelegramGrupos>();
        // Acompanhamento e controle de execuções (#305): mesma condição; sem ele, /execucoes, /execucao e /aguardando
        // respondem que não está habilitado, e /cancel segue só com os jobs em memória.
        if (OrquestracaoHabilitada(configuration)) services.AddSingleton<TelegramExecucoes>();
        services.AddSingleton<JobRegistry>();
        services.AddSingleton<RegistroDeOperacoesBrain>();
        services.AddSingleton(provider => new TelegramBrain(provider.GetRequiredService<IServiceScopeFactory>(), configuration, provider.GetRequiredService<RegistroDeOperacoesBrain>()));
        services.AddSingleton<IMediaTools>(_ => new MediaTools());
        services.AddSingleton<MediaPreparer>();
        services.AddSingleton<IAgentSessionDriverFactory, AgentSessionDriverFactory>();
        services.AddSingleton<TelegramDeliveryService>();
        services.AddSingleton<IShowcaseRenderer, ShowcaseRenderer>();
        // Showcase turns (#98) are intercepted before the delivery; everything else passes through to it.
        services.AddSingleton<TelegramShowcase>(provider => new TelegramShowcase(
            provider.GetRequiredService<TelegramDeliveryService>(), provider.GetRequiredService<TelegramDeliveryService>(),
            provider.GetRequiredService<IShowcaseRenderer>(), provider.GetRequiredService<AttachmentStore>(),
            provider.GetRequiredService<ILogger<TelegramShowcase>>()));
        // Brain metrics (#148) observe the session events on their way to the showcase and the delivery.
        services.AddSingleton<IAgentSessionEventSink>(provider => new MetricasDeSessaoDoBrain(
            provider.GetRequiredService<TelegramShowcase>(), provider.GetRequiredService<TelegramBrain>()));
        services.AddSingleton<OperacoesMcpDoBrain>();
        services.AddSingleton<IProvedorDeFerramentas, FerramentasDePlanilhaParaAgentes>();
        services.AddSingleton<IProvedorDeFerramentas, FerramentasDoBrainParaAgentes>();
        // dante_coordenacao (#308): só a sessão de revisão confinada de um roteiro o recebe.
        services.AddSingleton<IProvedorDeFerramentas, Dante.Worker.Coordenacao.FerramentasDeCoordenacao>();
        services.AddSingleton<IAgentToolServers, CompositorDeFerramentas>();
        services.AddSingleton<TelegramPlanilhas>();
        services.AddSingleton<SessionRegistry>();
        services.AddSingleton(_ => new AttachmentStore());
        services.AddSingleton(_ => new ArtifactStore());
        services.AddSingleton<PendingAttachments>(provider =>
            new PendingAttachments(provider.GetRequiredService<AttachmentStore>()));
        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.SectionName));
        services.AddSingleton<TelegramUserAuthorizer>();
        // Only the Telegram adapter consumes this client. Same transport and 100s timeout as `new HttpClient()`, but
        // connecting with Happy Eyeballs (IPv6 that ignores the SYN no longer blocks IPv4) and connection diagnostics.
        services.AddSingleton(provider => new HttpClient(DiagnosticoHttpDoTelegram.CriarHandler(
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DiagnosticoHttpDoTelegram)))));
        services.AddSingleton<ITelegramBotApi, TelegramBotApi>();
        services.AddHostedService<Worker>();
        services.AddHostedService<TelegramPollingService>();
        return services;
    }

    // Antes dos demais hosted services: o encerramento do ledger para por último (ordem inversa).
    internal static IServiceCollection AddLedgerDeExecucoes(this IServiceCollection services)
    {
        services.AddSingleton<EspelhoDoLedger>();
        services.AddHostedService<EncerramentoDoLedger>();
        return services;
    }

    // Fila durável e recuperação (AD-67), junto com o ledger. O processador é registrado logo depois do encerramento
    // do ledger: inicia antes do polling do Telegram (a recuperação vem antes de qualquer reivindicação) e para depois
    // dele, com o provider ainda vivo para gravar o fim das etapas em andamento.
    internal static IServiceCollection AddFilaDuravel(this IServiceCollection services, IConfiguration configuration)
    {
        var opcoes = OpcoesDaFila(configuration);
        services.AddSingleton(opcoes);
        services.AddSingleton(IdentidadeDoProcessador.DoProcessoAtual());
        services.AddSingleton<SinalDaFila>();
        services.AddSingleton<MetricasDaFila>();
        services.AddSingleton<ExecutorDeJobsDaFila>();
        services.AddSingleton<IExecutorDeEtapa>(provider => provider.GetRequiredService<ExecutorDeJobsDaFila>());
        // /tarefa (AD-68): o executor de TipoDeExecucao.Tarefa; a fila é o único caminho que executa uma tarefa.
        // A retomada depois das delegações (#309) abre a própria sessão, resolvida só quando roda.
        services.AddSingleton(provider => ActivatorUtilities.CreateInstance<ExecutorDeTarefasDaFila>(provider,
            (Func<SessionRegistry>)provider.GetRequiredService<SessionRegistry>));
        services.AddSingleton<IExecutorDeEtapa>(provider => provider.GetRequiredService<ExecutorDeTarefasDaFila>());
        // Roteiro de grupo (#308, AD-69): o executor de TipoDeExecucao.Roteiro; um passo por trabalho.
        services.AddSingleton(provider => ActivatorUtilities.CreateInstance<ExecutorDeRoteirosDaFila>(provider,
            (Func<SessionRegistry>)provider.GetRequiredService<SessionRegistry>));
        services.AddSingleton<IExecutorDeEtapa>(provider => provider.GetRequiredService<ExecutorDeRoteirosDaFila>());
        // Delegação rastreada (#309, PR 2): o executor de TipoDeExecucao.Delegada, uma subordinada por trabalho, só
        // leitura. Nada cria delegações em produção até o PR 3 (dante_coordenacao).
        services.AddSingleton(provider => ActivatorUtilities.CreateInstance<ExecutorDeDelegacoesDaFila>(provider,
            (Func<SessionRegistry>)provider.GetRequiredService<SessionRegistry>));
        services.AddSingleton<IExecutorDeEtapa>(provider => provider.GetRequiredService<ExecutorDeDelegacoesDaFila>());
        // Coordenação por papel (#310, AD-75, PR 2): o executor de TipoDeExecucao.Coordenada (planejamento e avanço).
        // Nada cria uma coordenação em produção até o PR 3 (interface).
        services.AddSingleton(provider => ActivatorUtilities.CreateInstance<ExecutorDeCoordenacoesDaFila>(provider,
            (Func<SessionRegistry>)provider.GetRequiredService<SessionRegistry>));
        services.AddSingleton<IExecutorDeEtapa>(provider => provider.GetRequiredService<ExecutorDeCoordenacoesDaFila>());
        // Worktrees de execução de escrita (#307, AD-71). O limite global fica em 1 até a ativação autorizada da AD-63:
        // a composição recusa qualquer valor maior (OpcoesDeWorktrees.Validar sem paralelismo autorizado).
        // O gerenciador Git vem da Infrastructure e lê estas opções.
        services.AddSingleton(OpcoesDeWorktrees(configuration));
        services.AddSingleton<ProcessadorDaFila>();
        services.AddHostedService(provider => provider.GetRequiredService<ProcessadorDaFila>());
        return services;
    }

    // Orquestracao:Worktrees:{Raiz, LimiteGlobal, LimitePorRepositorio}; ausentes ficam no padrão (~/.dante/worktrees,
    // 1 e 1). Limite global acima de 1 impede a inicialização: elevar exige alteração e autorização próprias (AD-63).
    internal static OpcoesDeWorktrees OpcoesDeWorktrees(IConfiguration configuration)
    {
        var secao = configuration.GetSection("Orquestracao:Worktrees");
        var padrao = new OpcoesDeWorktrees();
        var opcoes = padrao with
        {
            Raiz = secao["Raiz"] is { Length: > 0 } raiz ? raiz : padrao.Raiz,
            LimiteGlobal = Inteiro(secao["LimiteGlobal"]) ?? padrao.LimiteGlobal,
            LimitePorRepositorio = Inteiro(secao["LimitePorRepositorio"]) ?? padrao.LimitePorRepositorio
        };
        opcoes.Validar(paralelismoAutorizado: false);
        return opcoes;
    }

    private static int? Inteiro(string? valor) => string.IsNullOrWhiteSpace(valor) ? null
        : int.Parse(valor, System.Globalization.CultureInfo.InvariantCulture);

    // Orquestracao:Fila:{LeaseSegundos, HeartbeatSegundos, PollingSegundos, Slots}; ausentes ficam no padrão
    // (60/20/5 e 1 slot). Valores inválidos impedem a inicialização em vez de virar padrão silencioso.
    internal static OpcoesDaFila OpcoesDaFila(IConfiguration configuration)
    {
        var secao = configuration.GetSection("Orquestracao:Fila");
        var padrao = new OpcoesDaFila();
        var opcoes = padrao with
        {
            Lease = Segundos(secao["LeaseSegundos"]) ?? padrao.Lease,
            Heartbeat = Segundos(secao["HeartbeatSegundos"]) ?? padrao.Heartbeat,
            Polling = Segundos(secao["PollingSegundos"]) ?? padrao.Polling,
            Slots = secao["Slots"] is { Length: > 0 } slots ? int.Parse(slots, System.Globalization.CultureInfo.InvariantCulture) : padrao.Slots
        };
        opcoes.Validar();
        return opcoes;
    }

    private static TimeSpan? Segundos(string? valor) => string.IsNullOrWhiteSpace(valor) ? null
        : TimeSpan.FromSeconds(double.Parse(valor, System.Globalization.CultureInfo.InvariantCulture));

    internal static bool OrquestracaoHabilitada(IConfiguration configuration) =>
        bool.TryParse(configuration["Orquestracao:Habilitada"], out var habilitada) && habilitada &&
        !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Dante"));
}
