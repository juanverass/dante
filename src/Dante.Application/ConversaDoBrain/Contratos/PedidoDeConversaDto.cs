namespace Dante.Application.ConversaDoBrain;

public sealed record PedidoDeConversaDto(string IdConversa,string Texto,string ReferenciaDaMensagem,string? TrechoSelecionado=null,string? ReferenciaDoTrecho=null);
