using Dante.Application;
using Dante.Application.Contextos;
using Dante.Application.IntegracaoDeCi;
using Dante.Domain.Preferencias;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

// #238 (AD-77): acompanhamento do CI de um PR na Application, neutro de interface. Só fakes das portas: a resolução do
// repositório (explícito > sessão > ativo), a leitura do head SHA atual antes da consulta, a correlação só com esse SHA
// e as falhas que nunca viram "o CI falhou".
public sealed class AcompanhamentoDeCiTests
{
    private const string Head = "1111111111111111111111111111111111111111";
    private const string Antigo = "2222222222222222222222222222222222222222";
    private static readonly DateTimeOffset Agora = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly ConexaoDeCi Conexao = new("pessoal", "secret://host/DANTE_CI_TOKEN");
    private static readonly ConexaoDeCi OutraConexao = new("cliente", "secret://host/CLIENTE_CI_TOKEN");

    private readonly ProvedorFalso provedor = new();
    private readonly RepositoriosFalsos repositorios = new();
    private readonly CatalogoFalso catalogo = new("@app", "@api", "@lib");
    private readonly PreferenciasFalsas preferencias = new();

    [Fact]
    public async Task LeOHeadAtualDoPrEConsultaSoEsseSha()
    {
        provedor.PullRequest = Pr("@app", 12, Head);
        provedor.Execucoes = [Execucao("@app", 10, Head, EstadoDaExecucao.Sucesso), Execucao("@app", 9, Antigo, EstadoDaExecucao.Falha)];

        var situacao = await Servico().ConsultarPullRequestAsync(new PedidoDeCiDoPullRequestDto(1, 12, "@app"));

        Assert.Equal(("@app", OrigemDoRepositorioDeCi.Explicito), (situacao.Alias, situacao.Origem));
        var consulta = Assert.Single(provedor.Consultas);
        Assert.Equal((Head, 12, 42L), (consulta.ShaDoHead, consulta.NumeroDoPr, consulta.Repositorio.IdNoProvedor));
        Assert.Equal(EstadoDoCheckDeCi.Sucesso, situacao.Check!.Estado);
        Assert.Equal(1, situacao.Check.Descartadas);
        Assert.Null(situacao.ProblemaDoRepositorio);
        Assert.Null(situacao.FalhaDoPullRequest);
    }

    [Fact]
    public async Task CommitNovoNoPrNaoHerdaOSucessoDoCommitAnterior()
    {
        provedor.Execucoes = [Execucao("@app", 10, Antigo, EstadoDaExecucao.Sucesso)];
        provedor.PullRequest = Pr("@app", 12, Antigo);
        var servico = Servico();
        Assert.Equal(EstadoDoCheckDeCi.Sucesso,
            (await servico.ConsultarPullRequestAsync(new PedidoDeCiDoPullRequestDto(1, 12, "@app"))).Check!.Estado);

        provedor.PullRequest = Pr("@app", 12, Head);
        var semExecucao = await servico.ConsultarPullRequestAsync(new PedidoDeCiDoPullRequestDto(1, 12, "@app"));
        Assert.Equal(EstadoDoCheckDeCi.SemExecucao, semExecucao.Check!.Estado);

        provedor.Execucoes = [Execucao("@app", 10, Antigo, EstadoDaExecucao.Sucesso), Execucao("@app", 11, Head, EstadoDaExecucao.EmAndamento)];
        var andamento = await servico.ConsultarPullRequestAsync(new PedidoDeCiDoPullRequestDto(1, 12, "@app"));
        Assert.Equal(EstadoDoCheckDeCi.EmAndamento, andamento.Check!.Estado);
        Assert.Equal([11L], andamento.Check.Execucoes.Select(e => e.Identidade.IdDaExecucao));
    }

    [Fact]
    public async Task RepositorioVemDoAliasExplicitoDaSessaoOuDoAtivoNessaOrdem()
    {
        preferencias.Ativo = "@api";
        provedor.PullRequest = null;
        var servico = Servico();

        Assert.Equal(("@lib", OrigemDoRepositorioDeCi.Explicito),
            Origem(await servico.ConsultarPullRequestAsync(new PedidoDeCiDoPullRequestDto(1, 5, "@lib", "@app"))));
        Assert.Equal(("@app", OrigemDoRepositorioDeCi.Sessao),
            Origem(await servico.ConsultarPullRequestAsync(new PedidoDeCiDoPullRequestDto(1, 5, null, "@app"))));
        Assert.Equal(("@api", OrigemDoRepositorioDeCi.Ativo),
            Origem(await servico.ConsultarPullRequestAsync(new PedidoDeCiDoPullRequestDto(1, 5))));
        Assert.Equal(["@lib", "@app", "@api"], repositorios.Resolvidos);

        static (string?, OrigemDoRepositorioDeCi?) Origem(SituacaoDoCiDoPullRequestDto s) => (s.Alias, s.Origem);
    }

    [Theory]
    [InlineData(null, null, ProblemaDoRepositorioDeCi.SemRepositorio)]
    [InlineData("app", null, ProblemaDoRepositorioDeCi.AliasInvalido)]
    [InlineData("@web", null, ProblemaDoRepositorioDeCi.NaoCadastrado)]
    [InlineData(null, "@removido", ProblemaDoRepositorioDeCi.AtivoForaDoCadastro)]
    public async Task SemRepositorioCadastradoNadaEConsultado(string? explicito, string? ativo, ProblemaDoRepositorioDeCi problema)
    {
        preferencias.Ativo = ativo;

        var situacao = await Servico().ConsultarPullRequestAsync(new PedidoDeCiDoPullRequestDto(1, 12, explicito));

        Assert.Equal(problema, situacao.ProblemaDoRepositorio);
        Assert.Empty(repositorios.Resolvidos);
        Assert.Equal(0, provedor.Chamadas);
    }

    [Theory]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.NaoConfigurada)]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.NaoAutorizada)]
    public async Task SemConexaoQueLibereOAliasOPrNaoELido(MotivoDaFalhaNaConsultaDeCi motivo)
    {
        // Um vinculado ao @app sem conexão própria: a identidade não é herdada.
        repositorios.Falhas["@lib"] = motivo;

        var situacao = await Servico().ConsultarPullRequestAsync(new PedidoDeCiDoPullRequestDto(1, 12, "@lib", "@app"));

        Assert.Equal(motivo, situacao.FalhaDoPullRequest);
        Assert.Null(situacao.PullRequest);
        Assert.Null(situacao.Check);
        Assert.Equal(0, provedor.Chamadas);
    }

    [Theory]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.NaoEncontrada)]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.SemPermissao)]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.Indisponivel)]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.LimiteDeTaxa)]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.Inconsistente)]
    public async Task FalhaAoLerOPrNaoConsultaExecucoes(MotivoDaFalhaNaConsultaDeCi motivo)
    {
        provedor.FalhaDoPr = new FalhaNaConsultaDeCiException(motivo, "falha", TimeSpan.FromSeconds(90));

        var situacao = await Servico().ConsultarPullRequestAsync(new PedidoDeCiDoPullRequestDto(1, 12, "@app"));

        Assert.Equal(motivo, situacao.FalhaDoPullRequest);
        Assert.Equal(TimeSpan.FromSeconds(90), situacao.RepetirApos);
        Assert.Empty(provedor.Consultas);
    }

    [Fact]
    public async Task PrDeOutroRepositorioOuConexaoOuNumeroERecusadoMesmoSeOAdapterDeixarPassar()
    {
        foreach (var errado in new[]
                 {
                     Pr("@api", 12, Head),
                     new PullRequestDeCiDto(new RepositorioDeCi("@app", "dono/app", 42, OutraConexao), 12, Head,
                         EstadoDoPullRequestDeCi.Aberto, false, false, "t", new Uri("https://github.com/dono/app/pull/12")),
                     Pr("@app", 13, Head)
                 })
        {
            provedor.PullRequest = errado;
            var situacao = await Servico().ConsultarPullRequestAsync(new PedidoDeCiDoPullRequestDto(1, 12, "@app"));
            Assert.Equal(MotivoDaFalhaNaConsultaDeCi.Inconsistente, situacao.FalhaDoPullRequest);
        }
        Assert.Empty(provedor.Consultas);
    }

    [Theory]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.SemPermissao, EstadoDoCheckDeCi.SemPermissao)]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.NaoEncontrada, EstadoDoCheckDeCi.SemPermissao)]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.Indisponivel, EstadoDoCheckDeCi.Indisponivel)]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.LimiteDeTaxa, EstadoDoCheckDeCi.Indisponivel)]
    [InlineData(MotivoDaFalhaNaConsultaDeCi.Inconsistente, EstadoDoCheckDeCi.Indisponivel)]
    public async Task FalhaAoConsultarExecucoesNuncaViraFalhaDoCi(MotivoDaFalhaNaConsultaDeCi motivo, EstadoDoCheckDeCi estado)
    {
        provedor.FalhaDaConsulta = new FalhaNaConsultaDeCiException(motivo, "falha");

        var situacao = await Servico().ConsultarPullRequestAsync(new PedidoDeCiDoPullRequestDto(1, 12, "@app"));

        Assert.NotNull(situacao.PullRequest);
        Assert.Equal(estado, situacao.Check!.Estado);
        Assert.Equal(motivo, situacao.Check.MotivoDaIndisponibilidade);
        Assert.Equal(Agora, situacao.Check.ObtidoEm);
    }

    [Fact]
    public async Task ExecucaoDeOutroRepositorioRecusaARespostaInteira()
    {
        provedor.Execucoes = [Execucao("@app", 10, Head, EstadoDaExecucao.Sucesso), Execucao("@api", 11, Head, EstadoDaExecucao.Sucesso)];

        var situacao = await Servico().ConsultarPullRequestAsync(new PedidoDeCiDoPullRequestDto(1, 12, "@app"));

        Assert.Equal(EstadoDoCheckDeCi.Indisponivel, situacao.Check!.Estado);
        Assert.Equal(MotivoDaFalhaNaConsultaDeCi.Inconsistente, situacao.Check.MotivoDaIndisponibilidade);
    }

    [Fact]
    public async Task NumeroDePrInvalidoERecusado() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Servico().ConsultarPullRequestAsync(new PedidoDeCiDoPullRequestDto(1, 0, "@app")));

    [Fact]
    public void ComposicaoDaApplicationRegistraOCasoDeUsoComAsPortasDaInfrastructure()
    {
        var services = new ServiceCollection().AddApplication();
        services.AddSingleton<IProvedorDeCi>(provedor);
        services.AddSingleton<IRepositoriosDeCi>(repositorios);
        services.AddSingleton<ICatalogoDeRepositorios>(catalogo);
        using var provider = services.BuildServiceProvider();
        using var escopo = provider.CreateScope();

        Assert.IsType<AcompanhamentoDeCiAppService>(escopo.ServiceProvider.GetRequiredService<IAcompanhamentoDeCiAppService>());
    }

    private AcompanhamentoDeCiAppService Servico() =>
        new(provedor, repositorios, catalogo, preferencias, new RelogioFixo(Agora));

    private static RepositorioDeCi Repositorio(string alias, long? id = null) =>
        new(alias, "dono/" + alias[1..], id, Conexao);

    private static PullRequestDeCiDto Pr(string alias, int numero, string sha) =>
        new(Repositorio(alias, 42), numero, sha, EstadoDoPullRequestDeCi.Aberto, false, false, "Título",
            new Uri($"https://github.com/dono/{alias[1..]}/pull/{numero}"));

    private static ExecucaoDeCiDto Execucao(string alias, long id, string sha, EstadoDaExecucao estado) => new()
    {
        Identidade = new IdentidadeDaExecucaoDeCi(Repositorio(alias, alias == "@app" ? 42 : 43), 7, id, 1, sha),
        NomeDoWorkflow = "CI",
        Gatilho = "pull_request",
        Estado = estado,
        UrlOficial = new Uri($"https://github.com/dono/{alias[1..]}/actions/runs/{id}"),
        CriadaEm = Agora.AddMinutes(-id),
        AtualizadaEm = Agora
    };

    private sealed class ProvedorFalso : IProvedorDeCi
    {
        public PullRequestDeCiDto? PullRequest { get; set; } = Pr("@app", 12, Head);
        public IReadOnlyList<ExecucaoDeCiDto> Execucoes { get; set; } = [];
        public FalhaNaConsultaDeCiException? FalhaDoPr { get; set; }
        public FalhaNaConsultaDeCiException? FalhaDaConsulta { get; set; }
        public List<ConsultaDeCi> Consultas { get; } = [];
        public int Chamadas { get; private set; }

        public Task<PullRequestDeCiDto> ObterPullRequestAsync(RepositorioDeCi repositorio, int numero,
            CancellationToken cancellationToken = default)
        {
            Chamadas++;
            if (FalhaDoPr is not null) throw FalhaDoPr;
            return Task.FromResult(PullRequest ?? new PullRequestDeCiDto(
                new RepositorioDeCi(repositorio.Alias, repositorio.NomeCompleto, 42, repositorio.Conexao), numero, Head,
                EstadoDoPullRequestDeCi.Aberto, false, false, "t", new Uri($"https://github.com/{repositorio.NomeCompleto}/pull/{numero}")));
        }

        public Task<ResultadoDaConsultaDeCi> ConsultarAsync(ConsultaDeCi consulta, CancellationToken cancellationToken = default)
        {
            Chamadas++;
            Consultas.Add(consulta);
            if (FalhaDaConsulta is not null) throw FalhaDaConsulta;
            return Task.FromResult(new ResultadoDaConsultaDeCi
            {
                PossuiWorkflowsConfigurados = true,
                Completo = true,
                ObtidoEm = Agora,
                Execucoes = Execucoes
            });
        }

        public Task<ExecucaoDeCiDto> ObterExecucaoAsync(IdentidadeDaExecucaoDeCi execucao,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LogDeJobDeCiDto> ObterLogDoJobAsync(IdentidadeDoJobDeCi job, int limiteEmBytes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RepositoriosFalsos : IRepositoriosDeCi
    {
        public Dictionary<string, MotivoDaFalhaNaConsultaDeCi> Falhas { get; } = [];
        public List<string> Resolvidos { get; } = [];

        public RepositorioDeCi Resolver(string alias)
        {
            Resolvidos.Add(alias);
            return Falhas.TryGetValue(alias, out var motivo)
                ? throw new FalhaNaConsultaDeCiException(motivo, "sem conexão")
                : Repositorio(alias);
        }
    }

    private sealed class CatalogoFalso(params string[] aliases) : ICatalogoDeRepositorios
    {
        public IReadOnlyList<RepositorioCadastrado> Listar() => aliases.Select(a => new RepositorioCadastrado(a, "/repos/" + a[1..])).ToArray();

        public RepositorioCadastrado? Obter(string alias) => !alias.StartsWith('@')
            ? throw new ArgumentException("Alias inválido.", nameof(alias))
            : Listar().SingleOrDefault(r => r.Alias == alias.ToLowerInvariant());

        public ResolvedRepositoryEnvironment ResolverAmbiente(string alias) => throw new NotSupportedException();
        public IReadOnlyList<RepositorioCadastrado> ResolverVinculos(string alias) => throw new NotSupportedException();
    }

    private sealed class PreferenciasFalsas : IPreferenciasDoAssistente
    {
        public string? Ativo { get; set; }
        public AssistantSettings Atual => AssistantSettings.Default;
        public string? ObterRepositorioAtivo(long idUsuario) => Ativo;
    }

    private sealed class RelogioFixo(DateTimeOffset agora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => agora;
    }
}
