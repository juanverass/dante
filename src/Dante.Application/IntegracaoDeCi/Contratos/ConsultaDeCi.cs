namespace Dante.Application.IntegracaoDeCi;

// O que se quer saber do CI: um repositório e um head SHA completo. O provedor é consultado só pelo SHA; quem consulta
// resolve o head atual do PR antes, e só execuções desse SHA contam (AD-76). Com NumeroDoPr, a consolidação também
// exige que a execução seja associada pelo provedor a esse PR (#238, AD-77).
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
