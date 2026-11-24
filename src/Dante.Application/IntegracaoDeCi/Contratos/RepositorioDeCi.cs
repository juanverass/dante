namespace Dante.Application.IntegracaoDeCi;

// Repositório observado no CI, preso a um alias cadastrado e a uma conexão. IdNoProvedor é o identificador numérico
// imutável do provedor: nulo só na consulta, antes de o provedor confirmá-lo; toda execução devolvida o traz, e a
// consolidação recusa uma execução cujo repositório não seja exatamente o consultado (renomeação, transferência ou
// resposta de outro repositório nunca se misturam).
public sealed record RepositorioDeCi
{
    public RepositorioDeCi(string alias, string nomeCompleto, long? idNoProvedor, ConexaoDeCi conexao)
    {
        Alias = CiValidator.ExigirAlias(alias);
        NomeCompleto = CiValidator.ExigirNomeCompleto(nomeCompleto);
        IdNoProvedor = idNoProvedor is { } id ? CiValidator.ExigirPositivo(id, nameof(idNoProvedor)) : null;
        Conexao = conexao ?? throw new ArgumentNullException(nameof(conexao));
    }

    public string Alias { get; }
    public string NomeCompleto { get; }
    public long? IdNoProvedor { get; }
    public ConexaoDeCi Conexao { get; }

    // Mesmo alias, mesma conexão, mesmo nome (sem diferenciar maiúsculas, como o provedor) e, quando ambos conhecem o
    // ID, o mesmo ID.
    public bool Corresponde(RepositorioDeCi outro) =>
        Alias == outro.Alias && Conexao == outro.Conexao
        && string.Equals(NomeCompleto, outro.NomeCompleto, StringComparison.OrdinalIgnoreCase)
        && (IdNoProvedor is null || outro.IdNoProvedor is null || IdNoProvedor == outro.IdNoProvedor);
}
