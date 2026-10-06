namespace Dante.Application.ConversaDoBrain;

public sealed record EstadoDeConversaDto(Guid Versao,IReadOnlyList<AlvoDeConversaDto> Resultados,AlteracaoPendenteDto? Pendente,string? UltimoTermo,DateTimeOffset AtualizadoEm,bool ContextoDeAlteracao=false,bool ConfirmacaoExpirada=false);
