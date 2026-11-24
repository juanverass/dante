namespace Dante.Application.IntegracaoDeCi;

// O que se quer saber do CI: um repositório e um head SHA completo. NumeroDoPr é contexto de exibição e correlação,
// nunca chave: quem consulta resolve o head atual do PR antes, e só execuções desse SHA contam (AD-76).
public sealed record ConsultaDeCi
{
    public ConsultaDeCi(RepositorioDeCi repositorio, string shaDoHead, int? numeroDoPr = null)
    {
        Repositorio = repositorio ?? throw new ArgumentNullException(nameof(repositorio));
        ShaDoHead = CiValidator.ExigirSha(shaDoHead);
        NumeroDoPr = numeroDoPr is null or > 0 ? numeroDoPr
            : throw new ArgumentOutOfRangeException(nameof(numeroDoPr), "Número de PR deve ser positivo.");
    }

    public RepositorioDeCi Repositorio { get; }
    public string ShaDoHead { get; }
    public int? NumeroDoPr { get; }
}
