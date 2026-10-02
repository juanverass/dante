using Dante.Worker.Artifacts;
using Dante.Worker.Attachments;
using Dante.Worker.Sessions;
using System.Text;

namespace Dante.Worker.Telegram;

// Showcase images for LinkedIn (#98). /vitrine opens a turn that asks the agent for a ShowcaseSpec; the reply of that
// turn never reaches the chat as text. When the turn completes, the spec is checked against the prints of the
// conversation, the image is rendered locally and sent like a /send of the session's attachment directory (#97),
// named after the turn and the version. Every other event passes through unchanged. An adjustment is another /vitrine
// in the same conversation, so the agent revises its previous answer and a new version comes back.
public sealed class TelegramShowcase(IAgentSessionEventSink inner, TelegramDeliveryService delivery,
    IShowcaseRenderer renderer, AttachmentStore store, ILogger<TelegramShowcase> logger) : IAgentSessionEventSink
{
    private const int MaxShownReply = 3000;
    private readonly object gate = new();
    // By session: the next turn of that session is the showcase request (the session was idle with an empty queue).
    private readonly Dictionary<string, Request> expected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Session, string Turn), Request> turns = [];
    private readonly Dictionary<string, int> versions = new(StringComparer.OrdinalIgnoreCase);

    public string? Unavailable() => renderer.Unavailable();

    // The turn sent to the agent: the user's request, the contract of the answer and the prints it may use, by id.
    public static string RequestText(string request, IReadOnlyList<Attachment> prints,
        IReadOnlyDictionary<string, string?> names)
    {
        var text = new StringBuilder(request).Append("\n\n[Pedido de vitrine do D.A.N.T.E.] Não gere nem edite " +
            "imagens: o D.A.N.T.E. monta um PNG para LinkedIn colando os prints desta conversa sem alterá-los, sobre " +
            "fundo claro, com título, subtítulo, uma etiqueta sob cada print (uma pode ficar em destaque) e rodapé " +
            "opcional. Responda somente com um bloco ```json neste formato:\n").Append(ShowcaseSpec.Schema)
            .Append($"\nRegras: use só ids da lista, na ordem em que devem aparecer, de 1 a {ShowcaseSpec.MaxPrints} " +
                "prints; format é landscape (1600×900), square (1400×1400) ou portrait (1200×1500); title até " +
                $"{ShowcaseSpec.MaxTitle} caracteres, subtitle até {ShowcaseSpec.MaxSubtitle}, label até " +
                $"{ShowcaseSpec.MaxLabel} e footer até {ShowcaseSpec.MaxFooter} (pode ser vazio); accent é uma cor " +
                "escura dos prints, legível sobre fundo claro; highlight em no máximo um print. Escreva os textos no " +
                "idioma do pedido. Num ajuste, devolva o JSON completo revisado.\nPrints desta conversa:");
        foreach (var print in prints)
            text.Append($"\n- {print.Id}" + (names.GetValueOrDefault(print.Id) is { } name ? $" ({name})" : "") +
                $", {print.Width}×{print.Height}");
        return text.ToString();
    }

    // Called before the request is submitted, so no event of its turn can arrive first.
    public void Expect(string sessionId, long userId, long chatId)
    {
        lock (gate) expected[sessionId] = new Request(userId, chatId);
    }

    public void Forget(string sessionId)
    {
        lock (gate) expected.Remove(sessionId);
    }

    public async Task PublishAsync(AgentSessionSnapshot session, AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        Request? request = null;
        lock (gate)
        {
            if (agentEvent.TurnId is { } turnId && !turns.TryGetValue((session.Id, turnId), out request) &&
                expected.Remove(session.Id, out request))
                turns[(session.Id, turnId)] = request;
            if (agentEvent is ErrorEvent { TurnId: null } && session.State == AgentSessionState.Failed)
            {
                expected.Remove(session.Id);
                foreach (var key in turns.Keys.Where(key => key.Session == session.Id).ToArray()) turns.Remove(key);
            }
        }
        if (request is null)
        {
            await inner.PublishAsync(session, agentEvent, cancellationToken);
            return;
        }

        switch (agentEvent)
        {
            case MessageDeltaEvent:
                return;
            case MessageCompletedEvent message:
                lock (gate) request.Replies.Add(message.Text);
                return;
            case TurnCompletedEvent completed:
                lock (gate) turns.Remove((session.Id, completed.TurnId!));
                if (completed.Outcome == AgentTurnOutcome.Completed)
                {
                    var reply = await ComposeAsync(session, completed.TurnId!, request, cancellationToken);
                    await inner.PublishAsync(session, new MessageCompletedEvent("dante-showcase", reply)
                    {
                        SessionId = completed.SessionId,
                        TurnId = completed.TurnId,
                        TimestampUtc = DateTimeOffset.UtcNow
                    }, cancellationToken);
                }
                await inner.PublishAsync(session, completed, cancellationToken);
                return;
            default:
                await inner.PublishAsync(session, agentEvent, cancellationToken);
                return;
        }
    }

    private async Task<string> ComposeAsync(AgentSessionSnapshot session, string turnId, Request request,
        CancellationToken cancellationToken)
    {
        string reply;
        lock (gate) reply = string.Join('\n', request.Replies).Trim();
        var prints = store.Images(session.OwnerUserId, session.Id);
        var (spec, error) = ShowcaseSpec.Parse(reply, prints.Select(print => print.Id).ToArray());
        // Without a valid spec the agent may have asked something: its words are shown, with what was missing.
        if (spec is null)
            return (reply.Length == 0 ? "" : (reply.Length <= MaxShownReply ? reply : reply[..MaxShownReply] + "…") + "\n\n") +
                $"Não montei a imagem: {error}. Peça um ajuste com /vitrine <instruções>.";

        int version;
        lock (gate) versions[session.Id] = version = versions.GetValueOrDefault(session.Id) + 1;
        string directory;
        string path;
        try
        {
            directory = store.ScopePath(session.OwnerUserId, session.Id);
            path = Path.Combine(directory, $"vitrine-{turnId}-v{version}.png");
            await renderer.RenderAsync(spec, prints.Select(print =>
                new ShowcaseImage(print.Id, print.Path, print.Width!.Value, print.Height!.Value)).ToArray(), path,
                cancellationToken);
        }
        catch (ShowcaseRenderException exception)
        {
            return $"Não montei a imagem: {exception.Message}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Falha ao montar a vitrine da sessão {SessionId} ({ErrorType}).", session.Id,
                exception.GetType().Name);
            return "Não montei a imagem: falha ao gravar o arquivo.";
        }

        var (id, refusal) = delivery.SendFile(session.Id, request.UserId, request.ChatId, path, directory);
        if (refusal is not null) return $"Vitrine v{version} montada, mas não enviada: {refusal}.";
        var (width, height) = ShowcaseLayoutBuilder.Canvas(spec.Format);
        return $"Vitrine v{version} ({id}, {ShowcaseSpec.Name(spec.Format)} {width}×{height}): \"{spec.Title}\". " +
            "Os prints foram colados sem alteração. Para ajustar, use /vitrine <instruções>.";
    }

    private sealed class Request(long userId, long chatId)
    {
        public long UserId { get; } = userId;
        public long ChatId { get; } = chatId;
        public List<string> Replies { get; } = [];
    }
}
