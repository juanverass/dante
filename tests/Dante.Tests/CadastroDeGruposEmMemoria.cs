using Dante.Application;
using Dante.Application.Agentes;
using Dante.Application.Comum;
using Dante.Application.Execucoes;
using Dante.Application.Grupos;
using Dante.Domain.Grupos;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

// #308: portas do cadastro de grupos em memória, sobre as portas de agentes em memória, para testar o caso de uso e o
// Telegram sem banco. A unidade de trabalho confirma agentes e grupos juntos e recusa, como os índices únicos do banco,
// nome repetido e (grupo, número de versão) repetido; Atualizar de versão ou decisão é recusado como pelo trigger.
internal sealed class CadastroDeGruposEmMemoria(CadastroDeAgentesEmMemoria agentes)
    : IGrupoDeAgentesRepository, IVersaoDoGrupoRepository, IAtivacaoDeVersaoDoGrupoRepository, IUnitOfWorkDaOrquestracao
{
    private readonly object gate = new();
    private readonly List<GrupoDeAgentes> grupos = [];
    private readonly List<VersaoDoGrupo> versoes = [];
    private readonly List<AtivacaoDeVersaoDoGrupo> ativacoes = [];
    private readonly List<object> pendentes = [];

    public CadastroDeAgentesEmMemoria Agentes => agentes;
    public IReadOnlyList<VersaoDoGrupo> Versoes { get { lock (gate) return versoes.ToArray(); } }
    public IReadOnlyList<AtivacaoDeVersaoDoGrupo> Ativacoes { get { lock (gate) return ativacoes.ToArray(); } }

    public ServiceProvider Provider()
    {
        var services = new ServiceCollection().AddApplication();
        services.AddSingleton<IAgenteRepository>(agentes);
        services.AddSingleton<IVersaoDoAgenteRepository>(agentes);
        services.AddSingleton<IGrupoDeAgentesRepository>(this);
        services.AddSingleton<IVersaoDoGrupoRepository>(this);
        services.AddSingleton<IAtivacaoDeVersaoDoGrupoRepository>(this);
        services.AddSingleton<IUnitOfWorkDaOrquestracao>(this);
        services.AddSingleton<IAgentModelCatalog>(new CatalogoDeModelosFixo());
        return services.BuildServiceProvider();
    }

    public Task<GrupoDeAgentes?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (gate) return Task.FromResult(grupos.SingleOrDefault(grupo => grupo.Id == id));
    }

    Task<VersaoDoGrupo?> IRepository<VersaoDoGrupo>.ObterPorIdAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(versoes.SingleOrDefault(versao => versao.Id == id));
    }

    Task<AtivacaoDeVersaoDoGrupo?> IRepository<AtivacaoDeVersaoDoGrupo>.ObterPorIdAsync(Guid id,
        CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(ativacoes.SingleOrDefault(ativacao => ativacao.Id == id));
    }

    public Task AdicionarAsync(GrupoDeAgentes entidade, CancellationToken cancellationToken = default) => Pendente(entidade);
    public Task AdicionarAsync(VersaoDoGrupo entidade, CancellationToken cancellationToken = default) => Pendente(entidade);
    public Task AdicionarAsync(AtivacaoDeVersaoDoGrupo entidade, CancellationToken cancellationToken = default) =>
        Pendente(entidade);

    public void Atualizar(GrupoDeAgentes entidade) { }
    public void Remover(GrupoDeAgentes entidade) => throw new InvalidOperationException("Grupos não são removidos.");
    public void Atualizar(VersaoDoGrupo entidade) => throw new InvalidOperationException("Versões são imutáveis.");
    public void Remover(VersaoDoGrupo entidade) => throw new InvalidOperationException("Versões são imutáveis.");
    public void Atualizar(AtivacaoDeVersaoDoGrupo entidade) => throw new InvalidOperationException("Decisões são imutáveis.");
    public void Remover(AtivacaoDeVersaoDoGrupo entidade) => throw new InvalidOperationException("Decisões são imutáveis.");

    public Task<GrupoDeAgentes?> ObterPorNomeAsync(string nome, CancellationToken cancellationToken = default)
    {
        lock (gate) return Task.FromResult(grupos.SingleOrDefault(grupo => grupo.Nome == nome));
    }

    public Task<IReadOnlyList<GrupoDeAgentes>> ListarAsync(bool incluirArquivados, CancellationToken cancellationToken = default)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<GrupoDeAgentes>>(grupos
                .Where(grupo => incluirArquivados || grupo.Estado != EstadoDoGrupo.Arquivado).ToArray());
    }

    public Task<VersaoDoGrupo?> ObterComParticipantesAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (gate) return Task.FromResult(versoes.SingleOrDefault(versao => versao.Id == id));
    }

    public Task<IReadOnlyList<VersaoDoGrupo>> ListarDoGrupoAsync(Guid idGrupo, CancellationToken cancellationToken = default)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<VersaoDoGrupo>>(versoes.Where(versao => versao.IdGrupo == idGrupo)
                .OrderBy(versao => versao.Numero).ToArray());
    }

    Task<IReadOnlyList<AtivacaoDeVersaoDoGrupo>> IAtivacaoDeVersaoDoGrupoRepository.ListarDoGrupoAsync(Guid idGrupo,
        CancellationToken cancellationToken)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<AtivacaoDeVersaoDoGrupo>>(ativacoes
                .Where(ativacao => ativacao.IdGrupo == idGrupo).OrderBy(ativacao => ativacao.Em).ToArray());
    }

    public async Task SalvarAlteracoesAsync(CancellationToken cancellationToken = default)
    {
        await agentes.SalvarAlteracoesAsync(cancellationToken);
        lock (gate)
        {
            var novos = pendentes.OfType<GrupoDeAgentes>().ToArray();
            var novasVersoes = pendentes.OfType<VersaoDoGrupo>().ToArray();
            var conflito = novos.Any(novo => grupos.Any(grupo => grupo.Nome == novo.Nome)) ||
                novos.Select(novo => novo.Nome).Distinct().Count() != novos.Length ||
                novasVersoes.Any(nova => versoes.Any(versao => versao.IdGrupo == nova.IdGrupo && versao.Numero == nova.Numero));
            if (conflito)
            {
                pendentes.Clear();
                throw new ConflitoDeConcorrenciaException();
            }
            grupos.AddRange(novos);
            versoes.AddRange(novasVersoes);
            ativacoes.AddRange(pendentes.OfType<AtivacaoDeVersaoDoGrupo>());
            pendentes.Clear();
        }
    }

    private Task Pendente(object entidade)
    {
        lock (gate) pendentes.Add(entidade);
        return Task.CompletedTask;
    }
}
