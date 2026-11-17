namespace Dante.Domain.Agentes;

// Servidores de ferramentas do D.A.N.T.E. que uma versão de agente pode receber (AD-68). Fora desta lista, nada é
// concedido: um servidor novo só chega a um agente registrado depois de entrar aqui e numa versão nova.
public enum FerramentaDoDante
{
    Brain,
    Planilhas
}
