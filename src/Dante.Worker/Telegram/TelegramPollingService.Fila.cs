using Dante.Application.Agentes;
using Dante.Worker.Execucoes;
using Dante.Worker.Jobs;

namespace Dante.Worker.Telegram;

// /claude e /codex pelo caminho da fila durável (AD-67). As respostas ao usuário são as do legado; o que muda é que
// execução e trabalho são gravados antes do "iniciado", e o RunJobAsync roda quando o processador reivindica o
// trabalho, com início e resultado gravados sob posse. A chave de deduplicação é a mensagem do Telegram.
public sealed partial class TelegramPollingService
{
    private async Task EnqueueJobAsync(TelegramMessage message, AgentContextResolution resolution,
        JobExecutionContext context, AgentModelSelection modelSelection, CancellationToken cancellationToken)
    {
        var userId = message.From!.Id;
        var agent = resolution.Agent.ToString();
        // Telegram always sends message_id; without one there is nothing to deduplicate by, and the job gets its own key.
        var key = message.MessageId > 0 ? $"telegram:{message.Chat.Id}:{message.MessageId}"
            : $"telegram:{message.Chat.Id}:sem-id:{Guid.NewGuid():N}";
        var created = await filaDeJobs!.CriarAsync(agent, context, userId, key, modelSelection, cancellationToken);
        if (created.Job is not { } job)
        {
            await SendReplyAsync(message.Chat.Id, created.Recusa!, cancellationToken);
            return;
        }
        // Until released, the processor waits; any exit without release ends the job as failed, never runs it.
        var released = false;
        try
        {
            var images = Adopt(userId, AttachmentContext(userId), job.Id, newScope: true);
            if (images is null)
            {
                filaDeJobs.Abortar(job.Id, ImagesUnavailable);
                released = true;
                await SendReplyAsync(message.Chat.Id, ImagesUnavailable, cancellationToken);
                return;
            }
            try
            {
                await SendReplyAsync(message.Chat.Id,
                    $"{agent} iniciado. Job ID: {job.Id} ({context.Descricao}){ModelSuffix(modelSelection)}{ImageSuffix(images)}." +
                    OverrideNotice(userId, resolution), cancellationToken);
            }
            catch
            {
                filaDeJobs.Abortar(job.Id, "Falha ao confirmar o início.", () => ReleaseJobImages(userId, job.Id, images));
                released = true;
                throw;
            }
            var jobToken = created.Token;
            filaDeJobs.Liberar(job.Id, controle => RunJobAsync(job.Id, agent, resolution.Agent == AgentKind.Codex,
                resolution.Prompt, context, resolution.Environment, modelSelection, images, userId, message.Chat.Id,
                jobToken, cancellationToken, controle));
            released = true;
        }
        finally
        {
            if (!released) filaDeJobs.Abortar(job.Id, "Falha ao confirmar o início.");
        }
    }

    private async Task RecordQueuedResultAsync(ExecucaoSobPosse posse, JobSnapshot completed)
    {
        try
        {
            await posse.ConcluirAsync(ExecutorDeJobsDaFila.Resultado(completed), CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Lease lost or database unavailable: the ledger keeps the step running and the queue's recovery applies
            // the resume policy (never re-run without confirmation). The user still gets the local result.
            logger.LogWarning("Resultado do job {JobId} não gravado na fila ({ErrorType}).", completed.Id,
                exception.GetType().Name);
        }
    }
}
