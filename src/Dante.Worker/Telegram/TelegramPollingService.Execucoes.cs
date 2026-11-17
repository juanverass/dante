using Dante.Domain.Execucoes;

namespace Dante.Worker.Telegram;

// /cancel (#305): J… continua no registro em memória do job (AD-06/67), agora só para o dono; um J… que esse registro
// não tem (boot anterior, interrompido) e todo E… vão ao caso de uso de controle da Application, que é quem decide.
public sealed partial class TelegramPollingService
{
    private const string CancelUsage = "Uso: /cancel <J…|E…> [motivo]";

    private async Task<string> HandleCancelCommandAsync(long userId, string prompt, CancellationToken cancellationToken)
    {
        var separator = prompt.IndexOfAny([' ', '\t', '\r', '\n']);
        var code = separator < 0 ? prompt : prompt[..separator];
        var reason = separator < 0 ? null : prompt[(separator + 1)..].Trim();
        if (code.Length == 0) return CancelUsage;
        var legacyNotFound = $"Job {code} não encontrado ou já encerrado.";
        var parsed = Execucao.TentarInterpretarCodigo(code, out _, out var legacy);
        if (!parsed || legacy)
        {
            // A job of another user is answered exactly like one that does not exist.
            if (jobs.TryCancel(code, userId, out var cancelled))
                return $"Cancelamento solicitado para {cancelled!.Id} ({cancelled.Context.Descricao}).";
            return parsed && execucoes is not null
                ? await execucoes.CancelarAsync(userId, code, reason, legacyNotFound, cancellationToken)
                : legacyNotFound;
        }
        return execucoes is null ? TelegramExecucoes.Desabilitado
            : await execucoes.CancelarAsync(userId, code, reason, TelegramExecucoes.NaoEncontrada(code), cancellationToken);
    }
}
