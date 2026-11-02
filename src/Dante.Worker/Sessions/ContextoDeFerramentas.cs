namespace Dante.Worker.Sessions;

// Metadados de entrada autenticada do host. Não são argumentos enviados pelo modelo às ferramentas.
// AutorDoTrecho: autor da mensagem citada; preservado para que o Brain não trate texto do bot como do usuário (#264).
public sealed record ContextoDeFerramentas(long Usuario, long Chat, long? Topico, long Mensagem, string? Texto, string? TrechoSelecionado,
    long? AutorDoTrecho = null, bool AutorDoTrechoEhBot = false);
