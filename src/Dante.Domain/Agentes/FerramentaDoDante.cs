namespace Dante.Domain.Agentes;

// Servidores de ferramentas do D.A.N.T.E. que uma versão de agente pode receber (AD-68). Fora desta lista, nada é
// concedido: um servidor novo só chega a um agente registrado depois de entrar aqui e numa versão nova.
// Coordenacao (AD-70, #309): o dante_coordenacao para delegar a quem está em PodeConsultar. A concessão é dado da
// versão; o servidor só chega a uma sessão quando a entrega que o expõe (PR 3 da #309) o conceder.
public enum FerramentaDoDante
{
    Brain,
    Planilhas,
    Coordenacao
}
