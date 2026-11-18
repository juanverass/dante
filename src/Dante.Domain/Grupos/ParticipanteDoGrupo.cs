namespace Dante.Domain.Grupos;

// Participante de uma versão de grupo (AD-69): o agente (não a versão dele, que é congelada só na criação da
// execução), o papel único dentro do grupo, as responsabilidades entregues ao próprio agente e se o papel escreve.
// PodeEscrever nunca passa do que a versão do agente permite: conferido ao salvar, ao ativar e ao executar.
public sealed record ParticipanteDoGrupo(Guid IdAgente, string Papel, string? Responsabilidades, bool PodeEscrever);
