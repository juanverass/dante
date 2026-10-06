namespace Dante.Application.DocumentosFonte;

public sealed record TrechoDaFonteDto(Guid IdDocumento, int Revisao, int Numero, int Inicio, string Conteudo, string Origem, string Hash)
{
    public string Referencia => $"brain://fonte/{IdDocumento:D}/revisao/{Revisao}/parte/{Numero}?hash={Hash}";
}
