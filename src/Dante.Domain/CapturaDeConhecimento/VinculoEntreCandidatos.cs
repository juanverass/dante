using Dante.Domain.Comum;
using Dante.Domain.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;
namespace Dante.Domain.CapturaDeConhecimento;

public enum EstadoDoVinculo { Pendente, Aplicado, Cancelado }

// Intenção de ResolvidoPor entre dois candidatos pendentes (#259, AD-61). Não é relação: só vira
// RelacaoDeConhecimento quando os dois candidatos tiverem sido consolidados pela confirmação do usuário.
// Nunca altera tipo, natureza ou status epistêmico de nenhum dos lados (AD-59).
public sealed class VinculoEntreCandidatos : EntidadeBase
{
    private VinculoEntreCandidatos() { }
    public VinculoEntreCandidatos(CandidatoDeConhecimento incidente, CandidatoDeConhecimento solucao,
        ProvenienciaDoConhecimento proveniencia, DateTimeOffset instante)
    {
        ArgumentNullException.ThrowIfNull(incidente); ArgumentNullException.ThrowIfNull(solucao); ArgumentNullException.ThrowIfNull(proveniencia);
        if (instante == default || incidente.Id == solucao.Id || incidente.IdEspacoDeConhecimento != solucao.IdEspacoDeConhecimento || incidente.IdProjeto != solucao.IdProjeto)
            throw new ArgumentException("Vínculo exige candidatos distintos no mesmo escopo.");
        if (incidente.Estado != EstadoDoCandidato.Pendente || solucao.Estado != EstadoDoCandidato.Pendente)
            throw new InvalidOperationException("Vínculo exige candidatos pendentes.");
        if (!RelacaoDeConhecimento.TiposCompativeis(TipoDeRelacao.ResolvidoPor, incidente.Tipo, solucao.Tipo))
            throw new ArgumentException("Vínculo ResolvidoPor exige candidato Incidente → candidato Solucao.");
        IdEspacoDeConhecimento = incidente.IdEspacoDeConhecimento; IdProjeto = incidente.IdProjeto;
        IdCandidatoIncidente = incidente.Id; IdCandidatoSolucao = solucao.Id;
        Proveniencia = proveniencia; CriadoEm = AtualizadoEm = instante; Estado = EstadoDoVinculo.Pendente;
    }
    public Guid IdEspacoDeConhecimento { get; private set; }
    public Guid? IdProjeto { get; private set; }
    public Guid IdCandidatoIncidente { get; private set; }
    public Guid IdCandidatoSolucao { get; private set; }
    // Preenchidos quando cada lado é consolidado; cada consolidação grava a linha, de modo que
    // confirmações concorrentes dos dois lados conflitam pelo token de concorrência em vez de deixarem a intenção órfã.
    public Guid? IdConhecimentoIncidente { get; private set; }
    public Guid? IdConhecimentoSolucao { get; private set; }
    // Revisão de cada conhecimento no ato da consolidação: a efetivação exige a mesma versão (revalidação de revisões).
    public int? RevisaoConhecimentoIncidente { get; private set; }
    public int? RevisaoConhecimentoSolucao { get; private set; }
    public Guid? IdRelacao { get; private set; }
    public EstadoDoVinculo Estado { get; private set; }
    public string? MotivoDoEncerramento { get; private set; }
    public ProvenienciaDoConhecimento Proveniencia { get; private set; } = null!;
    public ProvenienciaDoConhecimento? ProvenienciaDoEncerramento { get; private set; }
    public DateTimeOffset CriadoEm { get; private set; }
    public DateTimeOffset AtualizadoEm { get; private set; }

    // Registra o conhecimento que consolidou um dos candidatos; verdadeiro quando os dois lados já são conhecimentos.
    public bool RegistrarConsolidacao(Guid idCandidato, Guid idConhecimento, int revisao, DateTimeOffset instante)
    {
        GarantirPendente(instante);
        if (idConhecimento == Guid.Empty || revisao < 1) throw new ArgumentException("Conhecimento consolidado inválido.");
        if (idCandidato == IdCandidatoIncidente) (IdConhecimentoIncidente, RevisaoConhecimentoIncidente) = (idConhecimento, revisao);
        else if (idCandidato == IdCandidatoSolucao) (IdConhecimentoSolucao, RevisaoConhecimentoSolucao) = (idConhecimento, revisao);
        else throw new ArgumentException("Candidato não pertence ao vínculo.");
        AtualizadoEm = instante;
        return IdConhecimentoIncidente is not null && IdConhecimentoSolucao is not null;
    }
    // Regra única de efetivação (AD-61, aprovada): os dois lados foram consolidados pelo usuário (promoção do candidato),
    // continuam na revisão da consolidação, ativos, no escopo do vínculo, com sensibilidade de agente (como brain_criar_relacao)
    // e mantêm Incidente → Solucao. O status epistêmico não é exigido nem alterado: conclusões do agente consolidadas
    // permanecem Inferido (AD-59 I1) e ainda assim podem ser relacionadas.
    public bool PodeEfetivar(Conhecimento? incidente, Conhecimento? solucao) =>
        incidente is not null && solucao is not null && incidente.Id == IdConhecimentoIncidente && solucao.Id == IdConhecimentoSolucao &&
        incidente.Revisao == RevisaoConhecimentoIncidente && solucao.Revisao == RevisaoConhecimentoSolucao &&
        incidente.Sensibilidade <= Sensibilidade.Trabalho && solucao.Sensibilidade <= Sensibilidade.Trabalho &&
        incidente.IdEspacoDeConhecimento == IdEspacoDeConhecimento && solucao.IdEspacoDeConhecimento == IdEspacoDeConhecimento &&
        incidente.IdProjeto == IdProjeto && solucao.IdProjeto == IdProjeto &&
        RelacaoDeConhecimento.TiposCompativeis(TipoDeRelacao.ResolvidoPor, incidente.Tipo, solucao.Tipo) &&
        incidente.Status is not (StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido) &&
        solucao.Status is not (StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido);
    public void Aplicar(Guid idRelacao, ProvenienciaDoConhecimento responsavel, DateTimeOffset instante)
    {
        GarantirPendente(instante); ArgumentNullException.ThrowIfNull(responsavel);
        if (idRelacao == Guid.Empty || IdConhecimentoIncidente is null || IdConhecimentoSolucao is null)
            throw new InvalidOperationException("Vínculo só é aplicado com os dois lados consolidados.");
        IdRelacao = idRelacao; Estado = EstadoDoVinculo.Aplicado; ProvenienciaDoEncerramento = responsavel; AtualizadoEm = instante;
    }
    public void Cancelar(string motivo, ProvenienciaDoConhecimento responsavel, DateTimeOffset instante)
    {
        GarantirPendente(instante); ArgumentNullException.ThrowIfNull(responsavel);
        if (string.IsNullOrWhiteSpace(motivo) || motivo.Length > 500) throw new ArgumentException("Motivo do cancelamento inválido.");
        Estado = EstadoDoVinculo.Cancelado; MotivoDoEncerramento = motivo.Trim(); ProvenienciaDoEncerramento = responsavel; AtualizadoEm = instante;
    }
    private void GarantirPendente(DateTimeOffset instante)
    {
        if (Estado != EstadoDoVinculo.Pendente) throw new InvalidOperationException("Vínculo já encerrado.");
        if (instante == default || instante < AtualizadoEm) throw new ArgumentException("Timestamp anterior ao último ato do vínculo.");
    }
}
