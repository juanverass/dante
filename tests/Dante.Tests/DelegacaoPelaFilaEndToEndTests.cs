using System.Text.Json;
using Dante.Application.Agentes;
using Dante.Application.Execucoes;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Worker.Execucoes;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using static Dante.Tests.DelegacoesDominioTests;
using static Dante.Tests.FilaDuravelPostgreSqlTests;

namespace Dante.Tests;

// #309 (AD-70), PR 2, de ponta a ponta contra PostgreSQL descartável: o ProcessadorDaFila real com o
// ExecutorDeDelegacoesDaFila e o ExecutorDeTarefasDaFila (retomada) reais, sessões reservadas com drivers falsos e o
// ledger espelhado. A origem é uma Tarefa cuja primeira etapa pede a delegação pelo caso de uso interno (o que o
// dante_coordenacao fará no PR 3) e termina o turno. Fluxo: pai solicita → delegação persistida → filha na fila →
// destinatário executa numa sessão própria → resultado gravado → pai retomado numa sessão nova → resposta ao dono. O
// mesmo fluxo é repetido parando o Worker no meio da filha e no meio da retomada: nada é perdido nem gravado duas vezes.
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

        // A filha roda numa sessão reservada do destinatário: plan, no contexto congelado da origem, sem o ambiente do
        // Worker e sem servidores de ferramentas; o pedido entra como dado não confiável, com o schema do contrato.
        var filha = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal));
        var opcoes = filha.StartOptions!;
        Assert.Equal((AgentPermissionProfile.Plan, a.Repositorio), (opcoes.Profile, opcoes.WorkingDirectory));
        Assert.Empty(opcoes.ToolServers ?? []);
        Assert.DoesNotContain(opcoes.EnvironmentVariables ?? new Dictionary<string, string>(), v => v.Key == "DANTE_SEGREDO_DE_TESTE");
        var prompt = filha.TurnInputs[0].Text;
        Assert.Contains("<<<DADO_NAO_CONFIAVEL", prompt);
        Assert.Contains("consulta.v1", prompt);
        Assert.Contains("\"confianca\"", prompt);
        Assert.Equal(EstadoDaExecucao.AguardandoDelegacoes, await a.EstadoDaOrigemAsync());

        // Primeiro sem bloco JSON: o servidor recusa e pede a correção uma vez, na mesma sessão.
        Responder(filha, "Acho que sim.");
        await Eventually(() => Task.FromResult(filha.TurnInputs.Count == 2));
        Assert.Contains("O resultado não foi aceito", filha.TurnInputs[1].Text);
        Responder(filha, $"Aqui está:\n```json\n{Json}\n```");

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
        Assert.Equal(2, a.Drivers.Created.Count);
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
        var primeira = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal));
        await a.PararAsync();
        Assert.Equal(EstadoDaDelegacao.EmAndamento, (await a.DelegacaoAsync()).Estado);
        Assert.Equal(EstadoDaExecucao.AguardandoDelegacoes, await a.EstadoDaOrigemAsync());

        a.Iniciar();
        var segunda = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal), depoisDe: primeira);
        Responder(segunda, $"```json\n{Json}\n```");

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
        var retomada = await a.DriverAsync(c => c.StartsWith("turn:Retomada da tarefa", StringComparison.Ordinal));
        Assert.Contains("Falhou", retomada.TurnInputs[0].Text);
        Assert.Contains("não está mais ativo", retomada.TurnInputs[0].Text);
        Responder(retomada, "Sem a consulta.");
        await a.Api.ProximaAsync();
        Assert.Equal(EstadoDaDelegacao.Falhou, (await a.DelegacaoAsync()).Estado);
        // Nenhuma sessão do destinatário existiu.
        Assert.Single(a.Drivers.Created);
    }

    [PostgreSqlFact]
    public async Task DuracaoMaximaDoExecutorEncerraASessaoEExpiraADelegacao()
    {
        await using var a = await Ambiente.CriarAsync();
        await a.OrigemAsync();
        // O relógio do executor já está além da duração gravada no início: o temporizador local encerra a sessão.
        a.Iniciar(new RelogioManual(DateTimeOffset.UtcNow.AddMinutes(61)));
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

    // A primeira etapa da origem: o turno pede a delegação pelo caso de uso (como a ferramenta do PR 3) e termina.
    // As seguintes são a retomada, executada pelo ExecutorDeTarefasDaFila real.
    private sealed class OrigemDeTeste(IServiceScopeFactory escopos, ExecutorDeTarefasDaFila real,
        Func<Task>? antesDeConcluir = null) : IExecutorDeEtapa
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
            await using (var escopo = escopos.CreateAsyncScope())
                await escopo.ServiceProvider.GetRequiredService<IDelegacoesAppService>().SolicitarAsync(
                    new SolicitacaoDeDelegacaoDto(new ProprietarioDaExecucao("telegram", 123), alvo.IdExecucao, alvo.IdEtapa,
                        "revisor", "consulta.v1", Pergunta, "Conferir o índice por estado", "Citar o arquivo."),
                    cancellationToken);
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
            a.Sessoes = new SessionRegistry(a.Drivers, NullLogger<SessionRegistry>.Instance, ledger: a.Espelho);
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
                new OrigemDeTeste(Escopos, real, antesDeConcluir)
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

        public async ValueTask DisposeAsync()
        {
            if (processador is not null) await PararAsync();
            await Sessoes.DisposeAsync();
            await Espelho.DisposeAsync();
            await Provider.DisposeAsync();
            await banco.DisposeAsync();
            if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        }
    }
}
