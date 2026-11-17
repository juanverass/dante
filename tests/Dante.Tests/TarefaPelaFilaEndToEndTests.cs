using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Dante.Application.Agentes;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Contextos;
using Dante.Worker.Execucoes;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Dante.Tests.FilaDuravelPostgreSqlTests;

namespace Dante.Tests;

// #301 integrada à fila da #300, ponta a ponta contra PostgreSQL descartável (DANTE_TEST_POSTGRES): /tarefa grava
// execução, etapa somente leitura, evento e trabalho numa transação; o ProcessadorDaFila real reivindica, o executor de
// Tarefa confere guarda, versão congelada, agente e catálogo, a sessão reservada roda em plan fora de transação e o
// resultado é gravado sob posse antes de chegar ao Telegram. Sessões usam drivers falsos; nenhuma CLI real roda.
[Collection("FilaDuravel")]
public sealed class TarefaPelaFilaEndToEndTests
{
    private const string CriarDev =
        "/agente criar dev driver=claude model=opus modo=auto maximo=auto escrita=sim capacidades=implementar repos=@first\nImplemente.";

    [PostgreSqlFact]
    public async Task TarefaPelaFilaRodaSomenteLeituraGravaAntesDeResponderENaoAfetaOsLegados()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco);
        await a.IniciarAsync();
        Assert.StartsWith("Agente dev criado (v1)", await a.Send(CriarDev, 1));

        var confirmacao = await a.Send("/tarefa dev @first leia o README", 2);
        Assert.Matches(@"^Tarefa E\d+ enfileirada: agente dev v1 \(@first\)\. " + Regex.Escape(MontagemDaTarefa.AvisoDeLeitura) + "$",
            confirmacao);
        var codigo = Regex.Match(confirmacao, @"E\d+").Value;
        var numero = long.Parse(codigo[1..]);

        // Sessão reservada: plan no checkout principal (só leitura), instruções da versão + aviso, modelo da versão.
        var driver = await a.DriverComAsync("turn:leia o README");
        var opcoes = driver.StartOptions!;
        Assert.Equal((AgentPermissionProfile.Plan, a.Primeiro), (opcoes.Profile, opcoes.WorkingDirectory));
        Assert.Contains(MontagemDaTarefa.AvisoDeLeitura, opcoes.ContextInstructions);
        Assert.Contains("Implemente.", opcoes.ContextInstructions);
        Assert.Equal("opus", opcoes.ModelSelection!.Model);
        Assert.Null(a.Sessoes.GetActive(123));

        // Em execução sob posse: execução Tarefa com a versão congelada, etapa de leitura, trabalho reivindicado.
        var emExecucao = await ExecucaoAsync(a.Provider, numero);
        Assert.Equal((TipoDeExecucao.Tarefa, EstadoDaExecucao.EmExecucao, EstadoDaEtapa.EmExecucao, false, PoliticaDeRetomada.Automatica),
            (emExecucao.Tipo, emExecucao.Estado, emExecucao.Etapas[0].Estado, emExecucao.Etapas[0].Escrita,
             emExecucao.Etapas[0].PoliticaDeRetomada));
        Assert.Equal((await a.VersaoAsync("dev")).Id, emExecucao.IdVersaoDoAgente);
        Assert.Equal("leia o README", emExecucao.Objetivo);
        Assert.Equal(EstadoDoTrabalho.Reivindicado, (await TrabalhoAsync(a.Provider, "telegram:123:2")).Estado);

        // Pedido de aprovação: negado pelo executor, nunca mostrado ao usuário.
        driver.Emit(new ApprovalRequestedEvent("up-1", AgentToolKind.Command, "git push"));
        await Eventually(() => driver.Responses.Count == 1);
        Assert.Equal(AgentApprovalDecision.Deny, Assert.IsType<AgentApprovalResponse>(driver.Responses[0].Response).Decision);

        // A mesma mensagem do Telegram entregue de novo não cria outra execução.
        Assert.Equal($"Pedido já recebido como {codigo}.", await a.Send("/tarefa dev @first leia o README", 2));

        driver.Emit(new MessageCompletedEvent("m1", "Análise do README pronta."));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        Assert.Equal($"Tarefa {codigo} (dev v1, @first) concluída.\n\nAnálise do README pronta.", await a.ProximaAsync());
        // Quando a resposta chega, o resultado já está gravado.
        var final = await ExecucaoAsync(a.Provider, numero);
        Assert.Equal((EstadoDaExecucao.Concluida, EstadoDaEtapa.Concluida), (final.Estado, final.Etapas[0].Estado));
        Assert.Equal(EstadoDoTrabalho.Concluido, (await TrabalhoAsync(a.Provider, "telegram:123:2")).Estado);
        await Eventually(() => driver.Disposed);
        Assert.Single(a.Drivers.Created);

        // Legados intactos: mensagem comum abre a sessão comum (a da tarefa nunca foi a ativa) e /claude segue pela
        // fila de jobs, com as mesmas respostas.
        a.Enviar("olá", 3);
        var comum = await a.DriverComAsync("turn:olá");
        Assert.Equal(AgentPermissionProfile.Manual, comum.StartOptions!.Profile);
        Assert.Null(comum.StartOptions.ContextInstructions);
        Assert.Null(a.Sessoes.GetActive(123)!.RegisteredAgent);
        var job = await a.Send("/claude teste", 4);
        Assert.Matches(@"^Claude iniciado\. Job ID: J\d{6} \(General\)", job);
        Assert.StartsWith($"Claude concluído. Job {job.Split("Job ID: ")[1][..7]}", await a.ProximaAsync());
    }

    // Fail-closed antes de qualquer processo: guarda de escrita, versão divergente da registrada, catálogo indisponível
    // (transitório: backoff, sem executar) e agente desativado. Nenhuma sessão é aberta.
    [PostgreSqlFact]
    public async Task TarefaRecusadaAntesDeIniciarNaoAbreSessao()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco);
        await a.IniciarAsync(processador: false);
        Assert.StartsWith("Agente dev criado (v1)", await a.Send(CriarDev, 1));
        var codigos = new List<string>();
        for (var mensagem = 2; mensagem <= 4; mensagem++)
            codigos.Add(Regex.Match(await a.Send("/tarefa dev @first leia", mensagem), @"E\d+").Value);
        long Numero(int i) => long.Parse(codigos[i][1..]);

        // Adulterações que nenhum caminho do D.A.N.T.E. produz: o executor recusa mesmo assim.
        await Executar(banco, $"UPDATE orquestracao.etapas_da_execucao SET escrita = true WHERE id_execucao = " +
            $"(SELECT id FROM orquestracao.execucoes WHERE numero = {Numero(0)})");
        await Executar(banco, $"UPDATE orquestracao.etapas_da_execucao SET modelo = 'sonnet' WHERE id_execucao = " +
            $"(SELECT id FROM orquestracao.execucoes WHERE numero = {Numero(1)})");
        a.Catalogo.Falha = true;
        await a.IniciarProcessadorAsync();

        var avisos = new[] { await a.ProximaAsync(), await a.ProximaAsync() };
        Assert.Contains(avisos, aviso => aviso.StartsWith($"Tarefa {codigos[0]} não foi iniciada:") && aviso.Contains("worktree"));
        Assert.Contains($"Tarefa {codigos[1]} não foi iniciada: a versão do agente registrada na execução difere da do pedido.", avisos);
        // Catálogo fora: transitório, devolvido com backoff contando tentativa; nada roda.
        await PollAsync(async () => (await TrabalhoAsync(a.Provider, "telegram:123:4")) is { Tentativas: >= 1, UltimoErro: "InvalidOperationException" });
        Assert.Empty(a.Drivers.Created);

        // Agente desativado enquanto a tarefa espera: recusada quando o catálogo volta.
        Assert.StartsWith("Agente dev desativado", await a.Send("/agente desativar dev", 5));
        a.Catalogo.Falha = false;
        var desativado = await a.ProximaAsync();
        Assert.StartsWith($"Tarefa {codigos[2]} não foi iniciada:", desativado);
        Assert.Contains("desativado", desativado);
        Assert.Empty(a.Drivers.Created);
        foreach (var i in new[] { 0, 1, 2 })
            Assert.Equal(EstadoDaExecucao.Falhou, (await ExecucaoAsync(a.Provider, Numero(i))).Estado);
    }

    // Reinício do Worker: o pedido de uma tarefa só existe na memória do boot que a recebeu; o novo boot a cancela com
    // aviso e nunca a executa às cegas.
    [PostgreSqlFact]
    public async Task TarefaDeUmBootAnteriorECanceladaComAvisoENuncaExecutada()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco, IdentidadeDoProcessador.DoProcessoAtual() with { Boot = Guid.NewGuid() });
        await a.IniciarAsync(processador: false);
        Assert.StartsWith("Agente dev criado (v1)", await a.Send(CriarDev, 1));
        var codigo = Regex.Match(await a.Send("/tarefa dev @first leia", 2), @"E\d+").Value;

        var novoBoot = new ExecutorDeTarefasDaFila(a.Escopos, a.Jobs, a.Espelho, a.Sinal,
            NullLogger<ExecutorDeTarefasDaFila>.Instance, a.Api);
        var processador = new ProcessadorDaFila(a.Escopos, IdentidadeDoProcessador.DoProcessoAtual(), Rapidas, a.Sinal,
            [novoBoot], new MetricasDaFila(), NullLogger<ProcessadorDaFila>.Instance);
        await processador.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal($"Tarefa {codigo} não pôde continuar depois do reinício do Worker e foi cancelada: o pedido da " +
                "tarefa não é guardado. Reenvie se ainda precisar.", await a.ProximaAsync());
        }
        finally
        {
            await processador.StopAsync(CancellationToken.None);
        }
        Assert.Empty(a.Drivers.Created);
        Assert.Equal(EstadoDaExecucao.Cancelada, (await ExecucaoAsync(a.Provider, long.Parse(codigo[1..]))).Estado);
    }

    // Fencing: outro processador assumiu o trabalho (geração nova) enquanto a sessão rodava. A posse local cai, a sessão é
    // encerrada e nada é gravado nem respondido; a retomada Automatica roda de novo no mesmo boot e só ela entrega.
    [PostgreSqlFact]
    public async Task PossePerdidaDuranteATarefaNaoGravaNemRespondeERetomaAutomaticamente()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco);
        await a.IniciarAsync();
        Assert.StartsWith("Agente dev criado (v1)", await a.Send(CriarDev, 1));
        var codigo = Regex.Match(await a.Send("/tarefa dev @first leia", 2), @"E\d+").Value;
        var numero = long.Parse(codigo[1..]);
        var primeiro = await a.DriverComAsync("turn:leia");

        await Executar(banco, "UPDATE orquestracao.trabalhos_da_fila SET geracao = geracao + 1 " +
                              "WHERE chave_de_deduplicacao = 'telegram:123:2'");
        await Eventually(() => primeiro.Disposed);
        primeiro.Emit(new MessageCompletedEvent("m1", "resposta que não pode sair"));
        Assert.Equal(EstadoDaEtapa.EmExecucao, (await ExecucaoAsync(a.Provider, numero)).Etapas[0].Estado);

        // Lease vencido: a política Automatica retoma a leitura com o pedido que este processo ainda tem.
        Assert.Equal($"Tarefa {codigo} foi interrompida e será retomada automaticamente.", await a.ProximaAsync());
        var segundo = await a.DriverComAsync("turn:leia", ignorar: primeiro);
        segundo.Emit(new MessageCompletedEvent("m1", "resposta da retomada"));
        segundo.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        Assert.Equal($"Tarefa {codigo} (dev v1, @first) concluída.\n\nresposta da retomada", await a.ProximaAsync());
        var final = await ExecucaoAsync(a.Provider, numero);
        Assert.Equal((EstadoDaExecucao.Concluida, 2), (final.Estado, final.Etapas[0].Tentativa));
    }

    private static async Task Eventually(Func<bool> condicao)
    {
        for (var i = 0; i < 600 && !condicao(); i++) await Task.Delay(25);
        Assert.True(condicao(), "A condição esperada não ocorreu a tempo.");
    }

    private static Task PollAsync(Func<Task<bool>> condicao) => FilaDuravelPostgreSqlTests.Eventually(condicao);

    // Worker mínimo: banco descartável, ledger + fila + cadastro reais, processador real com os executores de job e de
    // tarefa, Telegram e sessões falsos.
    // #305: os comandos chamam os casos de uso da Application. Consulta sem diretório; /cancel E… de uma tarefa em
    // execução grava só o pedido, e o dono da posse encerra a sessão reservada e grava Cancelada; um único aviso, nenhum
    // resultado entregue e nenhuma reexecução.
    [PostgreSqlFact]
    public async Task TarefaEmExecucaoEAcompanhadaECanceladaPeloTelegramUmaVez()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco);
        await a.IniciarAsync();
        Assert.StartsWith("Agente dev criado (v1)", await a.Send(CriarDev, 1));
        var codigo = Regex.Match(await a.Send("/tarefa dev @first leia o README", 2), @"E\d+").Value;
        var numero = long.Parse(codigo[1..]);
        var driver = await a.DriverComAsync("turn:leia o README");
        await PollAsync(async () => (await ExecucaoAsync(a.Provider, numero)).Estado == EstadoDaExecucao.EmExecucao);

        var lista = await a.Send("/execucoes", 3);
        Assert.Contains($"{codigo} · tarefa · dev v1 · em execução · etapa 1/1", lista);
        var detalhe = await a.Send($"/execucao {codigo}", 4);
        Assert.Contains("Contexto: @first", detalhe);
        Assert.Contains("Pedido: leia o README", detalhe);
        // A etapa aponta para a sessão reservada que a executa: o S… exibido é o da sessão real no ledger, do dono,
        // vinculada sob posse antes do turno.
        var sessao = Regex.Match(detalhe, @"1\. em execução · tentativa 1 · claude opus · leitura · sessão (S\d{6})");
        Assert.True(sessao.Success, detalhe);
        var emCurso = await ExecucaoAsync(a.Provider, numero);
        await using (var escopo = a.Provider.CreateAsyncScope())
        {
            var registrada = await escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>()
                .ObterSessaoAsync(long.Parse(sessao.Groups[1].Value[1..]));
            Assert.NotNull(registrada);
            Assert.Equal((sessao.Groups[1].Value, "telegram", 123L, emCurso.Etapas[0].IdSessaoDeAgente),
                (registrada!.Codigo, registrada.Canal, registrada.IdDoDono, (Guid?)registrada.Id));
            Assert.Equal(AgentPermissionProfile.Plan, registrada.Perfil);
        }
        Assert.Contains($"Cancelar: /cancel {codigo} [motivo]", detalhe);
        Assert.DoesNotContain(a.Primeiro, detalhe);
        Assert.Equal("Nenhuma execução aguardando.", await a.Send("/aguardando", 5));
        Assert.Contains("Execuções: 1 em andamento, 0 aguardando (/execucoes, /aguardando).", await a.Send("/status", 6));

        Assert.Equal($"Cancelamento de {codigo} solicitado. A execução em andamento será encerrada e você receberá um aviso.",
            await a.Send($"/cancel {codigo} mudei de ideia", 7));
        Assert.Equal($"Tarefa {codigo} cancelada (Cancelada pelo dono: mudei de ideia). Nenhum resultado será entregue.",
            await a.ProximaAsync());
        await Eventually(() => driver.Disposed);
        var final = await ExecucaoAsync(a.Provider, numero);
        Assert.Equal((EstadoDaExecucao.Cancelada, EstadoDaEtapa.Cancelada), (final.Estado, final.Etapas[0].Estado));
        Assert.Equal(EstadoDoTrabalho.Descartado, (await TrabalhoAsync(a.Provider, "telegram:123:2")).Estado);

        // Nada mais chega: o turno que terminaria depois não entrega resultado, e a etapa não roda de novo.
        driver.Emit(new MessageCompletedEvent("m1", "tarde demais"));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        Assert.Equal($"{codigo} já está encerrada (cancelada).", await a.Send($"/cancel {codigo}", 8));
        Assert.Single(a.Drivers.Created);
        Assert.Contains($"{codigo} · tarefa · dev v1 · cancelada", await a.Send("/execucoes", 9));
        // O vínculo sobrevive ao cancelamento, e o encerramento da sessão segue normal.
        Assert.Contains($"sessão {sessao.Groups[1].Value}", await a.Send($"/execucao {codigo}", 10));
    }

    [PostgreSqlFact]
    public async Task TarefaNaFilaCanceladaPeloTelegramNuncaAbreSessao()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco);
        await a.IniciarAsync(processador: false);
        Assert.StartsWith("Agente dev criado (v1)", await a.Send(CriarDev, 1));
        var codigo = Regex.Match(await a.Send("/tarefa dev @first leia", 2), @"E\d+").Value;
        var aguardando = await a.Send("/aguardando", 3);
        Assert.Contains($"{codigo} · tarefa · dev v1 · pendente", aguardando);
        Assert.Contains("na fila, aguardando um slot de execução", aguardando);
        Assert.DoesNotContain("posição", aguardando);
        Assert.Contains("Execuções: 0 em andamento, 1 aguardando", await a.Send("/status", 4));

        Assert.Equal($"{codigo} cancelada. Nenhum processo foi iniciado.", await a.Send($"/cancel {codigo}", 5));
        Assert.Equal("Execução E999999 não encontrada. Veja /execucoes.", await a.Send("/cancel E999999", 6));
        await a.IniciarProcessadorAsync();
        await Task.Delay(1000);
        Assert.Empty(a.Drivers.Created);
        // Nenhum aviso extra: a próxima mensagem é a resposta do próximo comando.
        Assert.Equal("Nenhuma execução aguardando.", await a.Send("/aguardando", 7));
        Assert.Equal(EstadoDaExecucao.Cancelada, (await ExecucaoAsync(a.Provider, long.Parse(codigo[1..]))).Estado);
    }

    private sealed class Ambiente : IAsyncDisposable
    {
        private readonly string raiz = Path.Combine(Path.GetTempPath(), "dante-tarefa-" + Guid.NewGuid().ToString("N"));
        private readonly List<IAsyncDisposable> descartaveis = [];
        private TelegramPollingService? servico;
        private ProcessadorDaFila? processador;
        private ExecutorDeJobsDaFila? executorDeJobs;
        private ExecutorDeTarefasDaFila? executorDeTarefas;
        private IdentidadeDoProcessador identidade = IdentidadeDoProcessador.DoProcessoAtual();

        public ServiceProvider Provider { get; private set; } = null!;
        public IServiceScopeFactory Escopos => Provider.GetRequiredService<IServiceScopeFactory>();
        public EspelhoDoLedger Espelho { get; private set; } = null!;
        public JobRegistry Jobs { get; private set; } = null!;
        public SinalDaFila Sinal { get; } = new();
        public BotDeTeste Api { get; } = new();
        public FakeSessionDriverFactory Drivers { get; } = new();
        public CatalogoDeModelosFixo Catalogo { get; } = new();
        public SessionRegistry Sessoes { get; private set; } = null!;
        public string Primeiro { get; private set; } = "";

        public static async Task<Ambiente> CriarAsync(Banco banco, IdentidadeDoProcessador? identidade = null)
        {
            var ambiente = new Ambiente();
            if (identidade is not null) ambiente.identidade = identidade;
            ambiente.Provider = Provider(banco.ConnectionString, configurar: services =>
                services.Replace(ServiceDescriptor.Singleton<IAgentModelCatalog>(ambiente.Catalogo)));
            ambiente.Espelho = new EspelhoDoLedger(ambiente.Escopos, NullLogger<EspelhoDoLedger>.Instance);
            ambiente.Jobs = new JobRegistry(ambiente.Espelho);
            ambiente.Sessoes = new SessionRegistry(ambiente.Drivers, NullLogger<SessionRegistry>.Instance,
                new TelegramDeliveryService(ambiente.Api, NullLogger<TelegramDeliveryService>.Instance), ledger: ambiente.Espelho);
            await Task.CompletedTask;
            return ambiente;
        }

        public async Task IniciarAsync(bool processador = true)
        {
            var registro = new RepositoryRegistry(Path.Combine(raiz, "config", "repositories.json"));
            Primeiro = Git("first");
            registro.Add("@first", Primeiro);
            registro.Add("@second", Git("second"));
            var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
            var delivery = new TelegramDeliveryService(Api, NullLogger<TelegramDeliveryService>.Instance);
            var metricas = new MetricasDaFila();
            executorDeJobs = new ExecutorDeJobsDaFila(Escopos, Jobs, Espelho, Sinal, metricas,
                NullLogger<ExecutorDeJobsDaFila>.Instance, Api, identidade);
            executorDeTarefas = new ExecutorDeTarefasDaFila(Escopos, Jobs, Espelho, Sinal,
                NullLogger<ExecutorDeTarefasDaFila>.Instance, Api, identidade);
            var runner = new RunnerDeTeste();
            servico = new TelegramPollingService(Api, options, new TelegramUserAuthorizer(options), runner, runner, Jobs,
                NullLogger<TelegramPollingService>.Instance, registro, new GeneralWorkspace(Path.Combine(raiz, "general")),
                new AssistantSettingsStore(Path.Combine(raiz, "settings.json")), Sessoes, delivery, Catalogo,
                filaDeJobs: executorDeJobs, agentes: new TelegramAgentes(Escopos), filaDeTarefas: executorDeTarefas,
                execucoes: new TelegramExecucoes(Escopos, NullLogger<TelegramExecucoes>.Instance));
            await servico.StartAsync(CancellationToken.None);
            if (processador) await IniciarProcessadorAsync();
        }

        public async Task IniciarProcessadorAsync()
        {
            processador = new ProcessadorDaFila(Escopos, identidade, Rapidas, Sinal, [executorDeJobs!, executorDeTarefas!],
                new MetricasDaFila(), NullLogger<ProcessadorDaFila>.Instance);
            await processador.StartAsync(CancellationToken.None);
        }

        public async Task<VersaoDoAgenteDto> VersaoAsync(string handle)
        {
            await using var escopo = Provider.CreateAsyncScope();
            return (await escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>().ResolverAsync(handle)).Versao!;
        }

        public void Enviar(string texto, long idDaMensagem) => Api.Enviar(texto, idDaMensagem);

        public async Task<string> Send(string texto, long idDaMensagem)
        {
            Api.Enviar(texto, idDaMensagem);
            return await ProximaAsync();
        }

        public Task<string> ProximaAsync() => Api.ProximaAsync();

        public async Task<FakeSessionDriver> DriverComAsync(string chamada, FakeSessionDriver? ignorar = null)
        {
            FakeSessionDriver? achado = null;
            await Eventually(() => (achado = Drivers.Created.FirstOrDefault(d => d != ignorar && d.Calls.Contains(chamada))) is not null);
            return achado!;
        }

        private string Git(string nome)
        {
            var caminho = Path.Combine(raiz, nome);
            Directory.CreateDirectory(caminho);
            using var git = Process.Start(new ProcessStartInfo("git")
            {
                WorkingDirectory = caminho, UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, ArgumentList = { "init", "-q" }
            })!;
            git.WaitForExit();
            Assert.Equal(0, git.ExitCode);
            return caminho;
        }

        public async ValueTask DisposeAsync()
        {
            if (servico is not null)
            {
                await servico.StopAsync(CancellationToken.None);
                servico.Dispose();
            }
            if (processador is not null) await processador.StopAsync(CancellationToken.None);
            await Sessoes.DisposeAsync();
            await Espelho.DisposeAsync();
            foreach (var descartavel in descartaveis) await descartavel.DisposeAsync();
            await Provider.DisposeAsync();
            if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        }
    }

    internal sealed class BotDeTeste : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> mensagens = Channel.CreateUnbounded<string>();
        private long proximo;

        // Chat privado: o id do chat é o do usuário (123).
        public void Enviar(string texto, long idDaMensagem) => updates.Writer.TryWrite(new TelegramUpdate(
            Interlocked.Increment(ref proximo), new TelegramMessage(new TelegramChat(123), texto, new TelegramUser(123), idDaMensagem)));

        public async Task<string> ProximaAsync() =>
            await mensagens.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken) =>
            [await updates.Reader.ReadAsync(cancellationToken)];

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            Assert.Equal(123, chatId);
            mensagens.Writer.TryWrite(text);
            return Task.CompletedTask;
        }

        public Task SendChatActionAsync(long chatId, string action, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RunnerDeTeste : ICodexRunner, IClaudeRunner
    {
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Application.Anexos.Attachment>? attachments = null, IReadOnlyList<string>? additionalDirectories = null) =>
            Task.FromResult(new AgentProcessResult(AgentProcessStatus.Succeeded, "done", "", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }
}
