namespace Dante.Infrastructure.Agentes;

// Checkouts of linked repositories handed to a CLI besides its working directory (AD-58).
public static class DiretoriosVinculados
{
    // General Mode never reaches a repository (AD-09): a linked directory there is a bug upstream, not a fallback.
    public static IReadOnlyList<string> Validar(IReadOnlyList<string>? diretorios, bool generalMode)
    {
        diretorios ??= [];
        if (generalMode && diretorios.Count > 0)
            throw new ArgumentException("O General Mode não recebe repositórios vinculados.", nameof(diretorios));
        if (diretorios.Any(diretorio => !Path.IsPathFullyQualified(diretorio)))
            throw new ArgumentException("Os diretórios vinculados devem ser absolutos.", nameof(diretorios));
        return diretorios;
    }
}
