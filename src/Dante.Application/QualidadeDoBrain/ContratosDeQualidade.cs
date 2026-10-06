using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;
namespace Dante.Application.QualidadeDoBrain;

public enum ProblemaDeQualidade { PossivelDuplicata, PossivelContradicao, ContradicaoExplicita, ForaDaValidade,
    InativoOuSubstituido, SemFonteReferenciada, Orfao, ConfirmacaoAnteriorAoLimite }
public sealed record AchadoDeQualidadeDto(ProblemaDeQualidade Problema, Guid IdConhecimento, Guid? IdRelacionado, string Motivo);
public sealed record ConflitoDeConhecimentoDto(Guid IdRelacao, Guid IdOrigem, Guid IdDestino, Guid? IdEscolhido, DateTimeOffset? ResolvidoEm);
public sealed record RelatorioDeQualidadeDto(IReadOnlyList<AchadoDeQualidadeDto> Achados,
    IReadOnlyList<ConflitoDeConhecimentoDto> Conflitos, bool LimiteAtingido, int ItensExaminados);
public sealed record RevisaoEsperadaDto(Guid IdConhecimento, int Revisao);
public interface IConsultaDeQualidade
{
    Task<IReadOnlyList<Conhecimento>> ListarAsync(AcessoAoBrain acesso, int deslocamento, int limite, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RelacaoDeConhecimento>> ListarRelacoesAsync(AcessoAoBrain acesso, IReadOnlyList<Guid> ids, int limite, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Conhecimento>> ElegiveisParaContextoAsync(AcessoAoBrain acesso, DateTimeOffset instante, int limite, CancellationToken cancellationToken = default);
}
