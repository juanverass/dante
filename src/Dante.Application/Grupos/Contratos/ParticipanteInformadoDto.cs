namespace Dante.Application.Grupos;

// Participante como a interface o informa: o agente pelo handle registrado.
public sealed record ParticipanteInformadoDto(string Papel, string Handle, bool PodeEscrever,
    string? Responsabilidades = null);
