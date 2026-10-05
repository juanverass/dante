using System.Text.RegularExpressions;
namespace Dante.Application.SegurancaDoBrain;

// Defesa complementar à classificação; referências não resolvem valores no Brain.
public static class ProtecaoDeSegredos
{
    private static readonly Regex Padrao = new(
        @"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{20,})\b|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+|(?:password|senha|token|api[_-]?key|secret)\s*[\""']?\s*[:=]\s*[\""']?[^\s\""',;}]{4,}|://[^/\s:]+:[^/\s@]+@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static bool ContemSegredo(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return false;
        // Uma referência explícita não contém a credencial; demais texto continua inspecionado.
        texto = Regex.Replace(texto, @"secret://[A-Za-z0-9_./-]+", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        try { return Padrao.IsMatch(texto); }
        catch (RegexMatchTimeoutException) { return true; }
    }
    public static void GarantirSeguro(params string?[] textos)
    {
        if (textos.Any(ContemSegredo)) throw new ArgumentException("Possível credencial detectada. Use uma referência secret://host/NOME; o valor não deve ser capturado.");
    }
    public static string? Redigir(string? texto) => ContemSegredo(texto) ? "[conteúdo protegido]" : texto;
}
