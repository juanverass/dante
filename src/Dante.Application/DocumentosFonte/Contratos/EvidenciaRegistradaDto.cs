namespace Dante.Application.DocumentosFonte;

// Duplicidades são apenas informativas: cada registro é preservado como fonte própria.
public sealed record EvidenciaRegistradaDto(DocumentoFonteDto Fonte, IReadOnlyList<string> PossiveisDuplicidades)
{
    public string Referencia => Fonte.Referencia;
    public bool PossivelDuplicidade => PossiveisDuplicidades.Count > 0;
}
