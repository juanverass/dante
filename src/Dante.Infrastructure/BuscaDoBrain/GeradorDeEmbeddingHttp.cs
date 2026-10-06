using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dante.Application.BuscaDoBrain;
using Microsoft.Extensions.Configuration;
namespace Dante.Infrastructure.BuscaDoBrain;

// API de embeddings compatível com input/model/data[].embedding; sem dependência de assinatura/CLI.
public sealed class GeradorDeEmbeddingHttp(HttpClient http, IConfiguration configuracao) : IGeradorDeEmbedding, IDisposable
{
    public void Dispose() => http.Dispose();
    private readonly string? endpoint = configuracao["DANTE_BRAIN_EMBEDDING_ENDPOINT"];
    public bool Externo => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && !uri.IsLoopback;
    public ModeloEmbedding? Modelo => !string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(configuracao["DANTE_BRAIN_EMBEDDING_MODEL"]) &&
        int.TryParse(configuracao["DANTE_BRAIN_EMBEDDING_DIMENSION"], out var dim) && dim is > 0 and <= 16000 &&
        !string.IsNullOrWhiteSpace(configuracao["DANTE_BRAIN_EMBEDDING_VERSION"]) ?
        new(configuracao["DANTE_BRAIN_EMBEDDING_PROVIDER"] ?? "http", configuracao["DANTE_BRAIN_EMBEDDING_MODEL"]!, configuracao["DANTE_BRAIN_EMBEDDING_VERSION"]!, dim) : null;
    public async Task<float[]?> GerarAsync(string texto, CancellationToken cancellationToken = default)
    {
        var modelo = Modelo;
        if (modelo is null || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || Externo && (uri.Scheme != "https" || configuracao["DANTE_BRAIN_EMBEDDING_ALLOW_EXTERNAL"] != "true")) return null;
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); prazo.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(new { input = texto, model = modelo.Nome }) };
            var chave = configuracao["DANTE_BRAIN_EMBEDDING_API_KEY"];
            if (!string.IsNullOrEmpty(chave)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", chave);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, prazo.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 512000) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(prazo.Token);
            using var buffer = new MemoryStream(); var bytes = new byte[8192]; int lidos;
            while ((lidos = await stream.ReadAsync(bytes, prazo.Token)) > 0) { if (buffer.Length + lidos > 512000) return null; buffer.Write(bytes, 0, lidos); }
            using var json = JsonDocument.Parse(buffer.ToArray());
            var vetor = json.RootElement.GetProperty("data")[0].GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            return BuscaDoBrainAppService.Normalizar(vetor, modelo.Dimensao);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or FormatException) { return null; }
    }
}
