using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dante.Application.Agentes;
using Dante.Application.Execucoes;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Contextos;
using Dante.Worker.Coordenacao;
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

// #310 (AD-75), PR 3, pela interface, de ponta a ponta contra PostgreSQL descartável: o cadastro do grupo no modo
// coordenador, o /grupo que cria a Coordenada, o aviso do gate com o hash, a decisão por /execucao, as unidades, a síntese
// do coordenador, o aviso do fim e a exposição da trilha e da síntese, separadas, por /execucao consolidacao. O Telegram
// só interpreta e formata; o teste faz o papel das CLIs (drivers falsos) e do processo MCP do dante_coordenacao.
[Collection("FilaDuravel")]
public sealed class CoordenacaoPeloTelegramEndToEndTests
{
    private const string Resposta = """{"resposta":"Há ix_estado.","confianca":"alta","evidencias":[{"tipo":"arquivo","referencia":"README.md:1"}]}""";
    private const string Plano = """
        {"unidades":[
          {"id":"consulta","papel":"critico","contrato":"consulta.v1","objetivo":"Conferir a consulta por estado","entrada":{"pergunta":"O índice cobre a consulta?"},"dependeDe":["indice"]},
          {"id":"indice","papel":"quem","contrato":"consulta.v1","objetivo":"Levantar os índices","entrada":{"pergunta":"Quais índices existem?"}}
        ]}
        """;

    [PostgreSqlFact]
    public async Task CoordenacaoPeloChatDoCadastroAConsolidacao()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco);
        await a.IniciarAsync();
        Assert.Contains("criado (v1)", await a.Send("/agente criar coord driver=codex capacidades=planejar repos=@first", 1));
        Assert.Contains("criado (v1)", await a.Send("/agente criar pesq driver=codex capacidades=pesquisar repos=@first", 2));
        Assert.Contains("criado (v1)", await a.Send("/agente criar rev driver=codex capacidades=revisar repos=@first", 3));
        var criado = await a.Send("/grupo criar pesquisa coordenador=chefe repos=@first\nchefe=coord: planeja\n" +
            "quem=pesq: pesquisa\ncritico=rev: revisa", 4);
        var ativar = criado.Split('\n').Single(l => l.StartsWith("Para usá-la", StringComparison.Ordinal));
        Assert.Contains("v1 ativada", await a.Send(ativar[ativar.IndexOf("/grupo ativar", StringComparison.Ordinal)..], 5));

        // /grupo de um grupo coordenado cria a Coordenada (e não um roteiro).
        var confirmacao = await a.Send("/grupo pesquisa @first Investigar o índice por estado", 6);
        Assert.Matches(@"^Coordenação E\d+ enfileirada: grupo pesquisa v1, coordenador chefe", confirmacao);
        Assert.Contains("você o aprova antes de qualquer delegação", confirmacao);
        var codigo = Regex.Match(confirmacao, @"E\d+").Value;
        // A mesma mensagem não cria outra.
        Assert.Contains($"Pedido já recebido como {codigo}", await a.Send("/grupo pesquisa @first Investigar o índice por estado", 6));

        var coordenador = await a.DriverAsync(c => c.StartsWith("turn:Planejamento da coordenação", StringComparison.Ordinal));
        Assert.True((bool)JsonNode.Parse(await Mcp.ChamarAsync(coordenador, "propor_plano",
            new { plano = JsonNode.Parse(Plano) }))!["registrado"]!);
        Responder(coordenador, "Plano proposto.");
        var gate = await a.ProximaAsync();
        var hash = Regex.Match(gate, @"hash ([0-9a-f]{12})\)").Groups[1].Value;
        Assert.Contains($"Coordenação {codigo} (pesquisa v1) aguarda sua aprovação do plano (revisão 1, 2 unidade(s)", gate);
        Assert.Contains("- indice → quem: Levantar os índices", gate);

        // /execucao mostra o plano e o gate; o roteiro não se aplica; hash errado não decide.
        var detalhe = await a.Send($"/execucao {codigo}", 7);
        Assert.Contains($"{codigo} (coordenação de grupo, coord v1)", detalhe);
        Assert.Contains("Grupo: pesquisa v1", detalhe);
        Assert.Contains($"Plano r1 (inicial): aguardando aprovação, 2 unidade(s), hash {hash}", detalhe);
        Assert.Contains($"/execucao aprovar {codigo} {hash}", detalhe);
        Assert.Contains("é uma coordenação", await a.Send($"/execucao objetivo {codigo} outra tarefa", 8));
        Assert.Contains("hash", await a.Send($"/execucao aprovar {codigo} 000000000000", 9));
        Assert.Contains($"{codigo}: plano aprovado (revisão 1)", await a.Send($"/execucao aprovar {codigo} {hash}", 10));

        var indice = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal));
        Assert.True((bool)JsonNode.Parse(await Mcp.ChamarAsync(indice, "entregar_resultado",
            new { resultado = JsonNode.Parse(Resposta) }))!["registrado"]!);
        Responder(indice, "Entregue.");
        var consulta = await a.DriverAsync(c => c.StartsWith("turn:Delegação", StringComparison.Ordinal), indice);
        Assert.True((bool)JsonNode.Parse(await Mcp.ChamarAsync(consulta, "entregar_resultado",
            new { resultado = JsonNode.Parse(Resposta.Replace("Há ix_estado.", "Não cobre.")) }))!["registrado"]!);
        Responder(consulta, "Entregue.");

        // Antes da síntese, a consolidação mostra a trilha e diz que a síntese ainda não existe.
        var sintetizador = await a.DriverAsync(c => c.StartsWith("turn:Síntese da coordenação", StringComparison.Ordinal));
        Assert.Contains("ainda não", await a.Send($"/execucao consolidacao {codigo}", 11));
        var prompt = sintetizador.TurnInputs[0].Text;
        string Hash(string unidade) => Regex.Match(prompt, $@"unidade {unidade}, .*?; hash ([0-9a-f]{{64}})").Groups[1].Value;
        Assert.True((bool)JsonNode.Parse(await Mcp.ChamarAsync(sintetizador, "entregar_sintese", new
        {
            sintese = new
            {
                resumo = "Há o índice; a cobertura da consulta diverge.",
                divergencias = new[]
                {
                    new { texto = "O índice cobre a consulta?", citacoes = new[]
                    {
                        new { revisao = 1, unidade = "indice", hash = Hash("indice")[..12] },
                        new { revisao = 1, unidade = "consulta", hash = Hash("consulta")[..12] }
                    } }
                }
            }
        }))!["registrado"]!);
        Responder(sintetizador, "Síntese entregue.");
        var fim = await a.ProximaAsync();
        Assert.Contains($"Coordenação {codigo} (pesquisa v1) concluída. Unidades: 2 respondida(s)", fim);
        Assert.Contains("0 conclusão(ões), 1 divergência(s), 0 inconclusivo(s)", fim);

        // A trilha mecânica e a síntese, separadas e cada uma com o seu alcance.
        var consolidacao = await a.Send($"/execucao consolidacao {codigo}", 12);
        Assert.True(consolidacao.Length <= TelegramExecucoes.LimiteDaMensagem + 80, consolidacao.Length.ToString());
        Assert.Contains($"Consolidação de {codigo} — pesquisa v1", consolidacao);
        Assert.Contains("Trilha mecânica: Trilha mecânica gerada pelo servidor", consolidacao);
        Assert.Contains("- r1 indice → quem = pesq v1: respondida", consolidacao);
        // O resultado é exibido legível (a forma canônica gravada escapa acentos), sem mudar o que o hash cobre.
        Assert.Contains("\"resposta\":\"Não cobre.\"", consolidacao);
        Assert.Contains("O servidor não avaliou o mérito das afirmações", consolidacao);
        Assert.Contains($"Divergências (não resolvidas):\n- O índice cobre a consulta? [r1/consulta {Hash("consulta")[..12]}, " +
            $"r1/indice {Hash("indice")[..12]}]", consolidacao);
        Assert.True(consolidacao.IndexOf("Trilha mecânica", StringComparison.Ordinal) <
            consolidacao.IndexOf("Síntese do coordenador", StringComparison.Ordinal));
        Assert.Contains("Resultados: /execucao consolidacao", await a.Send($"/execucao {codigo}", 13));
        Assert.Contains("não encontrada", await a.Send("/execucao consolidacao E999999", 14));
    }

    private static void Responder(FakeSessionDriver driver, string mensagem)
    {
        driver.Emit(new MessageCompletedEvent("m", mensagem));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
    }

    // Faz o que o processo MCP do dante_coordenacao da sessão faria: conecta uma vez no pipe dela, apresenta o token e
    // chama pela mesma conexão.
    private static class Mcp
    {
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
    }

    private sealed class Ambiente : IAsyncDisposable
    {
        private readonly string raiz = Path.Combine(Path.GetTempPath(), "dante-coord-telegram-" + Guid.NewGuid().ToString("N"));
        private TelegramPollingService? servico;
        private ProcessadorDaFila? processador;
        private FerramentasDeCoordenacao? coordenacao;

        public ServiceProvider Provider { get; private set; } = null!;
        public IServiceScopeFactory Escopos => Provider.GetRequiredService<IServiceScopeFactory>();
        public EspelhoDoLedger Espelho { get; private set; } = null!;
        public JobRegistry Jobs { get; private set; } = null!;
        public SinalDaFila Sinal { get; } = new();
        public TarefaPelaFilaEndToEndTests.BotDeTeste Api { get; } = new();
        public FakeSessionDriverFactory Drivers { get; } = new();
        public CatalogoDeModelosFixo Catalogo { get; } = new();
        public SessionRegistry Sessoes { get; private set; } = null!;
        public RepositoryRegistry Registro { get; private set; } = null!;

        public static Task<Ambiente> CriarAsync(Banco banco)
        {
            var ambiente = new Ambiente();
            ambiente.Registro = new RepositoryRegistry(Path.Combine(ambiente.raiz, "config", "repositories.json"));
            var primeiro = Path.Combine(ambiente.raiz, "first");
            Directory.CreateDirectory(primeiro);
            RepositorioGitDeTeste.Git(primeiro, "init", "-q");
            File.WriteAllText(Path.Combine(primeiro, "README.md"), "# first\n");
            ambiente.Registro.Add("@first", primeiro);
            ambiente.Provider = Provider(banco.ConnectionString, configurar: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IAgentModelCatalog>(ambiente.Catalogo));
                services.Replace(ServiceDescriptor.Singleton<Dante.Application.Contextos.ICatalogoDeRepositorios>(ambiente.Registro));
            });
            ambiente.Espelho = new EspelhoDoLedger(ambiente.Escopos, NullLogger<EspelhoDoLedger>.Instance);
            ambiente.Jobs = new JobRegistry(ambiente.Espelho);
            ambiente.coordenacao = new FerramentasDeCoordenacao(ambiente.Escopos, NullLogger<FerramentasDeCoordenacao>.Instance);
            ambiente.Sessoes = new SessionRegistry(ambiente.Drivers, NullLogger<SessionRegistry>.Instance,
                new TelegramDeliveryService(ambiente.Api, NullLogger<TelegramDeliveryService>.Instance),
                toolServers: new CompositorDeFerramentas([ambiente.coordenacao]), ledger: ambiente.Espelho);
            return Task.FromResult(ambiente);
        }

        public async Task IniciarAsync()
        {
            var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
            var delivery = new TelegramDeliveryService(Api, NullLogger<TelegramDeliveryService>.Instance);
            var roteiros = new ExecutorDeRoteirosDaFila(Escopos, () => Sessoes, Espelho, Sinal,
                NullLogger<ExecutorDeRoteirosDaFila>.Instance, Api);
            var coordenacoes = new ExecutorDeCoordenacoesDaFila(Escopos, () => Sessoes,
                NullLogger<ExecutorDeCoordenacoesDaFila>.Instance, Api, Espelho, Sinal);
            var runner = new Runner();
            servico = new TelegramPollingService(Api, options, new TelegramUserAuthorizer(options), runner, runner, Jobs,
                NullLogger<TelegramPollingService>.Instance, Registro, new GeneralWorkspace(Path.Combine(raiz, "general")),
                new AssistantSettingsStore(Path.Combine(raiz, "settings.json")), Sessoes, delivery, Catalogo,
                agentes: new TelegramAgentes(Escopos), execucoes: new TelegramExecucoes(Escopos, NullLogger<TelegramExecucoes>.Instance),
                grupos: new TelegramGrupos(Escopos), filaDeRoteiros: roteiros, filaDeCoordenacoes: coordenacoes);
            await servico.StartAsync(CancellationToken.None);
            processador = new ProcessadorDaFila(Escopos, IdentidadeDoProcessador.DoProcessoAtual(), Rapidas, Sinal,
                [coordenacoes, new ExecutorDeDelegacoesDaFila(Escopos, () => Sessoes, NullLogger<ExecutorDeDelegacoesDaFila>.Instance)],
                new MetricasDaFila(), NullLogger<ProcessadorDaFila>.Instance);
            await processador.StartAsync(CancellationToken.None);
        }

        public async Task<string> Send(string texto, long idDaMensagem)
        {
            Api.Enviar(texto, idDaMensagem);
            return await Api.ProximaAsync();
        }

        public Task<string> ProximaAsync() => Api.ProximaAsync();

        public async Task<FakeSessionDriver> DriverAsync(Func<string, bool> chamada, FakeSessionDriver? depoisDe = null)
        {
            FakeSessionDriver? achado = null;
            await Eventually(() =>
            {
                var lista = Drivers.Created.ToList();
                achado = lista.FirstOrDefault(d => d != depoisDe && d.Calls.Any(chamada) &&
                    (depoisDe is null || lista.IndexOf(d) > lista.IndexOf(depoisDe)));
                return Task.FromResult(achado is not null);
            }, TimeSpan.FromSeconds(30));
            return achado!;
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
            coordenacao?.Dispose();
            await Espelho.DisposeAsync();
            await Provider.DisposeAsync();
            if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        }
    }

    private sealed class Runner : ICodexRunner, IClaudeRunner
    {
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Application.Anexos.Attachment>? attachments = null, IReadOnlyList<string>? additionalDirectories = null) =>
            Task.FromResult(new AgentProcessResult(AgentProcessStatus.Succeeded, "done", "", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }
}
