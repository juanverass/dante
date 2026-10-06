namespace Dante.Application.ConversaDoBrain;

// IdSessao: sessão do agente ativa no canal, usada só para associar a avaliação de retomada (#148).
public sealed record PedidoDeConversaDto(string IdConversa,string Texto,string ReferenciaDaMensagem,string? TrechoSelecionado=null,string? ReferenciaDoTrecho=null,string? IdSessao=null);
