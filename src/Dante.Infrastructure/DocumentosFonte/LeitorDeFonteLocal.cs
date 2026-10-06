using System.Text;
using Dante.Application.DocumentosFonte;
using Microsoft.Extensions.Configuration;
namespace Dante.Infrastructure.DocumentosFonte;

public sealed class LeitorDeFonteLocal(IConfiguration configuration) : ILeitorDeFonteLocal
{
    public async Task<(string Origem, string Formato, string Conteudo)> LerAsync(string caminho, CancellationToken cancellationToken = default)
    {
        var raiz = configuration["DANTE_BRAIN_IMPORT_ROOT"];
        if (string.IsNullOrWhiteSpace(raiz) || !Path.IsPathFullyQualified(raiz) || !Path.IsPathFullyQualified(caminho))
            throw new UnauthorizedAccessException("Diretório de importação explícita não configurado.");
        raiz = Path.GetFullPath(raiz); caminho = Path.GetFullPath(caminho);
        var relativo = Path.GetRelativePath(raiz, caminho);
        if (relativo == ".." || relativo.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathFullyQualified(relativo))
            throw new UnauthorizedAccessException("Arquivo fora do diretório autorizado.");
        for (var atual = caminho; atual is not null; atual = Path.GetDirectoryName(atual))
        {
            if ((File.GetAttributes(atual) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Links não são aceitos na importação.");
        }
        var extensao = Path.GetExtension(caminho).ToLowerInvariant();
        if (extensao is not (".md" or ".markdown" or ".txt" or ".csv" or ".json" or ".yaml" or ".yml" or ".log"))
            throw new ArgumentException("Arquivo textual não suportado.");
        await using var stream = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 1000000) throw new ArgumentException("Arquivo excede 1 MB.");
        var bytes = new byte[1000001];
        var quantidade = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken);
        if (quantidade > 1000000) throw new ArgumentException("Arquivo excede 1 MB.");
        var conteudo = new UTF8Encoding(false, true).GetString(bytes, 0, quantidade);
        return ("local:" + relativo.Replace('\\', '/'), extensao is ".md" or ".markdown" ? "markdown" : "texto", conteudo.TrimStart('\uFEFF'));
    }
}
