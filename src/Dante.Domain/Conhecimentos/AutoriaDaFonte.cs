namespace Dante.Domain.Conhecimentos;

// Quem escreveu o conteúdo de uma fonte, independentemente de quem pediu a captura (AD-59 P2/I9).
// Desconhecida existe só para registros anteriores à #264: nunca é deduzida para Usuario (AD-59 D3)
// e não pode ser atribuída a escritas novas.
public enum AutoriaDaFonte { Desconhecida, Usuario, Agente }
