using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dante.Domain.Agentes;

// O que uma versão de agente define (AD-68), já normalizado: listas ordenadas e sem repetição, aliases em minúsculas,
// instruções sem espaços nas pontas e com quebras de linha \n. O hash é o SHA-256 do JSON canônico (chaves em ordem
// fixa, enums pelo nome): duas configurações iguais têm o mesmo hash em qualquer máquina e em qualquer ordem de
// entrada. Nada aqui é credencial; a configuração não tem campo para segredo.
public sealed class ConfiguracaoDoAgente
{
    public const int VersaoDoFormato = 1;
    public const int TamanhoMaximoDasInstrucoes = 4000;
    public const int TamanhoMaximoDoModelo = 100;
    public const int MaximoDeRepositorios = 20;
    // Concessão de qualquer repositório cadastrado; sem ela, só os aliases listados (lista vazia: só o workspace geral).
    public const string QualquerRepositorio = "*";

    private static readonly Regex FormatoDoAlias = new("^@[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);

    public ConfiguracaoDoAgente(AgentKind driver, AgentPermissionProfile perfilPadrao, AgentPermissionProfile perfilMaximo,
        bool podeEscrever, string? modelo = null, string? esforco = null, string? instrucoes = null,
        IEnumerable<CapacidadeDoAgente>? capacidades = null, IEnumerable<FerramentaDoDante>? ferramentas = null,
        IEnumerable<string>? repositorios = null, LimitesDoAgente? limites = null)
    {
        if (!Enum.IsDefined(driver)) throw new ArgumentOutOfRangeException(nameof(driver), "Driver inválido.");
        if (!Enum.IsDefined(perfilPadrao)) throw new ArgumentOutOfRangeException(nameof(perfilPadrao), "Perfil inválido.");
        if (!Enum.IsDefined(perfilMaximo)) throw new ArgumentOutOfRangeException(nameof(perfilMaximo), "Perfil inválido.");
        if (Amplitude(perfilPadrao) > Amplitude(perfilMaximo))
            throw new ArgumentException("O perfil padrão não pode passar do perfil máximo.", nameof(perfilPadrao));
        // Sem escrita, só leitura: o máximo (e portanto o padrão) é plan.
        if (!podeEscrever && perfilMaximo != AgentPermissionProfile.Plan)
            throw new ArgumentException("Agente sem escrita só pode ter perfil máximo plan.", nameof(perfilMaximo));
        Driver = driver;
        PerfilPadrao = perfilPadrao;
        PerfilMaximo = perfilMaximo;
        PodeEscrever = podeEscrever;
        Modelo = Nome(modelo, nameof(modelo));
        Esforco = Nome(esforco, nameof(esforco));
        Instrucoes = NormalizarInstrucoes(instrucoes);
        Capacidades = Ordenadas(capacidades, nameof(capacidades));
        if (Capacidades.Contains(CapacidadeDoAgente.Implementar) && !podeEscrever)
            throw new ArgumentException("A capacidade implementar exige um agente com escrita.", nameof(capacidades));
        Ferramentas = Ordenadas(ferramentas, nameof(ferramentas));
        Repositorios = NormalizarRepositorios(repositorios);
        Limites = limites ?? LimitesDoAgente.Nenhum;
        Limites.Validar();
    }

    public AgentKind Driver { get; }
    public string? Modelo { get; }
    public string? Esforco { get; }
    public string? Instrucoes { get; }
    public IReadOnlyList<CapacidadeDoAgente> Capacidades { get; }
    public AgentPermissionProfile PerfilPadrao { get; }
    public AgentPermissionProfile PerfilMaximo { get; }
    public bool PodeEscrever { get; }
    public IReadOnlyList<FerramentaDoDante> Ferramentas { get; }
    public IReadOnlyList<string> Repositorios { get; }
    public LimitesDoAgente Limites { get; }

    public bool QualquerRepositorioPermitido => Repositorios.Contains(QualquerRepositorio);

    // Quanto um perfil permite: plan só lê, manual escreve pedindo aprovação, auto escreve decidindo sozinho.
    public static int Amplitude(AgentPermissionProfile perfil) => perfil switch
    {
        AgentPermissionProfile.Plan => 0,
        AgentPermissionProfile.Manual => 1,
        AgentPermissionProfile.Auto => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(perfil), "Perfil inválido.")
    };

    // Uma sessão pode restringir o perfil da versão, nunca passar do máximo.
    public bool PermitePerfil(AgentPermissionProfile perfil) =>
        Enum.IsDefined(perfil) && Amplitude(perfil) <= Amplitude(PerfilMaximo);

    public bool PermiteRepositorio(string alias) =>
        QualquerRepositorioPermitido || Repositorios.Contains(alias.ToLowerInvariant());

    public bool PermiteFerramenta(FerramentaDoDante ferramenta) => Ferramentas.Contains(ferramenta);

    public string JsonCanonico()
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartArray("capacidades");
            foreach (var capacidade in Capacidades) json.WriteStringValue(capacidade.ToString());
            json.WriteEndArray();
            json.WriteString("driver", Driver.ToString());
            json.WriteString("esforco", Esforco);
            json.WriteStartArray("ferramentas");
            foreach (var ferramenta in Ferramentas) json.WriteStringValue(ferramenta.ToString());
            json.WriteEndArray();
            json.WriteString("formato", VersaoDoFormato.ToString(System.Globalization.CultureInfo.InvariantCulture));
            json.WriteString("instrucoes", Instrucoes);
            json.WriteStartObject("limites");
            Numero(json, "execucoesSimultaneas", Limites.ExecucoesSimultaneas);
            Numero(json, "minutosPorEtapa", Limites.MinutosPorEtapa);
            Numero(json, "tokensPorExecucao", Limites.TokensPorExecucao);
            Numero(json, "turnosPorEtapa", Limites.TurnosPorEtapa);
            json.WriteEndObject();
            json.WriteString("modelo", Modelo);
            json.WriteString("perfilMaximo", PerfilMaximo.ToString());
            json.WriteString("perfilPadrao", PerfilPadrao.ToString());
            json.WriteBoolean("podeEscrever", PodeEscrever);
            json.WriteStartArray("repositorios");
            foreach (var repositorio in Repositorios) json.WriteStringValue(repositorio);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonCanonico())));

    private static void Numero(Utf8JsonWriter json, string nome, long? valor)
    {
        if (valor is { } numero) json.WriteNumber(nome, numero);
        else json.WriteNull(nome);
    }

    // Só a forma: quem confirma que o modelo e o esforço existem é o catálogo do driver, ao salvar e ao iniciar.
    private static string? Nome(string? valor, string parametro)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;
        if (valor.Length > TamanhoMaximoDoModelo || valor.Any(char.IsWhiteSpace) || valor[0] == '-')
            throw new ArgumentException("Nome inválido.", parametro);
        return valor;
    }

    private static string? NormalizarInstrucoes(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;
        var texto = valor.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (texto.Length > TamanhoMaximoDasInstrucoes)
            throw new ArgumentException($"Instruções passam de {TamanhoMaximoDasInstrucoes} caracteres.", nameof(valor));
        return texto;
    }

    private static IReadOnlyList<T> Ordenadas<T>(IEnumerable<T>? valores, string parametro) where T : struct, Enum
    {
        var lista = (valores ?? []).Distinct().ToArray();
        if (lista.Any(valor => !Enum.IsDefined(valor))) throw new ArgumentOutOfRangeException(parametro, "Valor inválido.");
        Array.Sort(lista);
        return lista;
    }

    private static IReadOnlyList<string> NormalizarRepositorios(IEnumerable<string>? valores)
    {
        var lista = (valores ?? []).Select(valor => valor?.Trim().ToLowerInvariant() ?? "")
            .Distinct(StringComparer.Ordinal).ToArray();
        if (lista.Any(valor => valor != QualquerRepositorio && !FormatoDoAlias.IsMatch(valor)))
            throw new ArgumentException("Repositório inválido: use @alias ou *.", nameof(valores));
        if (lista.Contains(QualquerRepositorio)) return [QualquerRepositorio];
        if (lista.Length > MaximoDeRepositorios)
            throw new ArgumentException($"No máximo {MaximoDeRepositorios} repositórios.", nameof(valores));
        Array.Sort(lista, StringComparer.Ordinal);
        return lista;
    }
}
