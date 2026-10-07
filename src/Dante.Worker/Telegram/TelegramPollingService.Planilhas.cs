namespace Dante.Worker.Telegram;

// /google, /planilhas e /planilha (#224): administram a conta e o cadastro; não iniciam agente nem sessão.
public sealed partial class TelegramPollingService
{
    private async Task HandleSpreadsheetCommandAsync(long chatId, string command, string prompt,
        CancellationToken cancellationToken)
    {
        if (planilhas is null)
        {
            await SendReplyAsync(chatId, "Planilhas indisponíveis.", cancellationToken);
            return;
        }

        var reply = command switch
        {
            "/google" => await planilhas.GoogleAsync(prompt, text => SendReplyAsync(chatId, text, CancellationToken.None),
                cancellationToken),
            "/planilhas" => await planilhas.ListarAsync(cancellationToken),
            _ => await planilhas.PlanilhaAsync(prompt, cancellationToken)
        };
        await SendLongMessageAsync(chatId, reply, cancellationToken);
    }
}
