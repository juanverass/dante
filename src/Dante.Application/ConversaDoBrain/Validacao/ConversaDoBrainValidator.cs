namespace Dante.Application.ConversaDoBrain;

internal static class ConversaDoBrainValidator
{
    internal static void ValidarPedido(PedidoDeConversaDto pedido)
    {
        if (string.IsNullOrWhiteSpace(pedido.IdConversa) || pedido.IdConversa.Length > 200 || pedido.Texto.Length > 10000 ||
            pedido.ReferenciaDaMensagem.Length > 2000)
            throw new ArgumentException("Conversa inválida.");
    }
}
