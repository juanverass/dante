namespace Dante.Domain.Grupos;

// Ciclo de vida de um grupo de agentes (AD-69), igual ao do agente: Ativo ⇄ Desativado → Arquivado. Arquivado é
// terminal; o nome continua reservado e as versões ficam para histórico.
public enum EstadoDoGrupo
{
    Ativo,
    Desativado,
    Arquivado
}
