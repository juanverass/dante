using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dante.Domain.Agentes;

namespace Dante.Domain.Execucoes;

// Forma de um campo de contrato de delegação. Texto: Minimo..Maximo caracteres depois de normalizado. ListaDeTextos:
// até Maximo itens de 1..MaximoPorItem caracteres. Opcao: um dos valores de Opcoes. HashSha256: 64 hexadecimais
// minúsculos. Referencias: até Maximo códigos de execução (E123 ou E123#2, a etapa 2) ou hashes de artefato.
// Evidencias: até Maximo objetos {tipo, referencia, trecho?}, com a referência conferida pelo tipo.
public enum TipoDeCampoDoContrato
{
    Texto,
    ListaDeTextos,
    Opcao,
    HashSha256,
    Referencias,
    Evidencias
}

public sealed record CampoDoContrato(string Nome, TipoDeCampoDoContrato Tipo, bool Obrigatorio, int Maximo,
    string Descricao, int Minimo = 1, int MaximoPorItem = 0, IReadOnlyList<string>? Opcoes = null);

// Contrato tipado de uma delegação (AD-70, #309): nome e versão imutáveis, campos de entrada e de saída declarados aqui,
// capacidade exigida do destinatário e validação feita no servidor. O catálogo é fechado (ContratosDeDelegacao): o
// agente escolhe um contrato da lista, nunca define um schema. Uma mudança de forma é um contrato novo (v2), nunca a
// edição deste. A forma canônica (chaves em ordem, todos os campos declarados presentes, texto normalizado) é o que
// se grava e se compara: dois pedidos iguais em conteúdo têm o mesmo hash em qualquer ordem de chaves.
// Nenhum campo é caminho absoluto, diretório ou comando: referências são códigos do ledger ou hashes, e a única
// referência a arquivo (evidência) é relativa ao repositório, sem "..", e só informativa.
public sealed class ContratoDeDelegacao
{
    public const int MaximoDoJson = 16_000;
    public const int TamanhoMaximoDaReferencia = 300;
    public const int TamanhoMaximoDoTrecho = 500;

    private static readonly Regex CodigoDeExecucao = new("^E[1-9][0-9]{0,17}(#[1-9][0-9]{0,5})?$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex ArquivoRelativo = new(
        "^[A-Za-z0-9._@+-]+(/[A-Za-z0-9._@+-]+)*(:[1-9][0-9]{0,6}(-[1-9][0-9]{0,6})?)?$", RegexOptions.CultureInvariant);
    private static readonly string[] TiposDeEvidencia = ["arquivo", "artefato", "execucao"];

    internal ContratoDeDelegacao(string nome, int versao, string descricao, IReadOnlyList<CapacidadeDoAgente> capacidades,
        IReadOnlyList<CampoDoContrato> entrada, IReadOnlyList<CampoDoContrato> saida)
    {
        Nome = nome;
        Versao = versao;
        Descricao = descricao;
        CapacidadesAceitas = capacidades;
        Entrada = entrada;
        Saida = saida;
    }

    public string Nome { get; }
    public int Versao { get; }
    public string Identificador => $"{Nome}.v{Versao.ToString(CultureInfo.InvariantCulture)}";
    public string Descricao { get; }
    // O destinatário precisa declarar ao menos uma delas (capacidades não concedem nada; aqui, só qualificam).
    public IReadOnlyList<CapacidadeDoAgente> CapacidadesAceitas { get; }
    public IReadOnlyList<CampoDoContrato> Entrada { get; }
    public IReadOnlyList<CampoDoContrato> Saida { get; }

    public bool Aceita(IReadOnlyList<CapacidadeDoAgente> capacidades)
    {
        foreach (var capacidade in CapacidadesAceitas)
            if (capacidades.Contains(capacidade)) return true;
        return false;
    }

    // Entrada validada e na forma canônica. ArgumentException com o motivo quando não confere.
    public string CanonizarEntrada(string json) => Canonizar(json, Entrada, "entrada");

    // Saída validada (inclusive as regras entre campos do contrato) e na forma canônica.
    public string CanonizarSaida(string json)
    {
        var canonico = Canonizar(json, Saida, "saída");
        if (Identificador == ContratosDeDelegacao.Revisao.Identificador)
        {
            using var documento = JsonDocument.Parse(canonico);
            var raiz = documento.RootElement;
            if (raiz.GetProperty("veredito").GetString() == "requer_correcoes" && raiz.GetProperty("itens").GetArrayLength() == 0)
                throw new ArgumentException("saída: requer_correcoes exige ao menos um item.");
        }
        return canonico;
    }

    public JsonObject SchemaDeEntrada() => Schema(Entrada);

    public JsonObject SchemaDeSaida() => Schema(Saida);

    public static string Hash(string canonico) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonico)));

    // Valor de um campo de texto da forma canônica (nulo quando ausente).
    public static string? Texto(string canonico, string campo)
    {
        using var documento = JsonDocument.Parse(canonico);
        return documento.RootElement.TryGetProperty(campo, out var valor) && valor.ValueKind == JsonValueKind.String
            ? valor.GetString() : null;
    }

    private static string Canonizar(string json, IReadOnlyList<CampoDoContrato> campos, string parte)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximoDoJson)
            throw new ArgumentException($"{parte}: JSON ausente ou acima de {MaximoDoJson} caracteres.");
        JsonDocument documento;
        try
        {
            documento = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new ArgumentException($"{parte}: JSON inválido.");
        }
        using (documento)
        {
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object) throw new ArgumentException($"{parte}: precisa ser um objeto.");
            var vistos = new HashSet<string>(StringComparer.Ordinal);
            foreach (var propriedade in raiz.EnumerateObject())
            {
                if (!vistos.Add(propriedade.Name)) throw new ArgumentException($"{parte}: campo repetido {propriedade.Name}.");
                if (Campo(campos, propriedade.Name) is null)
                    throw new ArgumentException($"{parte}: campo não permitido {propriedade.Name}.");
            }
            var ordenados = campos.ToArray();
            Array.Sort(ordenados, CompararPorNome);
            using var buffer = new MemoryStream();
            using (var escritor = new Utf8JsonWriter(buffer))
            {
                escritor.WriteStartObject();
                foreach (var campo in ordenados)
                {
                    var presente = raiz.TryGetProperty(campo.Nome, out var valor) && valor.ValueKind != JsonValueKind.Null;
                    if (!presente && campo.Obrigatorio) throw new ArgumentException($"{parte}: {campo.Nome} é obrigatório.");
                    escritor.WritePropertyName(campo.Nome);
                    EscreverValor(escritor, campo, presente ? valor : null, parte);
                }
                escritor.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    private static int CompararPorNome(CampoDoContrato a, CampoDoContrato b) => string.CompareOrdinal(a.Nome, b.Nome);

    private static CampoDoContrato? Campo(IReadOnlyList<CampoDoContrato> campos, string nome)
    {
        foreach (var campo in campos)
            if (campo.Nome == nome) return campo;
        return null;
    }

    private static void EscreverValor(Utf8JsonWriter escritor, CampoDoContrato campo, JsonElement? valor, string parte)
    {
        var nome = $"{parte}: {campo.Nome}";
        switch (campo.Tipo)
        {
            case TipoDeCampoDoContrato.Texto:
                if (valor is { } livre) escritor.WriteStringValue(TextoValido(livre, campo.Minimo, campo.Maximo, nome));
                else escritor.WriteNullValue();
                break;
            case TipoDeCampoDoContrato.Opcao:
                if (valor is { } opcao)
                {
                    var escolhida = opcao.ValueKind == JsonValueKind.String ? opcao.GetString()! : "";
                    if (campo.Opcoes is null || !campo.Opcoes.Contains(escolhida))
                        throw new ArgumentException($"{nome}: use {string.Join(", ", campo.Opcoes ?? [])}.");
                    escritor.WriteStringValue(escolhida);
                }
                else escritor.WriteNullValue();
                break;
            case TipoDeCampoDoContrato.HashSha256:
                if (valor is { } hash)
                {
                    var hex = hash.ValueKind == JsonValueKind.String ? hash.GetString()! : "";
                    if (!Sha256.IsMatch(hex)) throw new ArgumentException($"{nome}: SHA-256 em hexadecimal minúsculo.");
                    escritor.WriteStringValue(hex);
                }
                else escritor.WriteNullValue();
                break;
            case TipoDeCampoDoContrato.ListaDeTextos:
                escritor.WriteStartArray();
                foreach (var item in Itens(valor, campo, nome))
                    escritor.WriteStringValue(TextoValido(item, 1, campo.MaximoPorItem, nome));
                escritor.WriteEndArray();
                break;
            case TipoDeCampoDoContrato.Referencias:
                escritor.WriteStartArray();
                foreach (var item in Itens(valor, campo, nome))
                {
                    var referencia = item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
                    if (!CodigoDeExecucao.IsMatch(referencia) && !Sha256.IsMatch(referencia))
                        throw new ArgumentException($"{nome}: use o código da execução (E123 ou E123#2) ou o hash do artefato.");
                    escritor.WriteStringValue(referencia);
                }
                escritor.WriteEndArray();
                break;
            case TipoDeCampoDoContrato.Evidencias:
                escritor.WriteStartArray();
                foreach (var item in Itens(valor, campo, nome)) EscreverEvidencia(escritor, item, nome);
                escritor.WriteEndArray();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(campo), "Tipo de campo inválido.");
        }
    }

    private static JsonElement[] Itens(JsonElement? valor, CampoDoContrato campo, string nome)
    {
        if (valor is not { } lista) return [];
        if (lista.ValueKind != JsonValueKind.Array) throw new ArgumentException($"{nome}: precisa ser uma lista.");
        var itens = lista.EnumerateArray().ToArray();
        if (itens.Length > campo.Maximo) throw new ArgumentException($"{nome}: no máximo {campo.Maximo} itens.");
        if (campo.Obrigatorio && itens.Length == 0) throw new ArgumentException($"{nome}: ao menos um item.");
        return itens;
    }

    // Evidência: {tipo, referencia, trecho?} com a referência conferida pelo tipo. Arquivo é relativo ao repositório,
    // sem "." nem "..", sem barra inicial, "~" ou "\", com linha opcional (arquivo.cs:10 ou :10-20).
    private static void EscreverEvidencia(Utf8JsonWriter escritor, JsonElement item, string nome)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException($"{nome}: cada evidência é um objeto.");
        string? tipo = null, referencia = null;
        JsonElement? trecho = null;
        foreach (var propriedade in item.EnumerateObject())
        {
            switch (propriedade.Name)
            {
                case "tipo" when propriedade.Value.ValueKind == JsonValueKind.String && tipo is null:
                    tipo = propriedade.Value.GetString();
                    break;
                case "referencia" when propriedade.Value.ValueKind == JsonValueKind.String && referencia is null:
                    referencia = propriedade.Value.GetString();
                    break;
                case "trecho" when trecho is null:
                    if (propriedade.Value.ValueKind != JsonValueKind.Null) trecho = propriedade.Value;
                    break;
                default:
                    throw new ArgumentException($"{nome}: evidência só tem tipo, referencia e trecho.");
            }
        }
        if (tipo is null || !TiposDeEvidencia.Contains(tipo))
            throw new ArgumentException($"{nome}: tipo da evidência é arquivo, artefato ou execucao.");
        if (referencia is null || referencia.Length is 0 or > TamanhoMaximoDaReferencia)
            throw new ArgumentException($"{nome}: referência da evidência ausente ou longa demais.");
        var valida = tipo switch
        {
            "arquivo" => ArquivoRelativo.IsMatch(referencia) && SemSegmentoRelativo(referencia),
            "execucao" => CodigoDeExecucao.IsMatch(referencia),
            _ => Sha256.IsMatch(referencia)
        };
        if (!valida) throw new ArgumentException($"{nome}: referência inválida para evidência do tipo {tipo}.");
        escritor.WriteStartObject();
        escritor.WriteString("referencia", referencia);
        escritor.WriteString("tipo", tipo);
        if (trecho is { } texto) escritor.WriteString("trecho", TextoValido(texto, 1, TamanhoMaximoDoTrecho, nome));
        else escritor.WriteNull("trecho");
        escritor.WriteEndObject();
    }

    private static bool SemSegmentoRelativo(string referencia)
    {
        var arquivo = referencia.Split(':')[0];
        foreach (var segmento in arquivo.Split('/'))
            if (segmento is "." or "..") return false;
        return true;
    }

    // Texto normalizado: quebras de linha \n e sem espaços nas pontas; nenhum caractere de controle além de \n e \t.
    private static string TextoValido(JsonElement valor, int minimo, int maximo, string nome)
    {
        if (valor.ValueKind != JsonValueKind.String) throw new ArgumentException($"{nome}: precisa ser texto.");
        var texto = valor.GetString()!.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (texto.Length < minimo || texto.Length > maximo)
            throw new ArgumentException($"{nome}: entre {minimo} e {maximo} caracteres.");
        foreach (var caractere in texto)
            if (char.IsControl(caractere) && caractere is not ('\n' or '\t'))
                throw new ArgumentException($"{nome}: caractere de controle não permitido.");
        return texto;
    }

    private static JsonObject Schema(IReadOnlyList<CampoDoContrato> campos)
    {
        var propriedades = new JsonObject();
        var obrigatorios = new JsonArray();
        foreach (var campo in campos)
        {
            propriedades[campo.Nome] = SchemaDoCampo(campo);
            if (campo.Obrigatorio) obrigatorios.Add(campo.Nome);
        }
        return new JsonObject
        {
            ["type"] = "object", ["properties"] = propriedades, ["required"] = obrigatorios, ["additionalProperties"] = false
        };
    }

    private static JsonObject SchemaDoCampo(CampoDoContrato campo)
    {
        var schema = campo.Tipo switch
        {
            TipoDeCampoDoContrato.Texto => new JsonObject
            {
                ["type"] = "string", ["minLength"] = campo.Minimo, ["maxLength"] = campo.Maximo
            },
            TipoDeCampoDoContrato.Opcao => new JsonObject
            {
                ["type"] = "string", ["enum"] = new JsonArray((campo.Opcoes ?? []).Select(Valor).ToArray())
            },
            TipoDeCampoDoContrato.HashSha256 => new JsonObject { ["type"] = "string", ["pattern"] = "^[0-9a-f]{64}$" },
            TipoDeCampoDoContrato.ListaDeTextos => Lista(campo, new JsonObject
            {
                ["type"] = "string", ["minLength"] = 1, ["maxLength"] = campo.MaximoPorItem
            }),
            TipoDeCampoDoContrato.Referencias => Lista(campo, new JsonObject
            {
                ["type"] = "string", ["pattern"] = "^(E[1-9][0-9]{0,17}(#[1-9][0-9]{0,5})?|[0-9a-f]{64})$"
            }),
            _ => Lista(campo, new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("tipo", "referencia"),
                ["properties"] = new JsonObject
                {
                    ["tipo"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("arquivo", "artefato", "execucao") },
                    ["referencia"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = TamanhoMaximoDaReferencia },
                    ["trecho"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = TamanhoMaximoDoTrecho }
                }
            })
        };
        schema["description"] = campo.Descricao;
        return schema;
    }

    private static JsonNode? Valor(string texto) => JsonValue.Create(texto);

    private static JsonObject Lista(CampoDoContrato campo, JsonObject itens)
    {
        var lista = new JsonObject { ["type"] = "array", ["maxItems"] = campo.Maximo, ["items"] = itens };
        if (campo.Obrigatorio) lista["minItems"] = 1;
        return lista;
    }
}

// Catálogo fechado de contratos (AD-70). Um contrato novo entra aqui, com versão própria, e na constraint do banco.
public static class ContratosDeDelegacao
{
    public static ContratoDeDelegacao Consulta { get; } = new("consulta", 1,
        "Pergunta objetiva a outro agente, respondida com evidências rastreáveis.",
        [CapacidadeDoAgente.Pesquisar, CapacidadeDoAgente.Revisar],
        [
            new("pergunta", TipoDeCampoDoContrato.Texto, true, 2000, "O que o destinatário deve responder."),
            new("referencias", TipoDeCampoDoContrato.Referencias, false, 20,
                "Execuções (E123 ou E123#2) ou hashes de artefato que a resposta deve considerar.")
        ],
        [
            new("resposta", TipoDeCampoDoContrato.Texto, true, 4000, "Resposta objetiva, em português."),
            new("confianca", TipoDeCampoDoContrato.Opcao, true, 0, "Confiança na resposta.",
                Opcoes: ["alta", "media", "baixa"]),
            new("evidencias", TipoDeCampoDoContrato.Evidencias, false, 20,
                "Arquivo relativo ao repositório (com linha), execução ou hash de artefato que sustenta a resposta."),
            new("pendencias", TipoDeCampoDoContrato.ListaDeTextos, false, 10,
                "O que ficou sem resposta ou precisa de confirmação.", MaximoPorItem: 500)
        ]);

    public static ContratoDeDelegacao Revisao { get; } = new("revisao", 1,
        "Revisão de um artefato identificado pelo hash, com veredito estruturado.",
        [CapacidadeDoAgente.Revisar],
        [
            new("artefato", TipoDeCampoDoContrato.HashSha256, true, 64, "Hash do artefato a revisar."),
            new("foco", TipoDeCampoDoContrato.Texto, false, 2000, "Aspectos em que a revisão deve se concentrar."),
            new("referencias", TipoDeCampoDoContrato.Referencias, false, 20,
                "Execuções (E123 ou E123#2) ou hashes de artefato relacionados.")
        ],
        [
            new("veredito", TipoDeCampoDoContrato.Opcao, true, 0, "Veredito sobre o artefato.",
                Opcoes: ["aprovado", "requer_correcoes", "bloqueado"]),
            new("resumo", TipoDeCampoDoContrato.Texto, true, 2000, "Avaliação objetiva, em português."),
            new("itens", TipoDeCampoDoContrato.ListaDeTextos, false, 20,
                "Cada correção necessária; obrigatório com requer_correcoes.", MaximoPorItem: 500),
            new("evidencias", TipoDeCampoDoContrato.Evidencias, false, 20,
                "Arquivo relativo ao repositório (com linha), execução ou hash de artefato que sustenta o veredito.")
        ]);

    public static IReadOnlyList<ContratoDeDelegacao> Todos { get; } = [Consulta, Revisao];

    public static bool TentarObter(string? identificador, out ContratoDeDelegacao contrato)
    {
        foreach (var candidato in Todos)
            if (candidato.Identificador == identificador)
            {
                contrato = candidato;
                return true;
            }
        contrato = null!;
        return false;
    }
}
