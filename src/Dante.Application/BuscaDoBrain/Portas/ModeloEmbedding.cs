namespace Dante.Application.BuscaDoBrain;

public sealed record ModeloEmbedding(string Provedor, string Nome, string Versao, int Dimensao)
{
    public string Chave => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes($"{Provedor.Length}:{Provedor}{Nome.Length}:{Nome}{Versao.Length}:{Versao}|{Dimensao}|cosine|normalizado|indexador-1")));
}
