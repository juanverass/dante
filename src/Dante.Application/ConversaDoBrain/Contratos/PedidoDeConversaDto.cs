namespace Dante.Application.ConversaDoBrain;

// IdSessao: sessão do agente ativa no canal, usada só para associar a avaliação de retomada (#148).
// AutoriaDoTrecho: quem escreveu a mensagem citada; ausência é Desconhecida, nunca o usuário (AD-59 D1).
public sealed record PedidoDeConversaDto(string IdConversa,string Texto,string ReferenciaDaMensagem,string? TrechoSelecionado=null,string? ReferenciaDoTrecho=null,string? IdSessao=null,
    Dante.Domain.Conhecimentos.AutoriaDaFonte AutoriaDoTrecho=Dante.Domain.Conhecimentos.AutoriaDaFonte.Desconhecida);
