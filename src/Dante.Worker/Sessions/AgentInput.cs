using Dante.Application.Anexos;

namespace Dante.Worker.Sessions;

// The neutral input of a turn or steer (AD-29): the user's text plus attachments already downloaded and checked, in
// the order they were sent. Each driver translates it to its CLI; a plain string is a text-only input.
public sealed record AgentInput(string Text, IReadOnlyList<Attachment> Attachments)
{
    // Opaque id of the adapter, kept with the turn this input opens (even after waiting in the queue) and returned on
    // that turn's completion (#148). Never sent to the agent.
    public string? Correlation { get; init; }

    public static implicit operator AgentInput(string text) => new(text, []);

    // Claude Code runs a user message that starts with "/" as one of its own commands (/clear, /compact…), changing the
    // conversation without the D.A.N.T.E. knowing (#119, AD-32). Leading blanks are ignored, like a prefix the user
    // cannot see.
    public static bool StartsWithCommand(string text) => text.TrimStart().StartsWith('/');

    public static string CommandRefusal(AgentKind agent) =>
        $"Mensagem não enviada: o {agent} executaria texto iniciado por \"/\" como comando da própria CLI (como /clear " +
        "ou /compact) e mudaria a conversa sem o D.A.N.T.E. acompanhar. Reescreva sem a barra no início.";
}
