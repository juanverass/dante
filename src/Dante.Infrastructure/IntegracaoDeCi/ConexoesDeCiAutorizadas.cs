using Dante.Application.IntegracaoDeCi;
using Dante.Infrastructure.Contextos;
using Microsoft.Extensions.Configuration;

namespace Dante.Infrastructure.IntegracaoDeCi;

// Conexões de CI autorizadas pelo dono do host (#237, AD-76): cada uma liga um id a uma referência secret://host/NOME
// (AD-10) e à lista fechada de aliases cadastrados que ela pode consultar. Configuração:
//
//   IntegracaoDeCi__Conexoes__<id>__Credencial   = secret://host/NOME_DA_VARIAVEL
//   IntegracaoDeCi__Conexoes__<id>__Repositorios = @alias1,@alias2
//
// Nada aqui guarda o valor da credencial: ele só é lido do ambiente do host no momento da chamada, depois de todas as
// conferências. Sem seção, nenhuma conexão existe e toda consulta responde NaoConfigurada. Configuração malformada é
// recusada na composição, nunca aproximada.
internal sealed class ConexoesDeCiAutorizadas
{
    internal const string Secao = "IntegracaoDeCi:Conexoes";
    private const string PrefixoDaReferencia = "secret://host/";

    private readonly IReadOnlyDictionary<string, Conexao> conexoes;
    private readonly Func<string, string?> lerVariavelDoHost;

    internal ConexoesDeCiAutorizadas(IEnumerable<(ConexaoDeCi Conexao, IReadOnlyCollection<string> Aliases)> conexoes,
        Func<string, string?>? lerVariavelDoHost = null)
    {
        var porId = new Dictionary<string, Conexao>(StringComparer.Ordinal);
        foreach (var (conexao, aliases) in conexoes)
        {
            if (aliases.Count == 0)
                throw new ArgumentException($"A conexão de CI {conexao.Id} não libera nenhum repositório.");
            if (!porId.TryAdd(conexao.Id, new Conexao(conexao,
                    aliases.Select(RepositoryRegistry.NormalizeAlias).ToHashSet(StringComparer.Ordinal))))
                throw new ArgumentException($"Conexão de CI duplicada: {conexao.Id}.");
        }

        this.conexoes = porId;
        this.lerVariavelDoHost = lerVariavelDoHost ?? Environment.GetEnvironmentVariable;
    }

    internal static ConexoesDeCiAutorizadas DaConfiguracao(IConfiguration configuracao) =>
        new(configuracao.GetSection(Secao).GetChildren().Select(secao =>
        {
            var conexao = new ConexaoDeCi(secao.Key, secao["Credencial"] ?? string.Empty);
            var aliases = (secao["Repositorios"] ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return (conexao, (IReadOnlyCollection<string>)aliases);
        }).ToArray());

    // A conexão existe, é exatamente a configurada e libera este alias. Devolve o nome da variável do host, sem lê-la.
    internal string ExigirAutorizada(RepositorioDeCi repositorio)
    {
        if (!conexoes.TryGetValue(repositorio.Conexao.Id, out var configurada))
            throw Falha(MotivoDaFalhaNaConsultaDeCi.NaoConfigurada, "A conexão de CI não está configurada no host.");
        if (configurada.Definicao != repositorio.Conexao)
            throw Falha(MotivoDaFalhaNaConsultaDeCi.NaoAutorizada, "A conexão de CI não corresponde à configurada no host.");
        if (!configurada.Aliases.Contains(repositorio.Alias.ToLowerInvariant()))
            throw Falha(MotivoDaFalhaNaConsultaDeCi.NaoAutorizada, "O repositório não está liberado para esta conexão de CI.");
        return configurada.Definicao.ReferenciaDaCredencial[PrefixoDaReferencia.Length..];
    }

    // Conexões que liberam o alias (#238), para montar a identidade de CI sem escolher entre duas. Não lê credencial.
    internal IReadOnlyList<ConexaoDeCi> QueLiberam(string alias)
    {
        var normalizado = RepositoryRegistry.NormalizeAlias(alias);
        return conexoes.Values.Where(c => c.Aliases.Contains(normalizado)).Select(c => c.Definicao)
            .OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
    }

    internal string LerCredencial(string variavelDoHost) =>
        lerVariavelDoHost(variavelDoHost) is { Length: > 0 } valor
            ? valor
            : throw Falha(MotivoDaFalhaNaConsultaDeCi.NaoConfigurada, "A credencial da conexão de CI não está definida no host.");

    private static FalhaNaConsultaDeCiException Falha(MotivoDaFalhaNaConsultaDeCi motivo, string mensagem) => new(motivo, mensagem);

    private sealed record Conexao(ConexaoDeCi Definicao, IReadOnlySet<string> Aliases);
}
