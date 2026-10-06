namespace Dante.Application.ConversaDoBrain;

public sealed record AlteracaoPendenteDto(string Acao,AlvoDeConversaDto? Alvo=null,AlvoDeConversaDto? SegundoAlvo=null,string? Conteudo=null,DateTimeOffset? ExpiraEm=null);
