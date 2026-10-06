namespace Dante.Application.DocumentosFonte;

internal static class DocumentoFonteValidator
{
    internal static void ValidarFormato(string formato)
    {
        if (formato is not ("markdown" or "texto")) throw new ArgumentException("Formato não suportado.");
    }
}
