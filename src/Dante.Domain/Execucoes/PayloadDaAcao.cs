using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dante.Domain.Execucoes;

// Quem pede a ação (AD-72, #311). Dono: a pessoa dona da execução raiz, por um comando dela (a entrega do roteiro).
// Agente: a versão congelada do agente da sessão, identificada pelo canal da sessão, nunca por argumento (gateway, PR 2).
public sealed record SolicitanteDaAcao
{
    private SolicitanteDaAcao(string? canal, long? idDoUsuario, Guid? idVersaoDoAgente, string? hashDaVersaoDoAgente)
    {
        Canal = canal;
        IdDoUsuario = idDoUsuario;
        IdVersaoDoAgente = idVersaoDoAgente;
        HashDaVersaoDoAgente = hashDaVersaoDoAgente;
    }

    public string? Canal { get; }
    public long? IdDoUsuario { get; }
    public Guid? IdVersaoDoAgente { get; }
    public string? HashDaVersaoDoAgente { get; }
    public bool EhDono => IdVersaoDoAgente is null;

    public static SolicitanteDaAcao Dono(string canal, long idDoUsuario) =>
        string.IsNullOrWhiteSpace(canal) || canal.Length > Execucao.TamanhoMaximoDoCanal
            ? throw new ArgumentException("Canal inválido.", nameof(canal))
            : new(canal, idDoUsuario, null, null);

    public static SolicitanteDaAcao Agente(Guid idVersao, string hashDaVersao) =>
        idVersao == Guid.Empty ? throw new ArgumentException("Versão do agente obrigatória.", nameof(idVersao))
            : !PayloadDaAcao.HashValido(hashDaVersao) ? throw new ArgumentException("Hash inválido.", nameof(hashDaVersao))
            : new(null, null, idVersao, hashDaVersao);
}

// Payload canônico de uma ação protegida (AD-72, #311): ação, alvo, capacidades, escopo, execução, etapa, formato,
// parâmetros e solicitante, num JSON de chaves em ordem ordinal, sem espaços, com texto normalizado (NFC, \n, sem pontas).
// O hash é o SHA-256 dos bytes UTF-8 desta forma, calculado aqui e nunca recebido de fora; o banco recalcula e confere.
// Para executar, quem executa monta o payload de novo com os fatos atuais (versão, capacidades, conteúdo) e compara o
// hash: qualquer diferença invalida a aprovação. Os parâmetros são validados pelo catálogo: campo desconhecido, repetido,
// ausente ou fora da forma é recusado com o motivo.
public sealed class PayloadDaAcao
{
    public const int Formato = 1;
    public const int TamanhoDoHash = 64;
    public const int MaximoDoJson = 16_000;
    public const int MaximoDeCapacidades = 32;
    public const string Escopo = "UmaVez";

    private static readonly Regex Sha256 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex ShaDeCommit = new("^([0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant);
    private static readonly Regex Branch = new("^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Email = new("^[^\\s@<>\"]+@[^\\s@<>\"]+\\.[^\\s@<>\"]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Alias = new("^@[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Capacidade = new("^[a-z][a-z0-9_.:-]*$", RegexOptions.CultureInvariant);

    private PayloadDaAcao(DefinicaoDeAcao definicao, Guid idExecucao, Guid? idEtapa, string? alvo,
        SolicitanteDaAcao solicitante, string conteudo)
    {
        Definicao = definicao;
        IdExecucao = idExecucao;
        IdEtapa = idEtapa;
        Alvo = alvo;
        Solicitante = solicitante;
        Conteudo = conteudo;
        Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(conteudo)));
    }

    public DefinicaoDeAcao Definicao { get; }
    public Guid IdExecucao { get; }
    public Guid? IdEtapa { get; }
    public string? Alvo { get; }
    public SolicitanteDaAcao Solicitante { get; }
    public string Conteudo { get; }
    public string Hash { get; }

    // ArgumentException com o motivo quando algo não confere. A ação precisa ser protegida: com política Nunca não há
    // pedido nem payload a aprovar.
    public static PayloadDaAcao Montar(DefinicaoDeAcao acao, Guid idExecucao, Guid? idEtapa, string? alvo,
        IEnumerable<string> capacidades, SolicitanteDaAcao solicitante, string parametrosJson)
    {
        ArgumentNullException.ThrowIfNull(acao);
        ArgumentNullException.ThrowIfNull(capacidades);
        ArgumentNullException.ThrowIfNull(solicitante);
        if (!ReferenceEquals(CatalogoDeAcoes.Obter(acao.Nome), acao))
            throw new ArgumentException("Ação fora do catálogo.", nameof(acao));
        if (!acao.Protegida) throw new ArgumentException("A ação não exige aprovação; não há payload a aprovar.", nameof(acao));
        if (idExecucao == Guid.Empty) throw new ArgumentException("Execução obrigatória.", nameof(idExecucao));
        if (idEtapa == Guid.Empty) throw new ArgumentException("Etapa inválida.", nameof(idEtapa));
        if (acao.ExigeRepositorio != alvo is not null)
            throw new ArgumentException(acao.ExigeRepositorio ? "A ação exige o repositório alvo." : "A ação não tem alvo.",
                nameof(alvo));
        if (alvo is not null && (alvo.Length > 64 || !Alias.IsMatch(alvo)))
            throw new ArgumentException("Alvo inválido: use o alias do repositório (@alias).", nameof(alvo));
        var listaDeCapacidades = Capacidades(capacidades);
        var parametros = Parametros(acao, parametrosJson);

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("acao", acao.Nome);
            if (alvo is null) json.WriteNull("alvo");
            else json.WriteString("alvo", alvo);
            json.WriteStartArray("capacidades");
            foreach (var capacidade in listaDeCapacidades) json.WriteStringValue(capacidade);
            json.WriteEndArray();
            json.WriteString("escopo", Escopo);
            if (idEtapa is { } etapa) json.WriteString("etapa", Identificador(etapa));
            else json.WriteNull("etapa");
            json.WriteString("execucao", Identificador(idExecucao));
            json.WriteNumber("formato", Formato);
            json.WritePropertyName("parametros");
            json.WriteStartObject();
            foreach (var (nome, valor) in parametros) json.WriteString(nome, valor);
            json.WriteEndObject();
            json.WriteStartObject("solicitante");
            if (solicitante.EhDono)
            {
                json.WriteString("canal", solicitante.Canal);
                json.WriteString("tipo", "dono");
                json.WriteNumber("usuario", solicitante.IdDoUsuario!.Value);
            }
            else
            {
                json.WriteString("hash", solicitante.HashDaVersaoDoAgente);
                json.WriteString("tipo", "agente");
                json.WriteString("versao", Identificador(solicitante.IdVersaoDoAgente!.Value));
            }
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return new PayloadDaAcao(acao, idExecucao, idEtapa, alvo, solicitante, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    // Valor de um parâmetro da forma canônica gravada (nulo quando ausente).
    public static string? Parametro(string conteudo, string nome)
    {
        using var documento = JsonDocument.Parse(conteudo);
        return documento.RootElement.TryGetProperty("parametros", out var parametros) &&
            parametros.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.String
                ? valor.GetString() : null;
    }

    internal static bool HashValido(string? hash) => hash is { Length: TamanhoDoHash } && Sha256.IsMatch(hash);

    private static string Identificador(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static string[] Capacidades(IEnumerable<string> capacidades)
    {
        var lista = capacidades.ToArray();
        if (lista.Length > MaximoDeCapacidades)
            throw new ArgumentException($"No máximo {MaximoDeCapacidades} capacidades.", nameof(capacidades));
        var vistas = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capacidade in lista)
        {
            if (capacidade is null || capacidade.Length > 63 || !Capacidade.IsMatch(capacidade))
                throw new ArgumentException("Capacidade inválida.", nameof(capacidades));
            if (!vistas.Add(capacidade)) throw new ArgumentException($"Capacidade repetida: {capacidade}.", nameof(capacidades));
        }
        Array.Sort(lista, string.CompareOrdinal);
        return lista;
    }

    private static List<(string Nome, string Valor)> Parametros(DefinicaoDeAcao acao, string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximoDoJson)
            throw new ArgumentException($"parâmetros: JSON ausente ou acima de {MaximoDoJson} caracteres.", nameof(json));
        JsonDocument documento;
        try
        {
            documento = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new ArgumentException("parâmetros: JSON inválido.", nameof(json));
        }
        using (documento)
        {
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object) throw new ArgumentException("parâmetros: precisa ser um objeto.", nameof(json));
            var vistos = new HashSet<string>(StringComparer.Ordinal);
            foreach (var propriedade in raiz.EnumerateObject())
            {
                if (!vistos.Add(propriedade.Name))
                    throw new ArgumentException($"parâmetros: campo repetido {propriedade.Name}.", nameof(json));
                if (!Declarado(acao, propriedade.Name))
                    throw new ArgumentException($"parâmetros: campo não permitido {propriedade.Name}.", nameof(json));
            }
            var ordenados = acao.Parametros.OrderBy(p => p.Nome, StringComparer.Ordinal);
            var valores = new List<(string, string)>();
            foreach (var parametro in ordenados)
            {
                if (!raiz.TryGetProperty(parametro.Nome, out var valor) || valor.ValueKind != JsonValueKind.String)
                    throw new ArgumentException($"parâmetros: {parametro.Nome} é obrigatório e precisa ser texto.", nameof(json));
                valores.Add((parametro.Nome, Valor(parametro, valor.GetString()!)));
            }
            return valores;
        }
    }

    // Sem lambda que capture variável: o teste arquitetural do domínio lê também os tipos gerados pelo compilador.
    private static bool Declarado(DefinicaoDeAcao acao, string nome)
    {
        foreach (var parametro in acao.Parametros)
            if (parametro.Nome == nome) return true;
        return false;
    }

    private static string Valor(ParametroDaAcao parametro, string bruto)
    {
        var nome = $"parâmetros: {parametro.Nome}";
        var valor = Normalizar(bruto);
        if (valor.Length < parametro.Minimo || valor.Length > parametro.Maximo)
            throw new ArgumentException($"{nome} precisa ter de {parametro.Minimo} a {parametro.Maximo} caracteres.");
        var valido = parametro.Tipo switch
        {
            TipoDeParametroDaAcao.Texto => !valor.Any(char.IsControl),
            TipoDeParametroDaAcao.TextoLongo => !valor.Any(c => char.IsControl(c) && c != '\n'),
            TipoDeParametroDaAcao.HashSha256 => Sha256.IsMatch(valor),
            TipoDeParametroDaAcao.ShaDeCommit => ShaDeCommit.IsMatch(valor),
            TipoDeParametroDaAcao.NomeDeBranch => BranchValida(valor),
            TipoDeParametroDaAcao.Email => Email.IsMatch(valor),
            _ => false
        };
        return valido ? valor : throw new ArgumentException($"{nome} fora da forma {parametro.Tipo}.");
    }

    private static bool BranchValida(string valor) =>
        Branch.IsMatch(valor) && !valor.Contains("..", StringComparison.Ordinal) &&
        !valor.Contains("//", StringComparison.Ordinal) && !valor.Contains("/.", StringComparison.Ordinal) &&
        !valor.EndsWith('/') && !valor.EndsWith('.') && !valor.EndsWith(".lock", StringComparison.Ordinal);

    private static string Normalizar(string texto) =>
        texto.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Trim();
}
