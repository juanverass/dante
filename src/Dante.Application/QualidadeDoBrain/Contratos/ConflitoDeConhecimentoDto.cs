namespace Dante.Application.QualidadeDoBrain;

public sealed record ConflitoDeConhecimentoDto(Guid IdRelacao, Guid IdOrigem, Guid IdDestino, Guid? IdEscolhido, DateTimeOffset? ResolvidoEm);
