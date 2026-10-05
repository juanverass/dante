namespace Dante.Application.Anexos;

// Legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
public enum AttachmentKind { Image, Audio, Video, Document }

// The neutral attachment of AD-29: no Telegram type reaches sessions, drivers or runners. Name is only metadata.
public sealed record Attachment(
    string Id,
    long OwnerId,
    AttachmentKind Kind,
    string MediaType,
    string Path,
    long Bytes,
    int? Width,
    int? Height,
    string? Name);
