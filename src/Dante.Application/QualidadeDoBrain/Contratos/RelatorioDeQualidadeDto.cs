namespace Dante.Application.QualidadeDoBrain;

public sealed record RelatorioDeQualidadeDto(IReadOnlyList<AchadoDeQualidadeDto> Achados,
    IReadOnlyList<ConflitoDeConhecimentoDto> Conflitos, bool LimiteAtingido, int ItensExaminados);
