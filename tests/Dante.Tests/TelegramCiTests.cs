using System.Threading.Channels;
using Dante.Application;
using Dante.Application.Agentes;
using Dante.Application.IntegracaoDeCi;
using Dante.Domain.Preferencias;
using Dante.Worker.Jobs;
using Dante.Worker.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

// #238 (AD-77): adapter do Telegram para o andamento do CI de um PR. A gramática é fechada e determinística (o que não
// casa segue para a conversa), a resposta é curta, com links do PR e das execuções, e nunca transforma falta de
// permissão ou indisponibilidade em "falhou" nem sucesso em "pode mesclar".
public sealed class TelegramCiTests
{
    private const string Head = "1a2b3c4d5e6f7a8b9c0d1a2b3c4d5e6f7a8b9c0d";
    private const long Usuario = 123;
    private static readonly DateTimeOffset Agora = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly ConexaoDeCi Conexao = new("pessoal", "secret://host/DANTE_CI_TOKEN");
    private static readonly RepositorioDeCi App = new("@app", "dono/app", 42, Conexao);

    // ---- gramática ----

    [Theory]
    [InlineData("/ci 12", 12, null)]
    [InlineData("/ci @app 12", 12, "@app")]
    [InlineData("/CI #12 @app", 12, "@app")]
    [InlineData("como está o CI do PR 12 do @dante?", 12, "@dante")]
    [InlineData("Como está o CI do PR #12?", 12, null)]
    [InlineData("status do CI do PR 7 no @api", 7, "@api")]
    [InlineData("e o CI do PR 3?", 3, null)]
    [InlineData("o CI do PR 12 já passou?", 12, null)]
    [InlineData("Qual é o status do CI do pull request 40 do @Dante", 40, "@dante")]
    [InlineData("checks do PR 5", 5, null)]
    [InlineData("  como   anda o pipeline do PR 8 em @app ", 8, "@app")]
    public void PedidoDeCiExplicitoOuNaturalEReconhecido(string texto, int numero, string? alias)
    {
        Assert.Equal(new TelegramCi.Pedido(numero, alias), TelegramCi.Interpretar(texto));
    }

    [Theory]
    [InlineData("/ci")]
    [InlineData("/ci @app")]
    [InlineData("/ci abc")]
    [InlineData("/ci 0")]
    [InlineData("/ci 12 13")]
    [InlineData("/ci @app @api 12")]
    [InlineData("/ci -3")]
    public void ComandoSemNumeroDePrValidoPedeOUso(string texto)
    {
        Assert.Equal(new TelegramCi.Pedido(null, null), TelegramCi.Interpretar(texto));
    }

    [Theory]
    [InlineData("como está o CI?")]
    [InlineData("como está o PR 12?")]
    [InlineData("corrija o CI do PR 12")]
    [InlineData("o que você acha do CI do PR 12 e do deploy?")]
    [InlineData("/claude como está o CI do PR 12")]
    [InlineData("/cicd 12")]
    [InlineData("como está o CI do PR 12 do @app e do @api?")]
    public void OutrasMensagensSeguemParaAConversa(string texto)
    {
        Assert.Null(TelegramCi.Interpretar(texto));
    }

    // ---- formatação ----

    [Fact]
    public void SucessoMostraLinksCommitAtualENaoAfirmaQueOPrPodeSerMesclado()
    {
        var texto = TelegramCi.Formatar(Situacao(Check(EstadoDoCheckDeCi.Sucesso,
            Execucao(10, "CI", EstadoDaExecucao.Sucesso), Execucao(11, "Lint", EstadoDaExecucao.Sucesso))));

        Assert.StartsWith("PR #12 de @app: Corrige o polling\nCommit atual: 1a2b3c4\nCI: tudo passou (2 workflow(s)).", texto);
        Assert.Contains("• CI: passou — https://github.com/dono/app/actions/runs/10", texto);
        Assert.Contains("• Lint: passou — https://github.com/dono/app/actions/runs/11", texto);
        Assert.Contains("não diz se o PR pode ser mesclado", texto);
        Assert.EndsWith("PR: https://github.com/dono/app/pull/12", texto);
        Assert.DoesNotContain("api.github.com", texto);
        Assert.DoesNotContain(Head, texto);
    }

    [Fact]
    public void FalhaMostraJobEEtapaQueFalharamEAReexecucao()
    {
        var execucao = Execucao(10, "CI", EstadoDaExecucao.Falha, tentativa: 2,
            Job(500, "testes", EstadoDaExecucao.Falha, new EtapaDeJobDeCiDto(1, "Preparar", EstadoDaExecucao.Sucesso),
                new EtapaDeJobDeCiDto(2, "Rodar testes", EstadoDaExecucao.Falha)),
            Job(501, "lint", EstadoDaExecucao.Sucesso));

        var texto = TelegramCi.Formatar(Situacao(Check(EstadoDoCheckDeCi.Falha, execucao)));

        Assert.Contains("CI: falhou.", texto);
        Assert.Contains("• CI: falhou, tentativa 2 — https://github.com/dono/app/actions/runs/10", texto);
        Assert.Contains("   testes falhou na etapa Rodar testes", texto);
        Assert.DoesNotContain("lint", texto);
        Assert.DoesNotContain("mesclado", texto);
    }

    [Fact]
    public void PendenteResumeOQueFaltaPorWorkflowEJob()
    {
        var texto = TelegramCi.Formatar(Situacao(Check(EstadoDoCheckDeCi.EmAndamento,
            Execucao(10, "CI", EstadoDaExecucao.EmAndamento, 1,
                Job(500, "build", EstadoDaExecucao.EmAndamento), Job(501, "testes", EstadoDaExecucao.AguardandoRunner)),
            Execucao(11, "Lint", EstadoDaExecucao.Sucesso))));

        Assert.Contains("CI: em andamento (1 de 2 workflow(s) concluído(s)).", texto);
        Assert.Contains("   pendente: build (em andamento), testes (aguardando runner)", texto);
        Assert.DoesNotContain("mesclado", texto);
    }

    [Theory]
    [InlineData(EstadoDoCheckDeCi.SemPermissao, null, "sem permissão para ler o CI")]
    [InlineData(EstadoDoCheckDeCi.Indisponivel, MotivoDaFalhaNaConsultaDeCi.Indisponivel, "não consegui saber o resultado agora")]
    [InlineData(EstadoDoCheckDeCi.Indisponivel, MotivoDaFalhaNaConsultaDeCi.LimiteDeTaxa, "limitou as consultas; tente de novo em cerca de 2 min")]
    public void SemPermissaoOuIndisponivelNuncaViramFalha(EstadoDoCheckDeCi estado, MotivoDaFalhaNaConsultaDeCi? motivo, string esperado)
    {
        var situacao = Situacao(Check(estado) with { Completo = false, MotivoDaIndisponibilidade = motivo })
            with { RepetirApos = TimeSpan.FromSeconds(90) };

        var texto = TelegramCi.Formatar(situacao);

        Assert.Contains(esperado, texto);
        Assert.Contains("Isso não quer dizer que o CI falhou.", texto);
        Assert.DoesNotContain("CI: falhou", texto);
        Assert.DoesNotContain("mesclado", texto);
    }

    [Fact]
    public void ResultadoIncompletoSemFalhaNaoAfirmaSucesso()
    {
        var texto = TelegramCi.Formatar(Situacao(Check(EstadoDoCheckDeCi.Indisponivel, Execucao(10, "CI", EstadoDaExecucao.Sucesso))
            with { Completo = false, MotivoDaIndisponibilidade = MotivoDaFalhaNaConsultaDeCi.Indisponivel }));

        Assert.Contains("o resultado veio incompleto", texto);
        Assert.DoesNotContain("tudo passou", texto);
    }

    [Fact]
    public void CommitNovoSemExecucaoDizQueAsAntigasNaoContam()
    {
        var texto = TelegramCi.Formatar(Situacao(Check(EstadoDoCheckDeCi.SemExecucao) with { Descartadas = 2 }));

        Assert.Contains("CI: ainda não há execução de CI para este commit.", texto);
        Assert.Contains("Execuções de commits anteriores, de outros PRs e tentativas substituídas não contam", texto);
    }

    [Fact]
    public void ExecucaoSemAssociacaoAoPrNaoViraResultadoEEInformada()
    {
        var soSemAssociacao = TelegramCi.Formatar(Situacao(Check(EstadoDoCheckDeCi.Indisponivel) with
        {
            SemAssociacaoAoPr = 1,
            MotivoDaIndisponibilidade = MotivoDaFalhaNaConsultaDeCi.Indisponivel
        }));
        Assert.Contains("CI: não consigo afirmar o resultado: há execução de CI deste commit, mas o provedor não a liga a " +
                        "este PR", soSemAssociacao);
        Assert.Contains("Isso não quer dizer que o CI falhou.", soSemAssociacao);
        Assert.DoesNotContain("tudo passou", soSemAssociacao);
        Assert.DoesNotContain("não aparece ligada", soSemAssociacao);

        var comAssociadas = TelegramCi.Formatar(Situacao(Check(EstadoDoCheckDeCi.Sucesso,
            Execucao(10, "CI", EstadoDaExecucao.Sucesso)) with { SemAssociacaoAoPr = 2 }));
        Assert.Contains("CI: tudo passou (1 workflow(s)).", comAssociadas);
        Assert.Contains("2 execuções deste commit não aparecem ligadas a este PR no provedor e não foram consideradas.",
            comAssociadas);
    }

    [Fact]
    public void RepositorioSemWorkflowCanceladoExpiradoEForkNaFila()
    {
        Assert.Contains("não tem workflow de CI configurado", TelegramCi.Formatar(Situacao(Check(EstadoDoCheckDeCi.SemWorkflowConfigurado))));
        Assert.Contains("CI: cancelado.", TelegramCi.Formatar(Situacao(Check(EstadoDoCheckDeCi.Cancelado,
            Execucao(10, "CI", EstadoDaExecucao.Cancelada)))));
        Assert.Contains("CI: expirou", TelegramCi.Formatar(Situacao(Check(EstadoDoCheckDeCi.Expirado,
            Execucao(10, "CI", EstadoDaExecucao.Expirada)))));

        var fork = Situacao(Check(EstadoDoCheckDeCi.NaFila, Execucao(10, "CI", EstadoDaExecucao.NaFila))) with
        {
            PullRequest = new PullRequestDeCiDto(App, 12, Head, EstadoDoPullRequestDeCi.Aberto, true, true, "Do fork",
                new Uri("https://github.com/dono/app/pull/12"))
        };
        var texto = TelegramCi.Formatar(fork);
        Assert.Contains("PR #12 de @app: Do fork (rascunho, de fork)", texto);
        Assert.Contains("CI: na fila (0 de 1 workflow(s) concluído(s)).", texto);
        Assert.Contains("aprovação de um mantenedor", texto);
    }

    [Fact]
    public void MuitosWorkflowsSaoResumidos()
    {
        var execucoes = Enumerable.Range(1, 12).Select(i => Execucao(i, $"W{i:00}", EstadoDaExecucao.Sucesso)).ToArray();

        var texto = TelegramCi.Formatar(Situacao(Check(EstadoDoCheckDeCi.Sucesso, execucoes)));

        Assert.Contains("• W10:", texto);
        Assert.DoesNotContain("• W11:", texto);
        Assert.Contains("… e mais 2 workflow(s).", texto);
    }

    [Theory]
    [InlineData(ProblemaDoRepositorioDeCi.SemRepositorio, null, "De qual repositório é o PR #12? Diga o alias, por exemplo /ci @alias 12")]
    [InlineData(ProblemaDoRepositorioDeCi.AliasInvalido, "app", "Alias inválido: app.")]
    [InlineData(ProblemaDoRepositorioDeCi.NaoCadastrado, "@web", "Repositório @web não cadastrado. Veja /repos.")]
    [InlineData(ProblemaDoRepositorioDeCi.AtivoForaDoCadastro, "@velho", "O repositório ativo @velho não está mais cadastrado.")]
    public void ProblemaDeRepositorioOrientaOProximoPasso(ProblemaDoRepositorioDeCi problema, string? alias, string esperado)
    {
        var texto = TelegramCi.Formatar(new SituacaoDoCiDoPullRequestDto { NumeroDoPr = 12, Alias = alias, ProblemaDoRepositorio = problema });

        Assert.StartsWith(esperado, texto);
    }

    [Theory]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.NaoConfigurada, "O CI de @app não está configurado neste D.A.N.T.E.")]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.NaoAutorizada, "A conexão de CI do host não pode consultar @app")]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.SemPermissao, "não tem permissão para ler o PR #12 de @app. Não sei o resultado do CI.")]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.NaoEncontrada, "Não encontrei o PR #12 em @app")]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.LimiteDeTaxa, "tente de novo em 30 s. Não sei o resultado do CI")]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.Inconsistente, "não confere com @app")]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.Indisponivel, "o provedor não respondeu. Não sei o resultado do CI")]
    public void FalhaAoLerOPrEExplicadaSemInventarResultado(MotivoDaFalhaNaConsultaDeCi motivo, string esperado)
    {
        var texto = TelegramCi.Formatar(new SituacaoDoCiDoPullRequestDto
        {
            NumeroDoPr = 12, Alias = "@app", Origem = OrigemDoRepositorioDeCi.Explicito, FalhaDoPullRequest = motivo,
            RepetirApos = TimeSpan.FromSeconds(30)
        });

        Assert.Contains(esperado, texto);
        Assert.DoesNotContain("falhou", texto);
    }

    [Fact]
    public void RepositorioVinculadoSemConexaoExplicaQueNaoHerda()
    {
        var texto = TelegramCi.Formatar(new SituacaoDoCiDoPullRequestDto
        {
            NumeroDoPr = 12, Alias = "@lib", FalhaDoPullRequest = MotivoDaFalhaNaConsultaDeCi.NaoConfigurada
        });

        Assert.Contains("Repositórios vinculados não herdam a conexão do principal.", texto);
        Assert.Contains("/repo show @lib", texto);
    }

    // ---- ponta a ponta pelo polling ----

    [Fact]
    public async Task ComandoEPerguntaNaturalRespondemSemChegarAoAgente()
    {
        var provedor = new ProvedorFalso();
        await using var cenario = await Cenario.IniciarAsync(provedor);

        cenario.Api.Enviar("/ci @app 12");
        var resposta = await cenario.Api.ProximaAsync();
        Assert.StartsWith("PR #12 de @app: Corrige o polling\nCommit atual: 1a2b3c4\nCI: tudo passou", resposta);

        cenario.Api.Enviar("como está o CI do PR 12 do @app?");
        Assert.Equal(resposta, await cenario.Api.ProximaAsync());

        // Sem alias: usa o repositório ativo do usuário.
        cenario.Api.Enviar("/ci 12");
        Assert.StartsWith("PR #12 de @app", await cenario.Api.ProximaAsync());

        cenario.Api.Enviar("/ci");
        Assert.Equal(TelegramCi.Uso, await cenario.Api.ProximaAsync());
        Assert.Equal(0, cenario.Runner.Execucoes);
        Assert.Equal(3, provedor.PrsLidos);
    }

    [Fact]
    public async Task FalhaInesperadaNaConsultaNaoViraFalhaDoCi()
    {
        await using var cenario = await Cenario.IniciarAsync(new ProvedorFalso { Erro = new InvalidOperationException("detalhe interno") });

        cenario.Api.Enviar("/ci @app 12");

        Assert.Equal("Consulta do CI do PR #12 indisponível agora. Isso não quer dizer que o CI falhou.",
            await cenario.Api.ProximaAsync());
    }

    [Fact]
    public void AjudaListaOComandoDeCi()
    {
        var ajuda = string.Join('\n', TelegramCommandHelp.Messages(""));
        Assert.Contains("/ci — Veja como está o CI de um PR, no commit atual dele.", ajuda);
        var detalhe = Assert.Single(TelegramCommandHelp.Messages("ci"));
        Assert.Contains("/ci [@alias] <número do PR>", detalhe);
        Assert.Contains("não acompanha sozinho", detalhe);
        Assert.Contains("como está o CI do PR 12 do @exemplo?", detalhe);
    }

    [Fact]
    public async Task SemOAdapterCompostoOComandoRespondeIndisponivel()
    {
        var api = new Bot();
        var runner = new RunnerQueConta();
        var options = Options.Create(new TelegramOptions
        {
            BotToken = "test-token", AllowedUserIds = $"{Usuario}", AgentWorkingDirectory = "/tmp/agent-work"
        });
        using var servico = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), runner, runner,
            new JobRegistry(), NullLogger<TelegramPollingService>.Instance);
        await servico.StartAsync(CancellationToken.None);
        try
        {
            api.Enviar("/ci @app 12");
            Assert.Equal(TelegramCi.Indisponivel, await api.ProximaAsync());
        }
        finally
        {
            await servico.StopAsync(CancellationToken.None);
        }
    }

    // ---- fixtures ----

    private static SituacaoDoCiDoPullRequestDto Situacao(CheckAgregadoDeCiDto check) => new()
    {
        NumeroDoPr = 12,
        Alias = "@app",
        Origem = OrigemDoRepositorioDeCi.Explicito,
        PullRequest = new PullRequestDeCiDto(App, 12, Head, EstadoDoPullRequestDeCi.Aberto, false, false, "Corrige o polling",
            new Uri("https://github.com/dono/app/pull/12")),
        Check = check
    };

    private static CheckAgregadoDeCiDto Check(EstadoDoCheckDeCi estado, params ExecucaoDeCiDto[] execucoes) => new()
    {
        Consulta = new ConsultaDeCi(App, Head, 12),
        Estado = estado,
        ObtidoEm = Agora,
        Execucoes = execucoes
    };

    private static ExecucaoDeCiDto Execucao(long id, string nome, EstadoDaExecucao estado, int tentativa = 1,
        params JobDeCiDto[] jobs) => new()
    {
        Identidade = new IdentidadeDaExecucaoDeCi(App, id, id, tentativa, Head),
        NomeDoWorkflow = nome,
        Gatilho = "pull_request",
        Estado = estado,
        UrlOficial = new Uri($"https://github.com/dono/app/actions/runs/{id}"),
        CriadaEm = Agora,
        AtualizadaEm = Agora,
        NumerosDePr = [12],
        Jobs = jobs
    };

    private static JobDeCiDto Job(long id, string nome, EstadoDaExecucao estado, params EtapaDeJobDeCiDto[] etapas) => new()
    {
        IdDoJob = id,
        Nome = nome,
        Estado = estado,
        UrlOficial = new Uri($"https://github.com/dono/app/actions/runs/10/job/{id}"),
        Etapas = etapas
    };

    private sealed class Cenario : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly TelegramPollingService servico;

        private Cenario(ServiceProvider provider, TelegramPollingService servico, Bot api, RunnerQueConta runner)
        {
            this.provider = provider;
            this.servico = servico;
            Api = api;
            Runner = runner;
        }

        public Bot Api { get; }
        public RunnerQueConta Runner { get; }

        public static async Task<Cenario> IniciarAsync(ProvedorFalso provedor)
        {
            var services = new ServiceCollection().AddApplication();
            services.AddSingleton<IProvedorDeCi>(provedor);
            services.AddSingleton<IRepositoriosDeCi>(new RepositoriosFalsos());
            services.AddSingleton<ICatalogoDeRepositorios>(new CatalogoFalso());
            services.AddSingleton<IPreferenciasDoAssistente>(new PreferenciasFalsas());
            var provider = services.BuildServiceProvider();
            var api = new Bot();
            var runner = new RunnerQueConta();
            var options = Options.Create(new TelegramOptions
            {
                BotToken = "test-token", AllowedUserIds = $"{Usuario}", AgentWorkingDirectory = "/tmp/agent-work"
            });
            var servico = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), runner, runner,
                new JobRegistry(), NullLogger<TelegramPollingService>.Instance,
                ci: new TelegramCi(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<TelegramCi>.Instance));
            await servico.StartAsync(CancellationToken.None);
            return new Cenario(provider, servico, api, runner);
        }

        public async ValueTask DisposeAsync()
        {
            await servico.StopAsync(CancellationToken.None);
            servico.Dispose();
            await provider.DisposeAsync();
        }
    }

    private sealed class ProvedorFalso : IProvedorDeCi
    {
        public Exception? Erro { get; init; }
        public int PrsLidos { get; private set; }

        public Task<PullRequestDeCiDto> ObterPullRequestAsync(RepositorioDeCi repositorio, int numero,
            CancellationToken cancellationToken = default)
        {
            if (Erro is not null) throw Erro;
            PrsLidos++;
            return Task.FromResult(new PullRequestDeCiDto(
                new RepositorioDeCi(repositorio.Alias, repositorio.NomeCompleto, 42, repositorio.Conexao), numero, Head,
                EstadoDoPullRequestDeCi.Aberto, false, false, "Corrige o polling",
                new Uri($"https://github.com/{repositorio.NomeCompleto}/pull/{numero}")));
        }

        public Task<ResultadoDaConsultaDeCi> ConsultarAsync(ConsultaDeCi consulta, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResultadoDaConsultaDeCi
            {
                PossuiWorkflowsConfigurados = true,
                Completo = true,
                ObtidoEm = Agora,
                Execucoes = [Execucao(10, "CI", EstadoDaExecucao.Sucesso)]
            });

        public Task<ExecucaoDeCiDto> ObterExecucaoAsync(IdentidadeDaExecucaoDeCi execucao,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LogDeJobDeCiDto> ObterLogDoJobAsync(IdentidadeDoJobDeCi job, int limiteEmBytes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RepositoriosFalsos : IRepositoriosDeCi
    {
        public RepositorioDeCi Resolver(string alias) => new(alias, "dono/" + alias[1..], null, Conexao);
    }

    private sealed class CatalogoFalso : ICatalogoDeRepositorios
    {
        public IReadOnlyList<RepositorioCadastrado> Listar() => [new("@app", "/repos/app")];
        public RepositorioCadastrado? Obter(string alias) => Listar().SingleOrDefault(r => r.Alias == alias.ToLowerInvariant());
        public ResolvedRepositoryEnvironment ResolverAmbiente(string alias) => throw new NotSupportedException();
        public IReadOnlyList<RepositorioCadastrado> ResolverVinculos(string alias) => throw new NotSupportedException();
    }

    private sealed class PreferenciasFalsas : IPreferenciasDoAssistente
    {
        public AssistantSettings Atual => AssistantSettings.Default;
        public string? ObterRepositorioAtivo(long idUsuario) => "@app";
    }

    private sealed class Bot : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> mensagens = Channel.CreateUnbounded<string>();
        private long proximo;

        public void Enviar(string texto) => updates.Writer.TryWrite(new TelegramUpdate(Interlocked.Increment(ref proximo),
            new TelegramMessage(new TelegramChat(Usuario), texto, new TelegramUser(Usuario), proximo)));

        public async Task<string> ProximaAsync() =>
            await mensagens.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken) =>
            [await updates.Reader.ReadAsync(cancellationToken)];

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            mensagens.Writer.TryWrite(text);
            return Task.CompletedTask;
        }
    }

    // Qualquer execução de agente seria um pedido de CI que escapou para a conversa.
    private sealed class RunnerQueConta : ICodexRunner, IClaudeRunner
    {
        public int Execucoes { get; private set; }

        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Application.Anexos.Attachment>? attachments = null,
            IReadOnlyList<string>? additionalDirectories = null)
        {
            Execucoes++;
            return Task.FromResult(new AgentProcessResult(AgentProcessStatus.Succeeded, "", "", 0, DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
        }
    }
}
