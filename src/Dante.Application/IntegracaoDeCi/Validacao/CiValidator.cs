using System.Text.RegularExpressions;

namespace Dante.Application.IntegracaoDeCi;

// Validação de forma da identidade e da política do CI (AD-52, AD-76). Não consulta o provedor: só recusa o que nunca
// pode ser uma identidade válida, antes de qualquer chamada ou comparação.
internal static class CiValidator
{
    // Mesmo formato do cadastro de repositórios (AD-07): o CI só existe para um alias cadastrado.
    internal static string ExigirAlias(string? alias) =>
        alias is not null && Regex.IsMatch(alias, "^@[a-zA-Z][a-zA-Z0-9_]*$", RegexOptions.None, TimeSpan.FromMilliseconds(100))
            ? alias
            : throw new ArgumentException("Alias de repositório inválido. Use @nome, como no cadastro.", nameof(alias));

    internal static string ExigirNomeCompleto(string? nomeCompleto) =>
        nomeCompleto is not null && Regex.IsMatch(nomeCompleto, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.None, TimeSpan.FromMilliseconds(100))
            ? nomeCompleto
            : throw new ArgumentException("Nome completo do repositório inválido. Use dono/repositorio.", nameof(nomeCompleto));

    // SHA-1 (40) ou SHA-256 (64) completo, em minúsculas: prefixo abreviado é ambíguo e nunca identifica o head.
    internal static string ExigirSha(string? sha)
    {
        var normalizado = sha?.Trim().ToLowerInvariant();
        return normalizado is not null && Regex.IsMatch(normalizado, "^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.None, TimeSpan.FromMilliseconds(100))
            ? normalizado
            : throw new ArgumentException("SHA do commit inválido. Informe o SHA completo, não abreviado.", nameof(sha));
    }

    internal static string ExigirIdDaConexao(string? id) =>
        id is not null && Regex.IsMatch(id, "^[a-z][a-z0-9_-]{0,39}$", RegexOptions.None, TimeSpan.FromMilliseconds(100))
            ? id
            : throw new ArgumentException("Identificador de conexão de CI inválido.", nameof(id));

    // A conexão guarda só a referência lógica ao binding do host (AD-10), nunca o valor da credencial.
    internal static string ExigirReferenciaDeCredencial(string? referencia) =>
        referencia is not null && Regex.IsMatch(referencia, "^secret://host/[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.None, TimeSpan.FromMilliseconds(100))
            ? referencia
            : throw new ArgumentException("Credencial de CI deve ser uma referência secret://host/NOME, nunca o valor.",
                nameof(referencia));

    // Enum vindo de fora (adapter, desserialização, cast) só vale se for um valor declarado: um valor desconhecido nunca
    // é aproximado para outro estado (revisão do PR #327).
    internal static T ExigirDefinido<T>(T valor, string parametro) where T : struct, Enum =>
        Enum.IsDefined(valor) ? valor
            : throw new ArgumentOutOfRangeException(parametro, valor, $"Valor desconhecido de {typeof(T).Name}.");

    internal static int ExigirNumeroDoPr(int numero) =>
        numero > 0 ? numero : throw new ArgumentOutOfRangeException(nameof(numero), "Número de PR deve ser positivo.");

    internal static long ExigirPositivo(long valor, string parametro) =>
        valor > 0 ? valor : throw new ArgumentOutOfRangeException(parametro, "Identificador do provedor deve ser positivo.");

    internal static Uri ExigirUrlOficial(Uri? url, string parametro) =>
        url is { IsAbsoluteUri: true } && url.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(url.UserInfo)
            ? url
            : throw new ArgumentException("URL oficial deve ser https absoluta e sem credencial.", parametro);

    // Texto exibível vindo do provedor (nome de job, resumo de evidência): curto e sem credencial aparente.
    internal static string ExigirTextoSeguro(string? texto, int maximo, string parametro)
    {
        if (string.IsNullOrWhiteSpace(texto) || texto.Length > maximo)
            throw new ArgumentException($"Texto vazio ou com mais de {maximo} caracteres.", parametro);
        if (SegurancaDoBrain.ProtecaoDeSegredos.ContemSegredo(texto))
            throw new ArgumentException("Possível credencial no texto do CI; use o trecho sanitizado.", parametro);
        return texto;
    }

    internal static PoliticaDeConsultaDeCi ExigirPolitica(PoliticaDeConsultaDeCi politica)
    {
        static void Faixa(bool ok, string campo)
        {
            if (!ok) throw new ArgumentOutOfRangeException(campo, $"{campo} fora da faixa permitida pela AD-76.");
        }

        Faixa(politica.TempoLimiteDaRequisicao > TimeSpan.Zero && politica.TempoLimiteDaRequisicao <= TimeSpan.FromSeconds(60),
            nameof(politica.TempoLimiteDaRequisicao));
        Faixa(politica.TentativasPorRequisicao is >= 1 and <= 5, nameof(politica.TentativasPorRequisicao));
        Faixa(politica.EsperaMaximaPorLimiteDeTaxa >= TimeSpan.Zero && politica.EsperaMaximaPorLimiteDeTaxa <= TimeSpan.FromMinutes(2),
            nameof(politica.EsperaMaximaPorLimiteDeTaxa));
        Faixa(politica.ItensPorPagina is >= 1 and <= 100, nameof(politica.ItensPorPagina));
        Faixa(politica.PaginasMaximas is >= 1 and <= 20, nameof(politica.PaginasMaximas));
        Faixa(politica.ValidadeDoCacheNaoFinal >= TimeSpan.Zero && politica.ValidadeDoCacheNaoFinal < politica.IntervaloMinimoDePolling,
            nameof(politica.ValidadeDoCacheNaoFinal));
        Faixa(politica.IntervaloMinimoDePolling >= TimeSpan.FromSeconds(10)
              && politica.IntervaloMaximoDePolling >= politica.IntervaloMinimoDePolling
              && politica.IntervaloMaximoDePolling <= TimeSpan.FromHours(1), nameof(politica.IntervaloMaximoDePolling));
        Faixa(politica.LimiteDoLogEmBytes is >= 1024 and <= 1024 * 1024, nameof(politica.LimiteDoLogEmBytes));
        Faixa(politica.EsperaSemRunnerAteSinalizar >= TimeSpan.FromMinutes(1), nameof(politica.EsperaSemRunnerAteSinalizar));
        return politica;
    }
}
