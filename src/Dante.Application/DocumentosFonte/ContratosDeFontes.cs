using Dante.Application.Comum;
using Dante.Application.BuscaDoBrain;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.DocumentosFonte;
using Dante.Domain.Conhecimentos;
namespace Dante.Application.DocumentosFonte;

public sealed record DocumentoFonteDto(Guid Id, Guid IdEspacoDeConhecimento, Guid? IdProjeto, string Origem,
    string Formato, string Hash, int Revisao, Sensibilidade Sensibilidade, DateTimeOffset AtualizadoEm, bool Removido);
public sealed record TrechoDaFonteDto(Guid IdDocumento, int Revisao, int Numero, int Inicio, string Conteudo, string Origem, string Hash)
{
    public string Referencia => $"brain://fonte/{IdDocumento:D}/revisao/{Revisao}/parte/{Numero}?hash={Hash}";
}
public interface IDocumentoFonteRepository : IRepository<DocumentoFonte>
{
    Task<DocumentoFonte?> ObterPelaOrigemAsync(AcessoAoBrain acesso, string origem, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentoFonte>> ListarAsync(AcessoAoBrain acesso, int limite, CancellationToken cancellationToken = default);
}
public interface IIndiceDeFontes
{
    Task<IReadOnlyList<ResultadoDaBuscaDto>> BuscarAsync(AcessoAoBrain acesso, BuscaDoBrainSearchDto filtro,
        ModeloEmbedding? modelo, float[]? vetor, CancellationToken cancellationToken = default);
    Task<TrechoDaFonteDto?> ObterTrechoAsync(AcessoAoBrain acesso, Guid id, int revisao, int numero, CancellationToken cancellationToken = default);
    Task ReconstruirAsync(AcessoAoBrain acesso, CancellationToken cancellationToken = default);
    Task<int> ReindexarAsync(AcessoAoBrain acesso, IGeradorDeEmbedding embeddings, int limite, CancellationToken cancellationToken = default);
}
public interface ILeitorDeFonteLocal
{
    Task<(string Origem, string Formato, string Conteudo)> LerAsync(string caminho, CancellationToken cancellationToken = default);
}
