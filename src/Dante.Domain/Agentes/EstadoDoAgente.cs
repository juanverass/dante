namespace Dante.Domain.Agentes;

// Ciclo de vida de um agente registrado (AD-68): Ativo ⇄ Desativado → Arquivado. Arquivado é terminal; o handle
// continua reservado e as versões ficam para histórico.
public enum EstadoDoAgente
{
    Ativo,
    Desativado,
    Arquivado
}
