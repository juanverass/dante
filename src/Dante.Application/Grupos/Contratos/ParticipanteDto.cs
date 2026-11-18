namespace Dante.Application.Grupos;

// Participante de uma versão com o handle do agente para exibição (o handle é imutável).
public sealed record ParticipanteDto(string Papel, Guid IdAgente, string Handle, bool PodeEscrever,
    string? Responsabilidades);
