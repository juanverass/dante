namespace Dante.Worker.Sessions;

// Metadados de entrada autenticada do host. Não são argumentos enviados pelo modelo às ferramentas.
public sealed record ContextoDeFerramentas(long Usuario, long Chat, long? Topico, long Mensagem, string? Texto, string? TrechoSelecionado);
