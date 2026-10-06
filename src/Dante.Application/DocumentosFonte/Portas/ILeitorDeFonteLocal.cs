namespace Dante.Application.DocumentosFonte;

public interface ILeitorDeFonteLocal
{
    Task<(string Origem, string Formato, string Conteudo)> LerAsync(string caminho, CancellationToken cancellationToken = default);
}
