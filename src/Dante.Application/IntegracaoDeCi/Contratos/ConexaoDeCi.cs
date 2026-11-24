namespace Dante.Application.IntegracaoDeCi;

// Conexão autorizada com um provedor de CI (AD-76): um identificador estável e a referência ao binding do host que
// guarda a credencial de menor privilégio. Credencial é por conexão, nunca global; o valor só é resolvido pelo adapter
// no momento da chamada e nunca chega à Application, ao ledger, ao log nem à interface.
public sealed record ConexaoDeCi
{
    public ConexaoDeCi(string id, string referenciaDaCredencial)
    {
        Id = CiValidator.ExigirIdDaConexao(id);
        ReferenciaDaCredencial = CiValidator.ExigirReferenciaDeCredencial(referenciaDaCredencial);
    }

    public string Id { get; }
    public string ReferenciaDaCredencial { get; }
}
