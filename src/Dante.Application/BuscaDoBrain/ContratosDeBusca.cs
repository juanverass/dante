using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
namespace Dante.Application.BuscaDoBrain;

public sealed record BuscaDoBrainSearchDto
{
    public string Texto { get; init; } = "";
    public Guid? IdConhecimento { get; init; }
    public TipoDeConhecimento? Tipo { get; init; }
    public IReadOnlyList<TipoDeConhecimento> Tipos { get; init; } = [];
    public StatusDoConhecimento? Status { get; init; }
    public Sensibilidade? Sensibilidade { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public DateTimeOffset? CriadoDesde { get; init; }
    public DateTimeOffset? CriadoAte { get; init; }
    public DateTimeOffset? ValidoEm { get; init; }
    public int Limite { get; init; } = 20;
    public int Deslocamento { get; init; }
}
public enum OrigemDoResultado { Conhecimento, FonteBruta }
public sealed record ResultadoDaBuscaDto(LeituraProtegidaDto Item, OrigemDoResultado Origem, double Score,
    double ScoreLexical, double? ScoreSemantico, string Explicacao, Dante.Application.DocumentosFonte.TrechoDaFonteDto? Fonte = null);
public sealed record BuscaDoBrainDto(IReadOnlyList<ResultadoDaBuscaDto> Resultados, string Modo,
    bool TemMais, string? ModeloEmbedding);
public sealed record ModeloEmbedding(string Provedor, string Nome, string Versao, int Dimensao)
{
    public string Chave => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes($"{Provedor.Length}:{Provedor}{Nome.Length}:{Nome}{Versao.Length}:{Versao}|{Dimensao}|cosine|normalizado|indexador-1")));
}
public interface IGeradorDeEmbedding
{
    ModeloEmbedding? Modelo { get; }
    bool Externo { get; }
    Task<float[]?> GerarAsync(string texto, CancellationToken cancellationToken = default);
}
public sealed record MatchDaBusca(Guid Id, int Revisao, double Score, double ScoreLexical, double? ScoreSemantico);
public interface IIndiceDeBusca
{
    Task<bool> VetoresDisponiveisAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MatchDaBusca>> BuscarAsync(AcessoAoBrain acesso, BuscaDoBrainSearchDto filtro,
        ModeloEmbedding? modelo, float[]? vetor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> ListarPendentesAsync(AcessoAoBrain acesso, ModeloEmbedding modelo, int limite,
        CancellationToken cancellationToken = default);
    Task<bool> GravarAsync(Guid id, int revisao, ModeloEmbedding modelo, float[] vetor, CancellationToken cancellationToken = default);
    Task LimparModeloAsync(AcessoAoBrain acesso, ModeloEmbedding modelo, CancellationToken cancellationToken = default);
}
