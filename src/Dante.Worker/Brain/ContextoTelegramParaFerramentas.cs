using Dante.Worker.Sessions;
using Dante.Worker.Telegram;

namespace Dante.Worker.Brain;

public static class ContextoTelegramParaFerramentas
{
    public static ContextoDeFerramentas ParaFerramentas(this TelegramMessage mensagem) => new(mensagem.From!.Id,
        mensagem.Chat.Id, mensagem.MessageThreadId, mensagem.MessageId, mensagem.Text ?? mensagem.Caption,
        mensagem.ReplyToMessage is { } reply && reply.Chat.Id == mensagem.Chat.Id && reply.MessageThreadId == mensagem.MessageThreadId ? reply.Text : null);
    public static TelegramMessage ParaMensagem(this ContextoDeFerramentas contexto) => new(new(contexto.Chat), contexto.Texto,
        new(contexto.Usuario), contexto.Mensagem, ReplyToMessage: contexto.TrechoSelecionado is null ? null :
            new(new(contexto.Chat), contexto.TrechoSelecionado, null, MessageThreadId: contexto.Topico), MessageThreadId: contexto.Topico);
}
