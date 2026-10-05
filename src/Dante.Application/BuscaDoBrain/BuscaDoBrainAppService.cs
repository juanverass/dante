using Dante.Application.Conhecimentos;
using Dante.Application.SegurancaDoBrain;
namespace Dante.Application.BuscaDoBrain;

public sealed class BuscaDoBrainAppService(IIndiceDeBusca indice, IGeradorDeEmbedding embeddings,
    IConhecimentoRepository conhecimentos, LeituraDoBrainAppService leitura, PoliticaDeSensibilidade politica)
{
    public async Task<BuscaDoBrainDto> BuscarAsync(AcessoAoBrain acesso, BuscaDoBrainSearchDto filtro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        await leitura.ValidarAcessoAsync(acesso, cancellationToken);
        if (filtro.Limite is < 1 or > 100 || filtro.Deslocamento is < 0 or > 10000 || filtro.Texto.Length > 2000 ||
            filtro.Tags.Count > 10 || filtro.Tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 100) ||
            filtro.IdConhecimento == Guid.Empty || string.IsNullOrWhiteSpace(filtro.Texto) && filtro.IdConhecimento is null ||
            filtro.CriadoDesde >= filtro.CriadoAte || filtro.ValidoEm == default(DateTimeOffset) ||
            filtro.Tipo is { } tipo && !Enum.IsDefined(tipo) || filtro.Status is { } status && !Enum.IsDefined(status) ||
            filtro.Sensibilidade is { } s && !Enum.IsDefined(s)) throw new ArgumentException("Filtro de busca inválido.");
        ProtecaoDeSegredos.GarantirSeguro(filtro.Texto);
        var modelo = embeddings.Modelo; float[]? vetor = null;
        if (modelo is not null && filtro.IdConhecimento is null && await indice.VetoresDisponiveisAsync(cancellationToken))
            vetor = await embeddings.GerarAsync(filtro.Texto, cancellationToken);
        if (vetor is not null) vetor = Normalizar(vetor, modelo!.Dimensao);
        var matches = await indice.BuscarAsync(acesso, filtro with { Limite = Math.Min(101, filtro.Limite + 1) }, modelo, vetor, cancellationToken);
        var resultados = new List<ResultadoDaBuscaDto>();
        foreach (var match in matches)
        {
            var item = await conhecimentos.ObterPorIdAsync(match.Id, cancellationToken);
            if (item is null || item.Revisao != match.Revisao || item.IdEspacoDeConhecimento != acesso.IdEspacoDeConhecimento ||
                item.IdProjeto != acesso.IdProjeto || !item.EstaValidoEm(filtro.ValidoEm ?? DateTimeOffset.UtcNow)) continue;
            var saida = politica.Projetar(item, acesso, FinalidadeDeLeitura.Busca);
            if (saida.ConteudoProtegido && filtro.IdConhecimento != item.Id) continue;
            resultados.Add(new(saida, OrigemDoResultado.Conhecimento, match.Score, match.ScoreLexical, match.ScoreSemantico,
                saida.ConteudoProtegido ? "Identificador autorizado; conteúdo protegido." : match.ScoreLexical > 0 && match.ScoreSemantico is not null ?
                "Correspondência lexical e proximidade semântica; combinação por ranking." : match.ScoreLexical > 0 ?
                "Correspondência full-text em conteúdo/tags autorizados." : "Proximidade semântica por distância cosseno."));
        }
        return new(resultados.Take(filtro.Limite).ToArray(), vetor is null ? "lexical" : "hibrido", resultados.Count > filtro.Limite,
            vetor is null ? null : modelo!.Nome + "@" + modelo.Versao);
    }
    public async Task<int> ReindexarAsync(AcessoAoBrain acesso, int limite = 100, bool reconstruir = false, CancellationToken cancellationToken = default)
    {
        await leitura.ValidarAcessoAsync(acesso, cancellationToken);
        if (limite is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limite));
        var modelo = embeddings.Modelo;
        if (modelo is null || !await indice.VetoresDisponiveisAsync(cancellationToken)) return 0;
        if (reconstruir) await indice.LimparModeloAsync(acesso, modelo, cancellationToken);
        var ids = await indice.ListarPendentesAsync(embeddings.Externo ? acesso with { PermitirConfidencial = false } : acesso, modelo, limite, cancellationToken); var gravados = 0;
        foreach (var id in ids)
        {
            var item = await conhecimentos.ObterPorIdAsync(id, cancellationToken);
            if (item is null || !item.EstaValidoEm(DateTimeOffset.UtcNow) ||
                !politica.PermiteConteudo(item, acesso, embeddings.Externo ? FinalidadeDeLeitura.IndexacaoExterna : FinalidadeDeLeitura.Busca)) continue;
            var texto = string.Join("\n", item.Conteudo, item.DadosEstruturados, string.Join(" ", item.Tags));
            if (ProtecaoDeSegredos.ContemSegredo(texto)) continue;
            var vetor = await embeddings.GerarAsync(texto, cancellationToken);
            if (vetor is null) continue;
            if (await indice.GravarAsync(item.Id, item.Revisao, modelo, Normalizar(vetor, modelo.Dimensao), cancellationToken)) gravados++;
        }
        return gravados;
    }
    public static float[] Normalizar(float[] vetor, int dimensao)
    {
        if (dimensao is < 1 or > 16000 || vetor.Length != dimensao || vetor.Any(x => !float.IsFinite(x)))
            throw new ArgumentException("Embedding inválido.");
        var norma = Math.Sqrt(vetor.Sum(x => (double)x * x));
        if (norma <= 0 || !double.IsFinite(norma)) throw new ArgumentException("Embedding sem direção válida.");
        return vetor.Select(x => (float)(x / norma)).ToArray();
    }
}
