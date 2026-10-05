using Dante.Domain.Comum;
using Dante.Domain.Conhecimentos;
namespace Dante.Domain.RelacoesDeConhecimento;

public enum TipoDeRelacao { RelacionadoA, PertenceA, Substitui, DerivadoDe, ResolvidoPor, ProduziuAprendizado, DependeDe, Referencia, Contradiz }

public sealed class RelacaoDeConhecimento : EntidadeBase
{
    private RelacaoDeConhecimento() { }
    public RelacaoDeConhecimento(Conhecimento origem, Conhecimento destino, TipoDeRelacao tipo,
        ProvenienciaDoConhecimento proveniencia, DateTimeOffset instante)
    {
        ArgumentNullException.ThrowIfNull(origem); ArgumentNullException.ThrowIfNull(destino);
        ArgumentNullException.ThrowIfNull(proveniencia);
        if (!Enum.IsDefined(tipo) || instante == default) throw new ArgumentException("Relação inválida.");
        if (origem.Id == destino.Id || origem.IdEspacoDeConhecimento != destino.IdEspacoDeConhecimento || origem.IdProjeto != destino.IdProjeto)
            throw new ArgumentException("Relação exige itens distintos no mesmo escopo.");
        if (tipo == TipoDeRelacao.ResolvidoPor && (origem.Tipo != TipoDeConhecimento.Incidente || destino.Tipo != TipoDeConhecimento.Solucao) ||
            tipo == TipoDeRelacao.ProduziuAprendizado && (origem.Tipo != TipoDeConhecimento.Solucao || destino.Tipo != TipoDeConhecimento.Aprendizado))
            throw new ArgumentException("Tipos dos itens não correspondem à semântica da relação.");
        if (tipo == TipoDeRelacao.Substitui && (destino.IdConhecimentoSubstituto != origem.Id || destino.Status != StatusDoConhecimento.Substituido))
            throw new ArgumentException("Relação de substituição exige ato canônico de substituição.");
        if (origem.Status is StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido ||
            destino.Status == StatusDoConhecimento.Inativo || (destino.Status == StatusDoConhecimento.Substituido && tipo != TipoDeRelacao.Substitui))
            throw new ArgumentException("Novas relações exigem itens ativos, salvo destino substituído rastreável.");
        IdEspacoDeConhecimento = origem.IdEspacoDeConhecimento; IdProjeto = origem.IdProjeto;
        IdOrigem = origem.Id; IdDestino = destino.Id; Tipo = tipo;
        if (tipo is TipoDeRelacao.RelacionadoA or TipoDeRelacao.Contradiz && IdOrigem.CompareTo(IdDestino) > 0)
            (IdOrigem, IdDestino) = (IdDestino, IdOrigem);
        Proveniencia = proveniencia; CriadaEm = instante;
    }
    public Guid? IdConhecimentoEscolhido { get; private set; }
    public DateTimeOffset? ResolvidaEm { get; private set; }
    public ProvenienciaDoConhecimento? ProvenienciaDaResolucao { get; private set; }
    public void ResolverContradicao(Conhecimento escolhido, ProvenienciaDoConhecimento responsavel, DateTimeOffset instante)
    {
        ArgumentNullException.ThrowIfNull(escolhido); ArgumentNullException.ThrowIfNull(responsavel);
        if (Tipo != TipoDeRelacao.Contradiz || ResolvidaEm is not null || instante < CriadaEm ||
            (escolhido.Id != IdOrigem && escolhido.Id != IdDestino) || escolhido.IdEspacoDeConhecimento != IdEspacoDeConhecimento ||
            escolhido.IdProjeto != IdProjeto || escolhido.Status != StatusDoConhecimento.Confirmado || !escolhido.EstaValidoEm(instante))
            throw new InvalidOperationException("Resolução exige decisão explícita sobre item confirmado e válido do conflito.");
        IdConhecimentoEscolhido = escolhido.Id; ResolvidaEm = instante; ProvenienciaDaResolucao = responsavel;
    }
    public Guid IdEspacoDeConhecimento { get; private set; }
    public Guid? IdProjeto { get; private set; }
    public Guid IdOrigem { get; private set; }
    public Guid IdDestino { get; private set; }
    public TipoDeRelacao Tipo { get; private set; }
    public ProvenienciaDoConhecimento Proveniencia { get; private set; } = null!;
    public DateTimeOffset CriadaEm { get; private set; }
}
