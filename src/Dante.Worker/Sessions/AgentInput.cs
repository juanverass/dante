using Dante.Worker.Attachments;

namespace Dante.Worker.Sessions;

// The neutral input of a turn or steer (AD-29): the user's text plus attachments already downloaded and checked, in
// the order they were sent. Each driver translates it to its CLI; a plain string is a text-only input.
public sealed record AgentInput(string Text, IReadOnlyList<Attachment> Attachments)
{
    public static implicit operator AgentInput(string text) => new(text, []);
}
