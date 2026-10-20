namespace Dante.Infrastructure.Contextos;

// Caminho físico de um checkout, com symlinks e junctions resolvidos em todos os componentes (AD-58): dois aliases
// por caminhos diferentes podem levar ao mesmo diretório, e um diretório trocado por um link depois do cadastro passa
// a apontar para outro lugar. Comparações usam também maiúsculas indiferentes, como o /mnt/c do WSL.
internal static class CaminhoReal
{
    private const int MaximoDeLinks = 40;

    internal static string Resolver(string caminho) => Resolver(caminho, 0);

    // Aninhado ou igual, comparando os caminhos físicos sem diferenciar maiúsculas: na dúvida, recusa.
    internal static bool Sobrepoe(string primeiro, string segundo)
    {
        var (a, b) = (Resolver(primeiro), Resolver(segundo));
        return Dentro(a, b) || Dentro(b, a);
    }

    private static string Resolver(string caminho, int links)
    {
        var completo = Path.TrimEndingDirectorySeparator(Path.GetFullPath(caminho));
        var raiz = Path.GetPathRoot(completo)!;
        var partes = completo[raiz.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var atual = raiz;
        for (var i = 0; i < partes.Length; i++)
        {
            var proximo = Path.Combine(atual, partes[i]);
            var info = new DirectoryInfo(proximo);
            if (info.LinkTarget is not null)
            {
                if (links >= MaximoDeLinks)
                    throw new IOException($"Links simbólicos demais ao resolver {caminho}.");
                // The target may itself go through other links: resolve it again with the remaining components.
                var alvo = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? proximo;
                return Resolver(Path.Combine([alvo, .. partes[(i + 1)..]]), links + 1);
            }
            atual = proximo;
        }
        return Path.TrimEndingDirectorySeparator(atual);
    }

    private static bool Dentro(string caminho, string raiz)
    {
        var relativo = Path.GetRelativePath(raiz.ToLowerInvariant(), caminho.ToLowerInvariant());
        return relativo == "." || (relativo != ".." && !relativo.StartsWith(".." + Path.DirectorySeparatorChar,
            StringComparison.Ordinal) && !Path.IsPathFullyQualified(relativo));
    }
}
