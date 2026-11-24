namespace Dante.Application.IntegracaoDeCi;

// Um fato observado no provedor que sustenta uma classificação. Descricao é curta, sanitizada e sem credencial; o
// texto do log em si fica no TrechoDeLogDeCi.
public sealed record EvidenciaDeCiDto
{
    public const int TamanhoMaximoDaDescricao = 500;

    public EvidenciaDeCiDto(TipoDeEvidenciaDeCi tipo, string descricao, Uri? urlOficial = null, long? idDoJob = null,
        int? numeroDaEtapa = null)
    {
        Tipo = tipo;
        Descricao = CiValidator.ExigirTextoSeguro(descricao, TamanhoMaximoDaDescricao, nameof(descricao));
        UrlOficial = urlOficial is null ? null : CiValidator.ExigirUrlOficial(urlOficial, nameof(urlOficial));
        IdDoJob = idDoJob is { } id ? CiValidator.ExigirPositivo(id, nameof(idDoJob)) : null;
        NumeroDaEtapa = numeroDaEtapa is null or > 0 ? numeroDaEtapa
            : throw new ArgumentOutOfRangeException(nameof(numeroDaEtapa), "Etapa começa em 1.");
        if (tipo == TipoDeEvidenciaDeCi.EtapaComFalha && (IdDoJob is null || NumeroDaEtapa is null))
            throw new ArgumentException("Etapa com falha precisa do job e do número da etapa.", nameof(numeroDaEtapa));
    }

    public TipoDeEvidenciaDeCi Tipo { get; }
    public string Descricao { get; }
    public Uri? UrlOficial { get; }
    public long? IdDoJob { get; }
    public int? NumeroDaEtapa { get; }
}
