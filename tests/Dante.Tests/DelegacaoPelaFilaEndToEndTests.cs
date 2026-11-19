using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Application.Agentes;
using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Worker.Coordenacao;
using Dante.Worker.Execucoes;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using static Dante.Tests.DelegacoesDominioTests;
using static Dante.Tests.FilaDuravelPostgreSqlTests;

namespace Dante.Tests;

// #309 (AD-70), PR 2 e PR 3, de ponta a ponta contra PostgreSQL descartável: o ProcessadorDaFila real com o
// ExecutorDeDelegacoesDaFila e o ExecutorDeTarefasDaFila (retomada) reais, sessões reservadas com drivers falsos, o
// ledger espelhado e o dante_coordenacao real (FerramentasDeCoordenacao, pipe e token da sessão). O teste faz o papel do
// processo MCP de cada agente. A origem é uma Tarefa cuja primeira etapa abre a sessão com o escopo da origem e pede a
// delegação pela ferramenta consultar_agente. Fluxo: pai solicita pelo MCP → delegação persistida → filha na fila →
// destinatário executa numa sessão própria e entrega pelo entregar_resultado → resultado gravado → pai retomado numa
// sessão nova → resposta ao dono. O mesmo fluxo é repetido parando o Worker no meio da filha e no meio da retomada:
// nada é perdido nem gravado duas vezes.
[Collection("FilaDuravel")]
public sealed class DelegacaoPelaFilaEndToEndTests
{
    private const string Json = """{"resposta":"Sim: ix_estado cobre a consulta.","confianca":"alta","evidencias":[{"tipo":"arquivo","referencia":"README.md:1"}]}""";

    [PostgreSqlFact]
    public async Task PaiDelegaFilhaRespondePelaFilaEPaiERetomadoComOResultado()
    {
        await using var a = await Ambiente.CriarAsync();
        var codigo = await a.OrigemAsync();
        a.Iniciar();

        // A origem pede pela ferramenta, com o escopo da sessão: consultar mostra a delegação aceita, repetir devolve a
        // mesma e um destinatário inexistente é recusado sem gravar nada.
        var origem = await a.DriverAsync(c => c.StartsWith("turn:Origem", StringComparison.Ordinal));
        var servidorDaOrigem = Assert.Single(origem.StartOptions!.ToolServers!);
        Assert.Equal(["--perfil", "origem"], servidorDaOrigem.Arguments.SkipLast(2).TakeLast(2));
        var pedida = JsonNode.Parse(await a.SolicitarAsync(origem))!;
        Assert.True((bool)pedida["nova"]!);
        Assert.False((bool)JsonNode.Parse(await a.SolicitarAsync(origem))!["nova"]!);
        Assert.Contains("estranho não existe", await a.SolicitarAsync(origem, "estranho"));
        var consulta = JsonNode.Parse(await Ambiente.ChamarAsync(origem, "estado_da_execucao", new { }))!;
        Assert.Equal(((string?)pedida["delegacao"], "Aceita"),
            ((string?)consulta["delegacoes"]![0]!["delegacao"], (string?)consulta["delegacoes"]![0]!["estado"]));
        Responder(origem, "Delegado ao revisor.");

        // A filha roda numa sessão reservada do destinatário: plan, no contexto congelado da origem, sem o ambiente do
        // Worker e só com o dante_coordenacao no perfil delegacao; o pedido entra como dado não confiável, com o schema.
        var filha = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal));
        var opcoes = filha.StartOptions!;
        Assert.Equal((AgentPermissionProfile.Plan, a.Repositorio), (opcoes.Profile, opcoes.WorkingDirectory));
        var servidorDaFilha = Assert.Single(opcoes.ToolServers!);
        Assert.Equal(["--perfil", "delegacao"], servidorDaFilha.Arguments.SkipLast(2).TakeLast(2));
        Assert.Equal(["entregar_resultado"], servidorDaFilha.PreAuthorizedTools);
        Assert.DoesNotContain(opcoes.EnvironmentVariables ?? new Dictionary<string, string>(), v => v.Key == "DANTE_SEGREDO_DE_TESTE");
        var prompt = filha.TurnInputs[0].Text;
        Assert.Contains("<<<DADO_NAO_CONFIAVEL", prompt);
        Assert.Contains("consulta.v1", prompt);
        Assert.Contains("\"confianca\"", prompt);
        Assert.Equal(EstadoDaExecucao.AguardandoDelegacoes, await a.EstadoDaOrigemAsync());

        // Texto livre (mesmo um JSON na mensagem) não é resultado: um lembrete, na mesma sessão.
        Responder(filha, $"Aqui está:\n```json\n{Json}\n```");
        await Eventually(() => Task.FromResult(filha.TurnInputs.Count == 2));
        Assert.Contains("O resultado não foi entregue", filha.TurnInputs[1].Text);
        Assert.Equal(EstadoDaDelegacao.EmAndamento, (await a.DelegacaoAsync()).Estado);
        // Pela ferramenta: evidência inexistente é recusada (o agente corrige), a válida é gravada na hora e repetir é
        // idempotente.
        Assert.Contains("não existe no repositório",
            await a.EntregarAsync(filha, Json.Replace("README.md:1", "src/nao-existe.cs")));
        Assert.True((bool)JsonNode.Parse(await a.EntregarAsync(filha, Json))!["registrado"]!);
        Assert.Equal(EstadoDaDelegacao.Respondida, (await a.DelegacaoAsync()).Estado);
        Assert.True((bool)JsonNode.Parse(await a.EntregarAsync(filha, Json))!["ja_registrado"]!);
        Responder(filha, "Entregue.");

        var retomada = await a.DriverAsync(c => c.StartsWith("turn:Retomada da tarefa", StringComparison.Ordinal));
        Assert.Equal(AgentPermissionProfile.Plan, retomada.StartOptions!.Profile);
        Assert.Contains("ix_estado cobre a consulta", retomada.TurnInputs[0].Text);
        Assert.Contains("Respondida", retomada.TurnInputs[0].Text);
        Responder(retomada, "Resposta final");
        Assert.StartsWith($"Tarefa {codigo}, retomada com 1 delegação(ões):\nResposta final", await a.Api.ProximaAsync());

        await Eventually(async () => await a.EstadoDaOrigemAsync() == EstadoDaExecucao.Concluida);
        var delegacao = await a.DelegacaoAsync();
        Assert.Equal(EstadoDaDelegacao.Respondida, delegacao.Estado);
        Assert.Contains("README.md:1", delegacao.Resultado);
        Assert.NotNull(delegacao.IdSessaoDoResultado);
        Assert.Equal(3, a.Drivers.Created.Count);
        // O resultado consultado pela origem é o estruturado gravado, não a mensagem.
        Assert.Contains("ix_estado cobre a consulta", delegacao.Resultado);
        // Proveniência: o resultado veio da sessão vinculada à etapa da subordinada, no ledger.
        Assert.Equal(1L, await a.Escalar($"SELECT count(*) FROM orquestracao.etapas_da_execucao WHERE id_execucao = '{delegacao.IdExecucaoSubordinada}' " +
            $"AND id_sessao_de_agente = '{delegacao.IdSessaoDoResultado}'"));
        Assert.Equal(3L, await a.Escalar($"SELECT count(*) FROM orquestracao.eventos WHERE tipo = {(int)TipoDeEventoDeOrquestracao.DelegacaoProgrediu}"));
    }

    [PostgreSqlFact]
    public async Task WorkerParadoNoMeioDaFilhaENoMeioDaRetomadaNaoPerdeNemRepeteOQueFoiGravado()
    {
        await using var a = await Ambiente.CriarAsync();
        var codigo = await a.OrigemAsync();

        // 1. Parado com a filha em execução: a etapa estava em andamento, e o próximo Worker a retoma (leitura).
        a.Iniciar();
        await a.OrigemDelegaAsync();
        var primeira = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal));
        await a.PararAsync();
        // A sessão parada perdeu o canal junto com a posse: o processo MCP dela não entrega mais nada.
        await Assert.ThrowsAnyAsync<Exception>(() => a.EntregarAsync(primeira, Json));
        Assert.Equal(EstadoDaDelegacao.EmAndamento, (await a.DelegacaoAsync()).Estado);
        Assert.Equal(EstadoDaExecucao.AguardandoDelegacoes, await a.EstadoDaOrigemAsync());

        a.Iniciar();
        var segunda = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal), depoisDe: primeira);
        Assert.True((bool)JsonNode.Parse(await a.EntregarAsync(segunda, Json))!["registrado"]!);
        Responder(segunda, "Entregue.");

        // 2. Parado na retomada da origem: a filha não roda de novo; a retomada é refeita a partir do ledger.
        var retomada = await a.DriverAsync(c => c.StartsWith("turn:Retomada da tarefa", StringComparison.Ordinal));
        await a.PararAsync();
        var gravada = await a.DelegacaoAsync();
        Assert.Equal(EstadoDaDelegacao.Respondida, gravada.Estado);

        a.Iniciar();
        var outraRetomada = await a.DriverAsync(c => c.StartsWith("turn:Retomada da tarefa", StringComparison.Ordinal), depoisDe: retomada);
        Responder(outraRetomada, "Resposta final");
        // O dono sabe da interrupção da retomada (aviso do executor da origem) e recebe só uma resposta final.
        Assert.Equal($"Tarefa {codigo} foi interrompida e será retomada automaticamente.", await a.Api.ProximaAsync());
        Assert.StartsWith($"Tarefa {codigo}, retomada com 1 delegação(ões):\nResposta final", await a.Api.ProximaAsync());
        await Eventually(async () => await a.EstadoDaOrigemAsync() == EstadoDaExecucao.Concluida);

        // A filha rodou duas vezes (a primeira foi parada sem resultado) e o resultado é o único gravado.
        Assert.Equal(2, a.Drivers.Created.Count(d => d.Calls.Any(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal))));
        Assert.Equal(2, a.Drivers.Created.Count(d => d.Calls.Any(c => c.StartsWith("turn:Retomada", StringComparison.Ordinal))));
        var final = await a.DelegacaoAsync();
        Assert.Equal((gravada.HashDoResultado, gravada.IdSessaoDoResultado, gravada.EncerradaEm),
            (final.HashDoResultado, final.IdSessaoDoResultado, final.EncerradaEm));
        Assert.Equal(1L, await a.Escalar("SELECT count(*) FROM orquestracao.delegacoes"));
        Assert.Equal(1L, await a.Escalar($"SELECT count(*) FROM orquestracao.execucoes WHERE tipo = {(int)TipoDeExecucao.Delegada}"));
        Assert.Equal(2L, await a.Escalar("SELECT count(*) FROM orquestracao.etapas_da_execucao e JOIN orquestracao.execucoes x " +
            $"ON x.id = e.id_execucao WHERE x.tipo = {(int)TipoDeExecucao.Tarefa}"));
    }

    [PostgreSqlFact]
    public async Task DestinatarioDesativadoAntesDeIniciarFalhaADelegacaoSemProcessoEOPaiERetomado()
    {
        await using var a = await Ambiente.CriarAsync();
        await a.OrigemAsync(desativarDestinatarioAntesDaFilha: true);
        a.Iniciar();
        await a.OrigemDelegaAsync();
        var retomada = await a.DriverAsync(c => c.StartsWith("turn:Retomada da tarefa", StringComparison.Ordinal));
        Assert.Contains("Falhou", retomada.TurnInputs[0].Text);
        Assert.Contains("não está mais ativo", retomada.TurnInputs[0].Text);
        Responder(retomada, "Sem a consulta.");
        await a.Api.ProximaAsync();
        Assert.Equal(EstadoDaDelegacao.Falhou, (await a.DelegacaoAsync()).Estado);
        // Nenhuma sessão do destinatário existiu (só a da origem e a da retomada).
        Assert.Equal(2, a.Drivers.Created.Count);
        Assert.DoesNotContain(a.Drivers.Created, d => d.Calls.Any(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal)));
    }

    [PostgreSqlFact]
    public async Task DuracaoMaximaDoExecutorEncerraASessaoEExpiraADelegacao()
    {
        await using var a = await Ambiente.CriarAsync();
        await a.OrigemAsync();
        // O relógio do executor já está além da duração gravada no início: o temporizador local encerra a sessão.
        a.Iniciar(new RelogioManual(DateTimeOffset.UtcNow.AddMinutes(61)));
        await a.OrigemDelegaAsync();
        var retomada = await a.DriverAsync(c => c.StartsWith("turn:Retomada da tarefa", StringComparison.Ordinal));
        Assert.Contains("Expirada", retomada.TurnInputs[0].Text);
        Assert.Contains("Duração máxima excedida", retomada.TurnInputs[0].Text);
        Responder(retomada, "Sem a consulta.");
        await a.Api.ProximaAsync();
        var delegacao = await a.DelegacaoAsync();
        Assert.Equal((EstadoDaDelegacao.Expirada, (string?)null), (delegacao.Estado, delegacao.Resultado));
        Assert.Equal(EstadoDaExecucao.Cancelada, (EstadoDaExecucao)await a.Escalar(
            $"SELECT estado FROM orquestracao.execucoes WHERE id = '{delegacao.IdExecucaoSubordinada}'"));
        // Nenhum turno chegou ao destinatário.
        Assert.DoesNotContain(a.Drivers.Created, d => d.Calls.Any(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal)));
    }

    private static void Responder(FakeSessionDriver driver, string mensagem)
    {
        driver.Emit(new MessageCompletedEvent("m", mensagem));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
    }

    // A primeira etapa da origem: a sessão reservada da versão do pesquisador, em plan, com o escopo da origem (a posse
    // desta etapa) e por isso com o dante_coordenacao no perfil origem; o teste, como o agente, pede a delegação pela
    // ferramenta e termina o turno. As etapas seguintes são a retomada, executada pelo ExecutorDeTarefasDaFila real.
    private sealed class OrigemDeTeste(IServiceScopeFactory escopos, Func<SessionRegistry> sessoes, string repositorio,
        ExecutorDeTarefasDaFila real, Func<Task>? antesDeConcluir = null) : IExecutorDeEtapa
    {
        public TipoDeExecucao Tipo => TipoDeExecucao.Tarefa;
        public bool PodeExecutar(AlvoDoTrabalhoDto alvo) => true;

        public async Task ExecutarAsync(AlvoDoTrabalhoDto alvo, ExecucaoSobPosse controle, CancellationToken cancellationToken)
        {
            if (alvo.NumeroDaEtapa > 1)
            {
                await real.ExecutarAsync(alvo, controle, cancellationToken);
                return;
            }
            await controle.IniciarAsync(cancellationToken);
            VersaoDoAgenteDto versao;
            await using (var escopo = escopos.CreateAsyncScope())
                versao = (await escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>()
                    .ResolverAsync("pesquisador", cancellationToken)).Versao!;
            var registro = sessoes();
            var observador = new ObservadorDaTarefa(registro, alvo.IdDoUsuarioNoCanal);
            var iniciada = await registro.StartAsync(new SessionStartRequest(alvo.IdDoUsuarioNoCanal, AgentKind.Codex,
                JobExecutionContext.Repository("@dante", repositorio), Profile: AgentPermissionProfile.Plan,
                RegisteredAgent: new AgenteDaSessao("pesquisador", versao)
                {
                    TetoDaExecucao = AgentPermissionProfile.Plan, Coordenacao = new CoordenacaoDaOrigem(controle)
                }, Observer: observador), cancellationToken);
            Assert.True(iniciada.Accepted, iniciada.Error);
            try
            {
                var numero = await registro.LedgerNumberWrittenAsync(alvo.IdDoUsuarioNoCanal, iniciada.Session!.Id,
                    cancellationToken);
                await controle.VincularSessaoAsync(numero!.Value, cancellationToken);
                await registro.SubmitReservedAsync(alvo.IdDoUsuarioNoCanal, iniciada.Session.Id,
                    new AgentInput("Origem: investigar o índice", []), cancellationToken);
                await observador.Turno.WaitAsync(cancellationToken);
            }
            finally
            {
                await registro.CloseReservedAsync(alvo.IdDoUsuarioNoCanal, iniciada.Session!.Id, CancellationToken.None);
            }
            if (antesDeConcluir is not null) await antesDeConcluir();
            await controle.ConcluirAsync(new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida, 0), cancellationToken);
        }

        public Task AoInterromperAsync(ExecucaoInterrompidaDto interrupcao, CancellationToken cancellationToken) =>
            real.AoInterromperAsync(interrupcao, cancellationToken);
    }

    private sealed class Ambiente : IAsyncDisposable
    {
        private readonly string raiz = Path.Combine(Path.GetTempPath(), "dante-delegacao-" + Guid.NewGuid().ToString("N"));
        private Banco banco = null!;
        private ProcessadorDaFila? processador;
        private Func<Task>? antesDeConcluir;
        private FerramentasDeCoordenacao coordenacao = null!;
        public ServiceProvider Provider { get; private set; } = null!;
        public IServiceScopeFactory Escopos => Provider.GetRequiredService<IServiceScopeFactory>();
        public EspelhoDoLedger Espelho { get; private set; } = null!;
        public SessionRegistry Sessoes { get; private set; } = null!;
        public FakeSessionDriverFactory Drivers { get; } = new();
        public TarefaPelaFilaEndToEndTests.BotDeTeste Api { get; } = new();
        public string Repositorio => Path.Combine(raiz, "dante");
        public Guid IdOrigem { get; private set; }

        public static async Task<Ambiente> CriarAsync()
        {
            var a = new Ambiente { banco = await FilaAsync() };
            Directory.CreateDirectory(a.Repositorio);
            await File.WriteAllTextAsync(Path.Combine(a.Repositorio, "README.md"), "# dante\n");
            a.Provider = Provider(a.banco.ConnectionString, configurar: services =>
                services.Replace(ServiceDescriptor.Singleton<IAgentModelCatalog>(new CatalogoDeModelosFixo())));
            a.Espelho = new EspelhoDoLedger(a.Escopos, NullLogger<EspelhoDoLedger>.Instance);
            a.coordenacao = new FerramentasDeCoordenacao(a.Escopos, NullLogger<FerramentasDeCoordenacao>.Instance);
            a.Sessoes = new SessionRegistry(a.Drivers, NullLogger<SessionRegistry>.Instance,
                toolServers: new CompositorDeFerramentas([a.coordenacao]), ledger: a.Espelho);
            return a;
        }

        // A origem gravada e enfileirada como uma /tarefa de leitura de pesquisador em @dante (o diretório deste teste).
        public async Task<string> OrigemAsync(bool desativarDestinatarioAntesDaFilha = false)
        {
            await using var escopo = Provider.CreateAsyncScope();
            var s = escopo.ServiceProvider;
            var agora = DateTimeOffset.UtcNow;
            var pesquisador = new Agente("pesquisador", null, null, 1, agora);
            var versao = pesquisador.NovaVersao(ConfiguracaoDoSolicitante(), 1, agora);
            var revisor = new Agente("revisor", null, null, 1, agora);
            var versaoDoRevisor = revisor.NovaVersao(ConfiguracaoDoDestinatario(), 1, agora);
            await s.GetRequiredService<IAgenteRepository>().AdicionarAsync(pesquisador);
            await s.GetRequiredService<IAgenteRepository>().AdicionarAsync(revisor);
            await s.GetRequiredService<IVersaoDoAgenteRepository>().AdicionarAsync(versao);
            await s.GetRequiredService<IVersaoDoAgenteRepository>().AdicionarAsync(versaoDoRevisor);
            var numero = await s.GetRequiredService<IExecucaoRepository>().ReservarNumeroAsync();
            var contexto = JsonSerializer.Serialize(new { modo = "Repository", alias = "@dante", diretorio = Repositorio,
                vinculados = Array.Empty<object>() });
            var origem = new Execucao(numero, TipoDeExecucao.Tarefa, "telegram", 123, contexto, "Investigar o índice", agora,
                versao.Id);
            var etapa = new EtapaDaExecucao(origem, 1, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica, agora,
                AgentKind.Codex);
            await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(origem);
            await s.GetRequiredService<IEtapaDaExecucaoRepository>().AdicionarAsync(etapa);
            var fila = s.GetRequiredService<IFilaDeTrabalho>();
            await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(origem, etapa, $"tarefa:{origem.Id:N}", fila.Agora));
            await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
            IdOrigem = origem.Id;
            if (desativarDestinatarioAntesDaFilha)
                antesDeConcluir = async () =>
                {
                    await using var outro = Provider.CreateAsyncScope();
                    await outro.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>().DesativarAsync("revisor");
                };
            return origem.Codigo;
        }

        // Um Worker novo (boot novo, mesmo host): recuperação na inicialização e então a fila.
        public void Iniciar(TimeProvider? relogioDoExecutor = null)
        {
            var identidade = Processador();
            var real = new ExecutorDeTarefasDaFila(Escopos, new JobRegistry(Espelho), Espelho, new SinalDaFila(),
                NullLogger<ExecutorDeTarefasDaFila>.Instance, Api, identidade, registroDeSessoes: () => Sessoes);
            IExecutorDeEtapa[] executores =
            [
                new ExecutorDeDelegacoesDaFila(Escopos, () => Sessoes, NullLogger<ExecutorDeDelegacoesDaFila>.Instance,
                    relogioDoExecutor),
                new OrigemDeTeste(Escopos, () => Sessoes, Repositorio, real, antesDeConcluir)
            ];
            processador = new ProcessadorDaFila(Escopos, identidade, Rapidas, new SinalDaFila(), executores, new MetricasDaFila(),
                NullLogger<ProcessadorDaFila>.Instance);
            processador.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public async Task PararAsync()
        {
            await processador!.StopAsync(CancellationToken.None);
            processador.Dispose();
            processador = null;
        }

        public async Task<FakeSessionDriver> DriverAsync(Func<string, bool> chamada, FakeSessionDriver? depoisDe = null)
        {
            FakeSessionDriver? achado = null;
            await Eventually(() => Task.FromResult((achado = Drivers.Created.FirstOrDefault(d => d != depoisDe &&
                d.Calls.Any(chamada) && (depoisDe is null ||
                    Drivers.Created.ToList().IndexOf(d) > Drivers.Created.ToList().IndexOf(depoisDe)))) is not null),
                TimeSpan.FromSeconds(30));
            return achado!;
        }

        public async Task<EstadoDaExecucao> EstadoDaOrigemAsync() =>
            (EstadoDaExecucao)await Escalar($"SELECT estado FROM orquestracao.execucoes WHERE id = '{IdOrigem}'");

        public async Task<DelegacaoDto> DelegacaoAsync()
        {
            await using var escopo = Provider.CreateAsyncScope();
            return Assert.Single(await escopo.ServiceProvider.GetRequiredService<IDelegacoesAppService>()
                .ListarDaRaizAsync(new ProprietarioDaExecucao("telegram", 123), IdOrigem));
        }

        public Task<long> Escalar(string sql) => FilaDuravelPostgreSqlTests.Escalar(banco, sql);

        // O turno da origem: o agente pede ao revisor pela ferramenta e termina.
        public async Task OrigemDelegaAsync()
        {
            var origem = await DriverAsync(c => c.StartsWith("turn:Origem", StringComparison.Ordinal));
            Assert.True((bool)JsonNode.Parse(await SolicitarAsync(origem))!["nova"]!);
            Responder(origem, "Delegado ao revisor.");
        }

        public Task<string> SolicitarAsync(FakeSessionDriver origem, string destinatario = "revisor") =>
            ChamarAsync(origem, "consultar_agente", new
            {
                destinatario, contrato = "consulta.v1", objetivo = "Conferir o índice por estado",
                entrada = JsonNode.Parse(Pergunta), criterios = "Citar o arquivo."
            });

        public Task<string> EntregarAsync(FakeSessionDriver filha, string resultado) =>
            ChamarAsync(filha, "entregar_resultado", new { resultado = JsonNode.Parse(resultado) });

        // Faz o que o processo MCP do dante_coordenacao da sessão faria: conecta uma vez no pipe dela, apresenta o
        // token e chama pela mesma conexão.
        private static readonly ConditionalWeakTable<FakeSessionDriver, NamedPipeClientStream> Canais = [];

        public static async Task<string> ChamarAsync(FakeSessionDriver driver, string nome, object argumentos)
        {
            var servidor = Assert.Single(driver.StartOptions!.ToolServers!, s => s.Name == ServidorMcpDeCoordenacao.Nome);
            if (!Canais.TryGetValue(driver, out var pipe))
            {
                pipe = new NamedPipeClientStream(".", servidor.Arguments[^1], PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(2_000);
                await EscreverAsync(pipe, new { token = servidor.Environment[ServidorMcpDeCoordenacao.VariavelDoToken] });
                Canais.Add(driver, pipe);
            }
            await EscreverAsync(pipe, new { nome, argumentos });
            var bytes = new List<byte>();
            var buffer = new byte[1];
            using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                if (await pipe.ReadAsync(buffer, prazo.Token) != 1) throw new IOException("canal fechado");
                if (buffer[0] == '\n') break;
                bytes.Add(buffer[0]);
            }
            return (string)JsonNode.Parse(Encoding.UTF8.GetString(bytes.ToArray()))!["content"]![0]!["text"]!;
        }

        private static async Task EscreverAsync(Stream pipe, object mensagem)
        {
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(mensagem) + "\n"));
            await pipe.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (processador is not null) await PararAsync();
            await Sessoes.DisposeAsync();
            coordenacao.Dispose();
            await Espelho.DisposeAsync();
            await Provider.DisposeAsync();
            await banco.DisposeAsync();
            if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        }
    }
}
