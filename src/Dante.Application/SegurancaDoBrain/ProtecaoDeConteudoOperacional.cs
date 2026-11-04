namespace Dante.Application.SegurancaDoBrain;

// Snapshot e evidências aceitam somente resultados operacionais selecionados: nunca transcript nem raciocínio privado.
public static class ProtecaoDeConteudoOperacional
{
    private static readonly string[] Marcadores = ["chain-of-thought", "<thinking>", "raciocínio privado"];
    public static bool ContemRaciocinioPrivado(string? texto) =>
        !string.IsNullOrEmpty(texto) && Marcadores.Any(m => texto.Contains(m, StringComparison.OrdinalIgnoreCase));
    public static void Garantir(IEnumerable<string?> textos, string mensagem)
    {
        if (textos.Any(ContemRaciocinioPrivado)) throw new ArgumentException(mensagem);
    }
}
