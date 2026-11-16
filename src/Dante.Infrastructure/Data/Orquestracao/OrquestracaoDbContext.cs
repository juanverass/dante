using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Data.Orquestracao;

// Ledger de execuções (AD-66): mesmo PostgreSQL do Brain, schema e histórico de migrations próprios. Não usa
// AutorizacaoDoBrain, FiltrosDoBrain nem ValidacaoDeEscritaDoBrain: execuções e sessões não são conhecimento.
public sealed class OrquestracaoDbContext(DbContextOptions<OrquestracaoDbContext> options) : DbContext(options)
{
    public const string Schema = "orquestracao";
    public const string TabelaDeHistorico = "__historico_de_migrations";
    public const string SequenciaDeExecucoes = "seq_numero_da_execucao";
    public const string SequenciaDeSessoes = "seq_numero_da_sessao";
    public const string SequenciaDeEventos = "seq_eventos";
    private const string NamespaceDosMappings = "Dante.Infrastructure.Modulos.Execucoes";
    // Cadastro de agentes (AD-68): mesmo schema e mesmo histórico de migrations do ledger.
    private const string NamespaceDosMappingsDeAgentes = "Dante.Infrastructure.Modulos.Agentes";

    // Os mappings do ledger vivem no mesmo assembly do Brain; cada contexto carrega só os seus.
    internal static bool PertenceAoLedger(Type tipo) =>
        tipo.Namespace is NamespaceDosMappings or NamespaceDosMappingsDeAgentes;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        // Sequências persistentes: J…/E… e S… nunca se repetem, nem depois de reiniciar o Worker.
        modelBuilder.HasSequence<long>(SequenciaDeExecucoes);
        modelBuilder.HasSequence<long>(SequenciaDeSessoes);
        modelBuilder.HasSequence<long>(SequenciaDeEventos);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrquestracaoDbContext).Assembly, PertenceAoLedger);
    }
}
