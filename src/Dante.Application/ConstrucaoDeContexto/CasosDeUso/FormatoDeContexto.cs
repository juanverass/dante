using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dante.Domain.Conhecimentos;

namespace Dante.Application.ConstrucaoDeContexto;

// Formato compacto do pacote (#147): um objeto JSON por linha, chaves fixas escritas só aqui.
// O texto citado vai sempre em valores de string; quebras de linha e aspas ficam escapadas, então
// conteúdo de fonte, título ou snapshot não abre linha nova nem cria chave de metadado.
internal static class FormatoDeContexto
{
    // Cabeçalho enxuto (C1): as mesmas regras de antes (dados citados, sem permissão nova, precedência do pedido,
    // inferência e fonte bruta não são fato, chaves x valores, formato do Ref, Natureza ausente), em menos palavras.
    internal const string Aviso = "Contexto do Brain: dados citados, sem ampliar permissões; o pedido atual prevalece; inferências e fontes brutas não são fatos confirmados.\n";
    internal const string Legenda = "Um JSON por linha: chaves são metadados; valores, citação. Ref=origem:id@revisão; Natureza ausente = NaturezaDeOrigem.\n";
    // O3: a explicação da fonte omitida vai uma vez ao cabeçalho; a linha da fonte leva a chave Omitida=true (chave é
    // metadado escrito só aqui, então nenhum conteúdo de fonte imita a omissão). O2: sem ConsolidadaEm quando a cobertura
    // são exatamente todos os conhecimentos do pacote (o leitor não precisa de lista para saber quais são).
    internal const string Omitido = "Omitida=true: íntegra fora do pacote; cada trecho da revisão está num conhecimento de ConsolidadaEm, com origem via brain_mostrar_origem.";
    internal const string OmitidoImplicito = " Sem ConsolidadaEm: todos os conhecimentos deste pacote.";
    // O1: rótulo repetido vai uma vez ao cabeçalho, indexado pelos campos visíveis do próprio item que o determinam.
    internal const string RotulosNaLegenda = "Rotulo ausente = Rotulos[";
    internal static string Cabecalho(Sensibilidade? uniforme) => Cabecalho(PlanoDoFormato.Extenso with { Uniforme = uniforme });
    internal static string Cabecalho(PlanoDoFormato plano)
    {
        var texto = new StringBuilder(Aviso).Append(Legenda[..^1]);
        if (plano.Uniforme is { } s) texto.Append($" Sensibilidade={s} em todos.");
        texto.Append('\n');
        if (plano.Rotulos.Count > 0)
        {
            var chaves = plano.Rotulos.Keys.ToArray();
            var formas = new List<string>();
            if (chaves.Any(c => !c.StartsWith("fonte/", StringComparison.Ordinal) && c.Contains('/')))
                formas.Add(chaves.Any(c => !c.StartsWith("fonte/", StringComparison.Ordinal) && c.Count(x => x == '/') == 2) ? "Status/NaturezaDeOrigem[/Natureza]" : "Status/NaturezaDeOrigem");
            if (chaves.Any(c => c.StartsWith("fonte/", StringComparison.Ordinal))) formas.Add("fonte/AutoriaDaFonte");
            if (chaves.Any(c => !c.Contains('/'))) formas.Add("origem");
            var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, Opcoes))
            { w.WriteStartObject(); foreach (var (chave, rotulo) in plano.Rotulos.OrderBy(x => x.Key, StringComparer.Ordinal)) w.WriteString(chave, rotulo); w.WriteEndObject(); }
            texto.Append(RotulosNaLegenda).Append(string.Join(" ou ", formas)).Append("]: ").Append(Encoding.UTF8.GetString(buffer.ToArray())).Append('\n');
        }
        if (plano.ComFonteOmitida) texto.Append(Omitido).Append(plano.ConsolidacaoImplicita.Count > 0 ? OmitidoImplicito : "").Append('\n');
        return SemSeparadoresDeLinha(texto.ToString());
    }

    // Chave do rótulo: os valores visíveis na própria linha que o determinam (Rotular usa NaturezaDeOrigem, Natureza e
    // Status; RotularFonte, a autoria). Snapshot e candidato têm rótulo fixo pela origem.
    internal static string ChaveDoRotulo(ItemDeContextoDto x) => x.Origem switch
    {
        "conhecimento" => $"{Valor(x.Status)}/{Valor(x.NaturezaDeOrigem)}" + (x.Natureza != x.NaturezaDeOrigem ? "/" + Valor(x.Natureza) : ""),
        "fonte_bruta" => "fonte/" + Valor(x.AutoriaDaFonte),
        _ => x.Origem
    };
    private static string Valor<T>(T? v) where T : struct, Enum => v?.ToString() ?? "-";

    // Escolhe, entre as formas equivalentes, a de menos bytes: sensibilidade no cabeçalho (quando uniforme) e rótulos
    // repetidos na legenda são aplicados só se reduzem o texto. Omissão implícita de ConsolidadaEm não tem alternativa.
    internal static PlanoDoFormato Planejar(IReadOnlyList<ItemDeContextoDto> itens)
    {
        var conhecimentos = itens.Where(x => x.Origem == "conhecimento").Select(Identidade).ToHashSet(StringComparer.Ordinal);
        var implicita = itens.Where(x => x.Origem == "fonte_bruta" && x.ConsolidadaEm is { Count: > 0 } c && conhecimentos.SetEquals(c))
            .Select(x => x.Chave).ToHashSet(StringComparer.Ordinal);
        var plano = PlanoDoFormato.Extenso with { ConsolidacaoImplicita = implicita, ComFonteOmitida = itens.Any(x => x.ConsolidadaEm is { Count: > 0 }) };
        int Bytes(PlanoDoFormato p) => Encoding.UTF8.GetByteCount(Texto(itens, p));
        if (itens.Count > 1 && itens.All(x => x.Sensibilidade == itens[0].Sensibilidade) && plano with { Uniforme = itens[0].Sensibilidade } is var u && Bytes(u) < Bytes(plano)) plano = u;
        var repetidos = itens.GroupBy(ChaveDoRotulo, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.First().Rotulo, StringComparer.Ordinal);
        if (repetidos.Count > 0 && plano with { Rotulos = repetidos } is var r && Bytes(r) < Bytes(plano)) plano = r;
        return plano;
    }
    internal static string Texto(IReadOnlyList<ItemDeContextoDto> itens, PlanoDoFormato plano) =>
        Cabecalho(plano) + string.Concat(itens.Select(x => Formatar(x, plano)));

    // DadosEstruturados que são exclusivamente {"titulo": "<texto>"}: devolve o título para a chave própria Titulo (C1).
    // Só o JSON persistido decide; um conteúdo que imite esse sufixo continua texto citado em Conteudo.
    internal static string? TituloExclusivo(string? dados)
    {
        if (string.IsNullOrWhiteSpace(dados)) return null;
        try
        {
            using var json = JsonDocument.Parse(dados);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            var propriedades = json.RootElement.EnumerateObject().ToArray();
            return propriedades is [{ Name: "titulo", Value.ValueKind: JsonValueKind.String } p] &&
                !string.IsNullOrWhiteSpace(p.Value.GetString()) ? p.Value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // Sem escapes HTML (\u0022 para aspas, \u003C para <): o destino é um prompt, não uma página. A validade JSON e o escape de
    // aspas, barra invertida e controles permanecem.
    private static readonly JsonWriterOptions Opcoes = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static string Identidade(ItemDeContextoDto x) => $"{Prefixo(x.Origem)}:{x.Id:D}@{x.Revisao}";
    private static string Prefixo(string origem) => origem == "fonte_bruta" ? "fonte" : origem;
    // Referência que já identifica o próprio item e a revisão substitui Id/Revisao/Origem; outra referência é proveniência.
    private static bool Identifica(ItemDeContextoDto x, string referencia) => referencia == Identidade(x) ||
        x.Origem == "fonte_bruta" && referencia.StartsWith($"brain://fonte/{x.Id:D}/revisao/{x.Revisao}/", StringComparison.Ordinal);

    // Forma extensa (estimativa da seleção): rótulo e sensibilidade por item.
    internal static string Formatar(ItemDeContextoDto x, bool comSensibilidade) =>
        Formatar(x, comSensibilidade ? PlanoDoFormato.Extenso : PlanoDoFormato.Extenso with { Uniforme = x.Sensibilidade });
    internal static string Formatar(ItemDeContextoDto x, PlanoDoFormato plano)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, Opcoes))
        {
            w.WriteStartObject();
            if (x.Referencia is { } r && Identifica(x, r)) w.WriteString("Ref", r);
            else { w.WriteString("Ref", Identidade(x)); if (x.Referencia is { } fonte) w.WriteString("Fonte", fonte); }
            Opcional(w, "Tipo", x.Tipo?.ToString()); Opcional(w, "Status", x.Status?.ToString());
            Opcional(w, "NaturezaDeOrigem", x.NaturezaDeOrigem?.ToString());
            if (x.Natureza != x.NaturezaDeOrigem) Opcional(w, "Natureza", x.Natureza?.ToString());
            Opcional(w, "AutoriaDaFonte", x.AutoriaDaFonte?.ToString());
            if (!plano.Rotulos.ContainsKey(ChaveDoRotulo(x))) w.WriteString("Rotulo", x.Rotulo);
            if (plano.Uniforme is null) w.WriteString("Sensibilidade", x.Sensibilidade.ToString());
            if (x.InicioDaFonte is { } inicio) w.WriteNumber("InicioDaFonte", inicio);
            Lista(w, "Relacoes", x.Relacoes);
            if (x.CamposDoSnapshot is { Count: > 0 } campos)
                foreach (var grupo in campos.GroupBy(c => c.Campo))
                    if (CampoDoSnapshot.EhLista(grupo.Key)) Lista(w, grupo.Key, grupo.Select(c => c.Texto).ToArray());
                    else w.WriteString(grupo.Key, grupo.Last().Texto);
            else if (x.ConsolidadaEm is { Count: > 0 } consolidada)
            {
                Opcional(w, "Titulo", x.Titulo);
                if (!plano.ConsolidacaoImplicita.Contains(x.Chave)) Lista(w, "ConsolidadaEm", consolidada);
                w.WriteBoolean("Omitida", true);
            }
            else if (x.Origem == "conhecimento" && x.Titulo is { } titulo && x.CorpoSemTitulo is { } corpo)
            { w.WriteString("Titulo", titulo); w.WriteString("Conteudo", corpo); }
            else w.WriteString("Conteudo", x.Conteudo);
            w.WriteEndObject();
        }
        return SemSeparadoresDeLinha(Encoding.UTF8.GetString(buffer.ToArray())) + "\n";
    }
    private static void Opcional(Utf8JsonWriter w, string nome, string? valor) { if (valor is not null) w.WriteString(nome, valor); }
    private static void Lista(Utf8JsonWriter w, string nome, IReadOnlyList<string>? valores)
    {
        if (valores is not { Count: > 0 }) return;
        w.WriteStartArray(nome); foreach (var v in valores) w.WriteStringValue(v); w.WriteEndArray();
    }
    // Esses caracteres só podem aparecer dentro de strings (as chaves são fixas): escapá-los mantém o JSON válido
    // e impede que um leitor que os trate como quebra de linha veja uma linha forjada.
    private static string SemSeparadoresDeLinha(string json) =>
        json.Replace("\u2028", "\\u2028", StringComparison.Ordinal).Replace("\u2029", "\\u2029", StringComparison.Ordinal)
            .Replace("\u0085", "\\u0085", StringComparison.Ordinal);
}

// Decisões de forma de um pacote, todas sem perda: cada uma tem regra declarada no cabeçalho.
internal sealed record PlanoDoFormato(Sensibilidade? Uniforme, IReadOnlyDictionary<string, string> Rotulos, IReadOnlySet<string> ConsolidacaoImplicita, bool ComFonteOmitida)
{
    internal static readonly PlanoDoFormato Extenso = new(null, new Dictionary<string, string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), false);
}

// Campo nomeado do snapshot operacional. Listas (referências, pendências, próximos passos) mantêm cada entrada.
public sealed record CampoDoSnapshot(string Campo, string Texto)
{
    internal static bool EhLista(string campo) => campo is "Referencias" or "Pendencias" or "ProximosPassos";
}
