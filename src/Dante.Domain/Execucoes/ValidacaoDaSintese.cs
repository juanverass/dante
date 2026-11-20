using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dante.Domain.Execucoes;

// Uma unidade da trilha mecânica contra a qual a síntese é conferida (AD-75, D7): a revisão do plano, o id da unidade e
// o hash do resultado persistido da delegação dela, nulo quando a unidade terminou sem resposta ou foi ignorada.
public sealed record UnidadeDaTrilha(int Revisao, string Unidade, string? HashDoResultado)
{
    public bool Respondida => HashDoResultado is not null;
}

// Síntese validada: a forma canônica e o SHA-256 dela, calculado aqui.
public sealed record SinteseValidada(string Conteudo, string Hash);

public enum MotivoDaRecusaDaSintese
{
    FormaInvalida,
    TextoInvalido,
    ItensDemais,
    CitacaoInvalida,
    UnidadeInexistente,
    // Conclusão ou divergência que cita unidade sem resultado persistido: só um inconclusivo pode citá-la.
    SemResultado,
    // O hash citado não é o do resultado persistido daquela unidade.
    HashDivergente,
    // Divergência é entre resultados: cita ao menos duas unidades.
    DivergenciaSemPar,
    // Alguma unidade da trilha não foi citada em nenhum item.
    CoberturaIncompleta
}

// Referência nula: o problema é da síntese inteira.
public sealed record RecusaDaSintese(MotivoDaRecusaDaSintese Motivo, string? Referencia, string Mensagem);

public sealed record ResultadoDaValidacaoDaSintese(SinteseValidada? Sintese, IReadOnlyList<RecusaDaSintese> Recusas)
{
    public bool Valido => Sintese is not null;
}

// Validação da síntese do coordenador (AD-75, D7, #310 PR 3): pura e determinística, sem banco, relógio nem cadastro.
// Confere a ESTRUTURA contra a trilha mecânica gerada pelo servidor, nunca o mérito do que o coordenador afirma:
// * toda unidade de todas as revisões da trilha é citada ao menos uma vez (cobertura de 100%);
// * conclusões e divergências só citam unidades com resultado persistido, pelo hash dele (ao menos 12 caracteres do
//   SHA-256 gravado): a citação aponta para um resultado verificável, não para texto livre;
// * divergência cita ao menos duas unidades: a síntese não esconde o desacordo nem inventa consenso;
// * unidade sem resposta ou ignorada só entra em inconclusivos, e sem hash;
// * campos desconhecidos (inclusive um hash da síntese) são recusados; o hash é calculado aqui.
//
// Proposta: {"resumo","conclusoes":[item],"divergencias":[item],"inconclusivos":[item]}, item = {"texto","citacoes":
// [{"revisao","unidade","hash"?}]}; as três listas são opcionais. Normalização: textos em NFC, quebras "\n", sem
// pontas; citações de um item sem repetição, em ordem (revisão, unidade), com o hash completo da trilha. Forma canônica:
// {"conclusoes","divergencias","formato":"1","inconclusivos","resumo"}, itens na ordem proposta e chaves em ordem
// alfabética; hash = SHA-256 dos bytes UTF-8 dela.
public static class ValidacaoDaSintese
{
    public const int VersaoDoFormato = 1;
    public const int TamanhoMaximoDaProposta = 32_000;
    public const int TamanhoMaximoDoTexto = 2000;
    public const int MaximoDeItensPorLista = 20;
    public const int MaximoDeCitacoesPorItem = 16;
    public const int TamanhoMinimoDoHashCitado = 12;
    // Uma síntese inválida volta ao coordenador uma vez; inválida de novo, a execução termina sem síntese, com a trilha.
    public const int CorrecoesPermitidas = 1;

    private static readonly Regex FormatoDoHash = new("^[0-9a-f]{12,64}$", RegexOptions.CultureInvariant);
    private static readonly string[] Listas = ["conclusoes", "divergencias", "inconclusivos"];
    private static readonly string[] CamposDaRaiz = ["conclusoes", "divergencias", "inconclusivos", "resumo"];
    private static readonly string[] CamposDoItem = ["citacoes", "texto"];
    private static readonly string[] CamposDaCitacao = ["hash", "revisao", "unidade"];

    private sealed record Citacao(int Revisao, string Unidade, string? Hash);

    private sealed record Item(string Texto, List<Citacao> Citacoes);

    public static ResultadoDaValidacaoDaSintese Validar(string? proposta, IReadOnlyList<UnidadeDaTrilha> trilha)
    {
        ArgumentNullException.ThrowIfNull(trilha);
        if (trilha.Count == 0) throw new ArgumentException("A trilha não tem unidades a sintetizar.", nameof(trilha));
        var recusas = new List<RecusaDaSintese>();
        if (string.IsNullOrWhiteSpace(proposta) || proposta.Length > TamanhoMaximoDaProposta)
            return Recusado(recusas, MotivoDaRecusaDaSintese.FormaInvalida, null,
                $"Síntese ausente ou acima de {TamanhoMaximoDaProposta} caracteres.");
        JsonDocument documento;
        try
        {
            documento = JsonDocument.Parse(proposta);
        }
        catch (JsonException)
        {
            return Recusado(recusas, MotivoDaRecusaDaSintese.FormaInvalida, null, "A síntese não é um JSON válido.");
        }
        using (documento)
        {
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object || !SoCampos(raiz, CamposDaRaiz))
                return Recusado(recusas, MotivoDaRecusaDaSintese.FormaInvalida, null,
                    "A síntese é um objeto com resumo, conclusoes, divergencias e inconclusivos; nenhum outro campo.");
            var resumo = raiz.TryGetProperty("resumo", out var valorDoResumo) && valorDoResumo.ValueKind == JsonValueKind.String
                ? ValidacaoDoPlano.NormalizarTexto(valorDoResumo.GetString()!) : "";
            if (resumo.Length == 0 || resumo.Length > TamanhoMaximoDoTexto)
                recusas.Add(new(MotivoDaRecusaDaSintese.TextoInvalido, "resumo",
                    $"resumo é obrigatório, com até {TamanhoMaximoDoTexto} caracteres."));

            var lidas = new List<Item>[Listas.Length];
            var citadas = new HashSet<(int, string)>();
            for (var l = 0; l < Listas.Length; l++)
                lidas[l] = LerLista(raiz, Listas[l], trilha, citadas, recusas);
            foreach (var unidade in trilha)
                if (!citadas.Contains((unidade.Revisao, unidade.Unidade)))
                    recusas.Add(new(MotivoDaRecusaDaSintese.CoberturaIncompleta, ChaveDaUnidade(unidade.Revisao, unidade.Unidade),
                        $"A unidade {unidade.Unidade} da revisão {unidade.Revisao} não foi citada; toda unidade entra numa " +
                        "conclusão, divergência ou inconclusivo" + (unidade.Respondida ? "." : " (sem resposta: inconclusivo).")));
            if (recusas.Count > 0) return new(null, recusas.AsReadOnly());
            var conteudo = Escrever(resumo, lidas);
            return new(new SinteseValidada(conteudo, ValidacaoDoPlano.Hash(conteudo)), []);
        }
    }

    // Relê uma forma canônica gravada contra a mesma trilha: aceita só o que Validar escreveria (o texto reescrito
    // precisa ser idêntico). Um conteúdo adulterado ou de outra versão do formato é recusado.
    public static SinteseValidada LerCanonico(string conteudo, IReadOnlyList<UnidadeDaTrilha> trilha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conteudo);
        var resultado = Validar(SemFormato(conteudo), trilha);
        if (!resultado.Valido || !string.Equals(resultado.Sintese!.Conteudo, conteudo, StringComparison.Ordinal))
            throw new InvalidOperationException("A síntese gravada não está na forma canônica.");
        return resultado.Sintese;
    }

    private static string SemFormato(string conteudo)
    {
        try
        {
            using var documento = JsonDocument.Parse(conteudo);
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object || !raiz.TryGetProperty("formato", out var formato) ||
                formato.GetString() != VersaoDoFormato.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Formato de síntese desconhecido.");
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                foreach (var propriedade in raiz.EnumerateObject())
                    if (propriedade.Name != "formato") propriedade.WriteTo(json);
                json.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException("A síntese gravada não está na forma canônica.", exception);
        }
    }

    private static List<Item> LerLista(JsonElement raiz, string nome, IReadOnlyList<UnidadeDaTrilha> trilha,
        HashSet<(int, string)> citadas, List<RecusaDaSintese> recusas)
    {
        var itens = new List<Item>();
        if (!raiz.TryGetProperty(nome, out var lista) || lista.ValueKind == JsonValueKind.Null) return itens;
        if (lista.ValueKind != JsonValueKind.Array)
        {
            recusas.Add(new(MotivoDaRecusaDaSintese.FormaInvalida, nome, $"{nome} é uma lista de itens."));
            return itens;
        }
        if (lista.GetArrayLength() > MaximoDeItensPorLista)
        {
            recusas.Add(new(MotivoDaRecusaDaSintese.ItensDemais, nome, $"No máximo {MaximoDeItensPorLista} itens em {nome}."));
            return itens;
        }
        var conclusivo = nome != "inconclusivos";
        var posicao = 0;
        foreach (var elemento in lista.EnumerateArray())
        {
            var rotulo = $"{nome}#{(++posicao).ToString(CultureInfo.InvariantCulture)}";
            if (elemento.ValueKind != JsonValueKind.Object || !SoCampos(elemento, CamposDoItem))
            {
                recusas.Add(new(MotivoDaRecusaDaSintese.FormaInvalida, rotulo,
                    "Cada item é um objeto com texto e citacoes; nenhum outro campo."));
                continue;
            }
            var texto = elemento.TryGetProperty("texto", out var valorDoTexto) && valorDoTexto.ValueKind == JsonValueKind.String
                ? ValidacaoDoPlano.NormalizarTexto(valorDoTexto.GetString()!) : "";
            if (texto.Length == 0 || texto.Length > TamanhoMaximoDoTexto)
                recusas.Add(new(MotivoDaRecusaDaSintese.TextoInvalido, rotulo,
                    $"texto é obrigatório, com até {TamanhoMaximoDoTexto} caracteres."));
            if (!elemento.TryGetProperty("citacoes", out var valorDasCitacoes) || valorDasCitacoes.ValueKind != JsonValueKind.Array ||
                valorDasCitacoes.GetArrayLength() == 0 || valorDasCitacoes.GetArrayLength() > MaximoDeCitacoesPorItem)
            {
                recusas.Add(new(MotivoDaRecusaDaSintese.CitacaoInvalida, rotulo,
                    $"citacoes é uma lista de 1 a {MaximoDeCitacoesPorItem} unidades da trilha."));
                continue;
            }
            var citacoes = new List<Citacao>();
            foreach (var valor in valorDasCitacoes.EnumerateArray())
                if (LerCitacao(valor, rotulo, conclusivo, trilha, recusas) is { } citacao)
                {
                    var repetida = false;
                    foreach (var anterior in citacoes)
                        repetida |= anterior.Revisao == citacao.Revisao && anterior.Unidade == citacao.Unidade;
                    if (repetida) continue;
                    citacoes.Add(citacao);
                    citadas.Add((citacao.Revisao, citacao.Unidade));
                }
            if (nome == "divergencias" && citacoes.Count < 2)
                recusas.Add(new(MotivoDaRecusaDaSintese.DivergenciaSemPar, rotulo,
                    "Uma divergência cita ao menos duas unidades cujos resultados divergem."));
            citacoes.Sort(CompararCitacoes);
            itens.Add(new Item(texto, citacoes));
        }
        return itens;
    }

    private static Citacao? LerCitacao(JsonElement valor, string rotulo, bool conclusivo, IReadOnlyList<UnidadeDaTrilha> trilha,
        List<RecusaDaSintese> recusas)
    {
        if (valor.ValueKind != JsonValueKind.Object || !SoCampos(valor, CamposDaCitacao) ||
            !valor.TryGetProperty("revisao", out var revisao) || !revisao.TryGetInt32(out var numero) ||
            !valor.TryGetProperty("unidade", out var unidade) || unidade.ValueKind != JsonValueKind.String)
        {
            recusas.Add(new(MotivoDaRecusaDaSintese.CitacaoInvalida, rotulo,
                "Cada citação é {\"revisao\": número, \"unidade\": id, \"hash\": hash do resultado}; nenhum outro campo."));
            return null;
        }
        var id = unidade.GetString()!;
        UnidadeDaTrilha? daTrilha = null;
        foreach (var candidata in trilha)
            if (candidata.Revisao == numero && candidata.Unidade == id) daTrilha = candidata;
        var referencia = $"{rotulo}:{ChaveDaUnidade(numero, id)}";
        if (daTrilha is null)
        {
            recusas.Add(new(MotivoDaRecusaDaSintese.UnidadeInexistente, referencia,
                $"A unidade {id} da revisão {numero} não está na trilha desta execução."));
            return null;
        }
        string? hash = null;
        if (valor.TryGetProperty("hash", out var valorDoHash) && valorDoHash.ValueKind != JsonValueKind.Null)
        {
            if (valorDoHash.ValueKind != JsonValueKind.String)
            {
                recusas.Add(new(MotivoDaRecusaDaSintese.CitacaoInvalida, referencia, "hash é texto."));
                return null;
            }
            hash = valorDoHash.GetString()!.Trim().ToLowerInvariant();
        }
        if (!daTrilha.Respondida)
        {
            if (conclusivo)
            {
                recusas.Add(new(MotivoDaRecusaDaSintese.SemResultado, referencia,
                    $"A unidade {id} da revisão {numero} não tem resultado persistido: cite-a só em inconclusivos."));
                return null;
            }
            if (hash is not null)
            {
                recusas.Add(new(MotivoDaRecusaDaSintese.HashDivergente, referencia,
                    $"A unidade {id} da revisão {numero} não tem resultado; a citação não leva hash."));
                return null;
            }
            return new Citacao(numero, id, null);
        }
        if (hash is null || !FormatoDoHash.IsMatch(hash) ||
            !daTrilha.HashDoResultado!.StartsWith(hash, StringComparison.Ordinal))
        {
            recusas.Add(new(MotivoDaRecusaDaSintese.HashDivergente, referencia,
                $"Cite a unidade {id} da revisão {numero} com o hash do resultado persistido (ao menos " +
                $"{TamanhoMinimoDoHashCitado} caracteres)."));
            return null;
        }
        return new Citacao(numero, id, daTrilha.HashDoResultado);
    }

    private static int CompararCitacoes(Citacao a, Citacao b)
    {
        var porRevisao = a.Revisao.CompareTo(b.Revisao);
        return porRevisao != 0 ? porRevisao : string.CompareOrdinal(a.Unidade, b.Unidade);
    }

    private static string Escrever(string resumo, List<Item>[] listas)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            for (var l = 0; l < Listas.Length; l++)
            {
                if (l == 2) json.WriteString("formato", VersaoDoFormato.ToString(CultureInfo.InvariantCulture));
                json.WriteStartArray(Listas[l]);
                foreach (var item in listas[l])
                {
                    json.WriteStartObject();
                    json.WriteStartArray("citacoes");
                    foreach (var citacao in item.Citacoes)
                    {
                        json.WriteStartObject();
                        if (citacao.Hash is null) json.WriteNull("hash");
                        else json.WriteString("hash", citacao.Hash);
                        json.WriteNumber("revisao", citacao.Revisao);
                        json.WriteString("unidade", citacao.Unidade);
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                    json.WriteString("texto", item.Texto);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            }
            json.WriteString("resumo", resumo);
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string ChaveDaUnidade(int revisao, string unidade) =>
        $"r{revisao.ToString(CultureInfo.InvariantCulture)}/{unidade}";

    private static bool SoCampos(JsonElement objeto, string[] permitidos)
    {
        var vistos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var propriedade in objeto.EnumerateObject())
            if (Array.IndexOf(permitidos, propriedade.Name) < 0 || !vistos.Add(propriedade.Name)) return false;
        return true;
    }

    private static ResultadoDaValidacaoDaSintese Recusado(List<RecusaDaSintese> recusas, MotivoDaRecusaDaSintese motivo,
        string? referencia, string mensagem)
    {
        recusas.Add(new(motivo, referencia, mensagem));
        return new(null, recusas.AsReadOnly());
    }
}
