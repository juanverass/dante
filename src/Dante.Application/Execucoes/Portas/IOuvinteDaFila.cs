namespace Dante.Application.Execucoes;

// Sinal de trabalho novo vindo do banco (LISTEN dante_fila, AD-67). É só um atalho: notificação perdida, conexão
// caída ou banco que não notifica nunca deixam trabalho parado, porque o processador também faz polling.
public interface IOuvinteDaFila
{
    // Ouve até o cancelamento, reconectando sozinho; chama aoNotificar a cada notificação e a cada (re)conexão.
    Task OuvirAsync(Action aoNotificar, CancellationToken cancellationToken);
}
