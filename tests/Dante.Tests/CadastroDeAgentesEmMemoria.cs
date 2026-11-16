using Dante.Application;
using Dante.Application.Agentes;
using Dante.Application.Comum;
using Dante.Application.Execucoes;
using Dante.Domain.Agentes;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

// #301: portas do cadastro de agentes em memória, para testar o caso de uso e o Telegram sem banco. A unidade de
// trabalho só publica o que foi adicionado ao confirmar, e recusa handle repetido como o índice único do banco.
internal sealed class CadastroDeAgentesEmMemoria : IAgenteRepository, IVersaoDoAgenteRepository, IUnitOfWorkDaOrquestracao
{
    private readonly object gate = new();
    private readonly List<Agente> agentes = [];
    private readonly List<VersaoDoAgente> versoes = [];
    private readonly List<object> pendentes = [];

    public int TrabalhoAtivo { get; set; }
    public int Confirmacoes { get; private set; }
    public IReadOnlyList<VersaoDoAgente> Versoes { get { lock (gate) return versoes.ToArray(); } }

    // Composição usada pelos testes do Telegram: o caso de uso real sobre estas portas.
    public ServiceProvider Provider(IAgentModelCatalog? catalogo = null)
    {
        var services = new ServiceCollection().AddApplication();
        services.AddSingleton<IAgenteRepository>(this);
        services.AddSingleton<IVersaoDoAgenteRepository>(this);
        services.AddSingleton<IUnitOfWorkDaOrquestracao>(this);
        if (catalogo is not null) services.AddSingleton(catalogo);
        return services.BuildServiceProvider();
    }

    public Task<Agente?> ObterPorIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (gate) return Task.FromResult(agentes.SingleOrDefault(agente => agente.Id == id));
    }

    Task<VersaoDoAgente?> IRepository<VersaoDoAgente>.ObterPorIdAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(versoes.SingleOrDefault(versao => versao.Id == id));
    }

    public Task AdicionarAsync(Agente entidade, CancellationToken cancellationToken = default)
    {
        lock (gate) pendentes.Add(entidade);
        return Task.CompletedTask;
    }

    public Task AdicionarAsync(VersaoDoAgente entidade, CancellationToken cancellationToken = default)
    {
        lock (gate) pendentes.Add(entidade);
        return Task.CompletedTask;
    }

    public void Atualizar(Agente entidade) { }
    public void Remover(Agente entidade) => throw new InvalidOperationException("Agentes não são removidos.");
    public void Atualizar(VersaoDoAgente entidade) => throw new InvalidOperationException("Versões são imutáveis.");
    public void Remover(VersaoDoAgente entidade) => throw new InvalidOperationException("Versões são imutáveis.");

    public Task<Agente?> ObterPorHandleAsync(string handle, CancellationToken cancellationToken = default)
    {
        lock (gate) return Task.FromResult(agentes.SingleOrDefault(agente => agente.Handle == handle));
    }

    public Task<IReadOnlyList<Agente>> ListarAsync(bool incluirArquivados, CancellationToken cancellationToken = default)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<Agente>>(agentes
                .Where(agente => incluirArquivados || agente.Estado != EstadoDoAgente.Arquivado).ToArray());
    }

    public Task<int> ContarTrabalhoAtivoAsync(Guid idAgente, CancellationToken cancellationToken = default) =>
        Task.FromResult(TrabalhoAtivo);

    public Task<IReadOnlyList<VersaoDoAgente>> ListarDoAgenteAsync(Guid idAgente, CancellationToken cancellationToken = default)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<VersaoDoAgente>>(versoes.Where(versao => versao.IdAgente == idAgente)
                .OrderBy(versao => versao.Numero).ToArray());
    }

    public Task SalvarAlteracoesAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var novos = pendentes.OfType<Agente>().ToArray();
            if (novos.Any(novo => agentes.Any(agente => agente.Handle == novo.Handle)) ||
                novos.Select(novo => novo.Handle).Distinct().Count() != novos.Length)
            {
                pendentes.Clear();
                throw new ConflitoDeConcorrenciaException();
            }
            agentes.AddRange(novos);
            versoes.AddRange(pendentes.OfType<VersaoDoAgente>());
            pendentes.Clear();
            Confirmacoes++;
        }
        return Task.CompletedTask;
    }
}

// Catálogo fixo: Claude com opus (padrão, low/high) e sonnet (low); Codex com fake-mini (padrão, low/xhigh).
internal sealed class CatalogoDeModelosFixo : IAgentModelCatalog
{
    public bool Falha { get; set; }
    public int Consultas { get; private set; }

    public Task<IReadOnlyList<AgentModelInfo>> GetModelsAsync(AgentKind agent, CancellationToken cancellationToken = default)
    {
        Consultas++;
        if (Falha) throw new AgentModelCatalogException("catálogo fora");
        IReadOnlyList<AgentModelInfo> modelos = agent == AgentKind.Claude
            ? [new("opus", "Opus", true, ["low", "high"], "claude-opus-5-5"), new("sonnet", "Sonnet", false, ["low"])]
            : [new("fake-mini", "Mini", true, ["low", "xhigh"])];
        return Task.FromResult(modelos);
    }
}
