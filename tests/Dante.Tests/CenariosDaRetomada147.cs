using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dante.Domain.Conhecimentos;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.RelacoesDeConhecimento;

namespace Dante.Tests;

// Roteiros da retomada da #147 em três durações (curto 2, médio 6, longo 12 turnos de A), lidos só da fixture
// pré-registrada CenariosDaRetomada147/cenarios.json. O código não acrescenta texto aos pedidos: tudo o que A recebe
// está na fixture, cujo SHA-256 é publicado na #147 antes de qualquer execução.
internal sealed record CasoDaRetomada147(int Ordem, int Pr, string Commit, string Titulo, string OrigemDoTexto, string? Markdown, string Incidente, string Solucao);
internal sealed record UnidadeEssencial147(string Id, string[] QualquerDe);

internal sealed partial class RoteiroDaRetomada147
{
    internal const string NomeDoArquivo = "cenarios.json";
    internal static readonly string[] Duracoes = ["curto", "medio", "longo"];
    private readonly JsonElement raiz;
    // Bytes exatos da fixture: é o que tem hash pré-registrado e o que cada execução grava como roteiro.json.
    internal byte[] Bytes { get; }
    private RoteiroDaRetomada147(JsonElement raiz, byte[] bytes) => (this.raiz, Bytes) = (raiz, bytes);

    internal static string Caminho => Path.Combine(AppContext.BaseDirectory, "CenariosDaRetomada147", NomeDoArquivo);
    internal static RoteiroDaRetomada147 Carregar(string? caminho = null)
    {
        var bytes = File.ReadAllBytes(caminho ?? Caminho);
        using var documento = JsonDocument.Parse(bytes);
        return new(documento.RootElement.Clone(), bytes);
    }

    private string Texto(string nome) => raiz.GetProperty(nome).GetString()!;
    internal string Versao => Texto("versao");
    internal string Separador => Texto("separadorDoHistorico");
    internal string DecisaoLiteral => Texto("decisaoLiteral");
    internal string ConsultaB(string duracao) => raiz.GetProperty("consultas").GetProperty(duracao).GetString()!;
    internal int QuantidadeDeCasos(string duracao) => raiz.GetProperty("duracoes").GetProperty(duracao).GetInt32();
    internal int Turnos(string duracao) => 2 * QuantidadeDeCasos(duracao);
    // O curto mantém o nome de cenário das retomadas de 15/11 com sufixo da duração; manifestos distinguem as três.
    internal static string Cenario(string duracao) => "retomada-" + duracao;

    internal IReadOnlyList<CasoDaRetomada147> TodosOsCasos => raiz.GetProperty("casos").EnumerateArray().Select(c => new CasoDaRetomada147(
        c.GetProperty("ordem").GetInt32(), c.GetProperty("pr").GetInt32(), c.GetProperty("commit").GetString()!, c.GetProperty("titulo").GetString()!,
        c.GetProperty("origemDoTexto").GetString()!, c.TryGetProperty("markdown", out var m) ? m.GetString() : null,
        c.GetProperty("incidente").GetString()!, c.GetProperty("solucao").GetString()!)).ToArray();
    internal IReadOnlyList<CasoDaRetomada147> Casos(string duracao) => TodosOsCasos.Take(QuantidadeDeCasos(duracao)).ToArray();

    // Identificador por caso: o primeiro usa o marcador da execução, como nas retomadas medidas; os demais, um sufixo.
    internal static string Identificador(int ordem, string marcador) => ordem == 1 ? marcador : $"{marcador}-c{ordem}";

    internal string Markdown(CasoDaRetomada147 caso, string marcador) =>
        Preencher(caso.Markdown ?? Texto("markdownCasoSeguinte"), Valores(caso, marcador));
    internal string PedidoDoCaso(CasoDaRetomada147 caso, string marcador) =>
        Preencher(Texto(caso.Ordem == 1 ? "pedidoPrimeiroCaso" : "pedidoCasoSeguinte"), new() { ["markdown"] = Markdown(caso, marcador) });
    internal string PedidoDoSnapshot(string refs, Guid decisao) =>
        Preencher(Texto("pedidoSnapshot"), new() { ["refs"] = refs, ["decisao"] = decisao.ToString() });

    // Inverso de PedidoDoSnapshot: extrai refs e decisão de uma entrada gravada; null se o texto não segue o roteiro.
    internal (string Refs, string Decisao)? LerPedidoDoSnapshot(string entrada)
    {
        var padrao = "^" + Regex.Escape(Texto("pedidoSnapshot")).Replace(@"\{refs}", "(?<refs>.+?)").Replace(@"\{decisao}", "(?<decisao>[0-9a-f-]{36})") + "$";
        var m = Regex.Match(entrada, padrao, RegexOptions.Singleline);
        return m.Success ? (m.Groups["refs"].Value, m.Groups["decisao"].Value) : null;
    }

    internal IReadOnlyList<UnidadeEssencial147> Essenciais(int ordem) => raiz.GetProperty("essenciais").GetProperty(ordem == 1 ? "primeiroCaso" : "casoSeguinte")
        .EnumerateArray().Select(u => new UnidadeEssencial147(u.GetProperty("id").GetString()!, u.GetProperty("qualquerDe").EnumerateArray().Select(x => x.GetString()!).ToArray())).ToArray();

    internal static Dictionary<string, string> Valores(CasoDaRetomada147 caso, string marcador) => new()
    {
        ["marcador"] = marcador, ["pr"] = caso.Pr.ToString(), ["commit"] = caso.Commit, ["titulo"] = caso.Titulo,
        ["incidente"] = caso.Incidente, ["solucao"] = caso.Solucao, ["ordem"] = caso.Ordem.ToString()
    };

    // Substituição em uma passada: texto inserido nunca é reinterpretado; placeholder desconhecido é erro.
    internal static string Preencher(string modelo, Dictionary<string, string> valores) =>
        Placeholder().Replace(modelo, m => valores.TryGetValue(m.Groups[1].Value, out var v) ? v : throw new KeyNotFoundException("Placeholder sem valor: " + m.Value));

    [GeneratedRegex(@"\{([a-z]+)\}")]
    private static partial Regex Placeholder();
}

// Estado exportado (estadoAntesDeB) visto pelos campos que os critérios usam. Enums chegam como inteiros.
internal sealed class EstadoExportado147
{
    internal JsonElement Raiz { get; }
    internal EstadoExportado147(string json) { using var d = JsonDocument.Parse(json); Raiz = d.RootElement.Clone(); }
    internal IEnumerable<JsonElement> Colecao(string nome) => Raiz.GetProperty(nome).EnumerateArray();
    internal static int Int(JsonElement e, string n) => e.GetProperty(n).GetInt32();
    internal static string Str(JsonElement e, string n) => e.GetProperty(n).GetString()!;
    internal static string Ref(string origem, JsonElement e) => $"{origem}:{Str(e, "Id")}@{Int(e, "Revisao")}";

    // Correspondência exata do identificador: o do caso 1 é prefixo do dos demais (marcador-cN) e não pode casar com eles.
    internal static bool ContemIdentificador(string texto, string identificador) =>
        Regex.IsMatch(texto, Regex.Escape(identificador) + @"(?!-c\d)");

    internal JsonElement[] FontesDoCaso(RoteiroDaRetomada147 roteiro, CasoDaRetomada147 caso, string marcador)
    {
        var md = roteiro.Markdown(caso, marcador);
        return Colecao("fontes").Where(f => Str(f, "Conteudo") == md).ToArray();
    }
    internal JsonElement[] ConhecimentosDoCaso(TipoDeConhecimento tipo, string identificador) =>
        Colecao("conhecimentos").Where(k => Int(k, "Tipo") == (int)tipo && ContemIdentificador(Str(k, "Conteudo"), identificador)).ToArray();
    internal JsonElement[] Decisoes => Colecao("conhecimentos").Where(k => Int(k, "Tipo") == (int)TipoDeConhecimento.Decisao).ToArray();
}

internal static class EssenciaisDaRetomada147
{
    // Unidade → presente. Alternativas com referência que não se resolve no estado contam como ausentes.
    internal static Dictionary<string, bool> AvaliarPacote(string textoDoPacote, EstadoExportado147 estado, RoteiroDaRetomada147 roteiro, string duracao, string marcador)
    {
        var textos = new List<string> { textoDoPacote };
        foreach (var linha in textoDoPacote.Split('\n'))
        {
            if (!linha.StartsWith('{')) continue;
            try { using var d = JsonDocument.Parse(linha); Coletar(d.RootElement, textos); } catch (JsonException) { }
        }
        var resultado = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var caso in roteiro.Casos(duracao))
        {
            var identificador = RoteiroDaRetomada147.Identificador(caso.Ordem, marcador);
            var valores = RoteiroDaRetomada147.Valores(caso, marcador);
            var fontes = estado.FontesDoCaso(roteiro, caso, marcador);
            string? Resolver(string chave) => chave switch
            {
                "fonte" => fontes.Length == 1 ? EstadoExportado147.Ref("fonte", fontes[0]) : null,
                "Decisao" => estado.Decisoes.Length == 1 ? EstadoExportado147.Ref("conhecimento", estado.Decisoes[0]) : null,
                _ when Enum.TryParse<TipoDeConhecimento>(chave, out var tipo) && estado.ConhecimentosDoCaso(tipo, identificador) is { Length: 1 } k
                    => EstadoExportado147.Ref("conhecimento", k[0]),
                _ => null
            };
            foreach (var unidade in roteiro.Essenciais(caso.Ordem))
                resultado[$"c{caso.Ordem}.{unidade.Id}"] = unidade.QualquerDe.Any(alternativa =>
                {
                    var m = Regex.Match(alternativa, @"^\{ref:(\w+)\}$");
                    var alvo = m.Success ? Resolver(m.Groups[1].Value) : RoteiroDaRetomada147.Preencher(alternativa, valores);
                    return alvo is not null && textos.Any(t => t.Contains(alvo, StringComparison.Ordinal));
                });
        }
        return resultado;
    }

    private static void Coletar(JsonElement e, List<string> textos)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: textos.Add(e.GetString()!); break;
            case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Coletar(p.Value, textos); break;
            case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) Coletar(x, textos); break;
        }
    }
}

// Integridade de uma execução de retomada antes de qualquer uso: evidência íntegra, histórico e estado da mesma execução,
// pedidos iguais ao roteiro pré-registrado, nada confirmado indevidamente, relações preservadas e pacote coerente.
internal static class VerificacaoDaRetomada147
{
    internal sealed record Resultado(string Id, string Duracao, string Marcador, IReadOnlyList<string> Divergencias, IReadOnlyDictionary<string, bool> Essenciais);

    internal static Resultado Verificar(string diretorio, RoteiroDaRetomada147 roteiro)
    {
        var divergencias = new List<string>();
        var integridade = ExecucaoDeMedicao147.Verificar(diretorio);
        if (integridade.Divergencias.Count > 0) return new(integridade.Id, "?", "?", integridade.Divergencias.Select(x => "evidencia: " + x).ToArray(), new Dictionary<string, bool>());
        var manifesto = ExecucaoDeMedicao147.LerManifesto(diretorio);
        // Execuções de 15/11 usam o cenário "retomada", com o roteiro curto.
        var duracao = manifesto.Cenario == "retomada" ? "curto" : manifesto.Cenario.StartsWith("retomada-", StringComparison.Ordinal) ? manifesto.Cenario["retomada-".Length..] : "";
        if (!RoteiroDaRetomada147.Duracoes.Contains(duracao)) return new(manifesto.Id, duracao, "?", ["cenario desconhecido: " + manifesto.Cenario], new Dictionary<string, bool>());
        if (manifesto.Estado != "completo") divergencias.Add("execucao incompleta");
        // Execuções novas gravam o roteiro usado; ele precisa ser byte a byte o pré-registrado (as de 15/11 são anteriores).
        if (manifesto.Arquivos.Any(a => a.Nome == "roteiro.json") && !File.ReadAllBytes(Path.Combine(diretorio, "roteiro.json")).AsSpan().SequenceEqual(roteiro.Bytes))
            divergencias.Add("roteiro da execução difere do pré-registrado");
        string Ler(string nome) => File.ReadAllText(Path.Combine(diretorio, nome), new UTF8Encoding(false, true));
        bool Existe(string nome) => manifesto.Arquivos.Any(a => a.Nome == nome);
        using var resultadoJson = JsonDocument.Parse(Ler("resultado.json"));
        var resultado = resultadoJson.RootElement;
        var markdownDoCaso1 = resultado.GetProperty("markdown").GetString()!;
        var marcador = resultado.TryGetProperty("marcador", out var mc) ? mc.GetString()! :
            Regex.Match(markdownDoCaso1, @"Identificador exclusivo do ensaio: (verificacao147[0-9a-f]{32})$").Groups[1].Value;
        if (marcador.Length == 0) divergencias.Add("marcador ausente");
        var casos = roteiro.Casos(duracao);
        if (markdownDoCaso1 != roteiro.Markdown(casos[0], marcador)) divergencias.Add("markdown do caso 1 diverge do roteiro");

        // Pedidos de A e histórico visível: a mesma sequência, com as respostas da própria execução.
        var estado = new EstadoExportado147(resultado.GetProperty("estadoAntesDeB").GetString()!);
        var historico = new List<string>();
        var decisao = estado.Decisoes.Length == 1 ? EstadoExportado147.Str(estado.Decisoes[0], "Id") : null;
        for (var i = 0; i < casos.Count; i++)
        {
            var caso = casos[i];
            var (nPedido, nSnapshot) = (2 * i + 1, 2 * i + 2);
            foreach (var n in new[] { nPedido, nSnapshot })
                if (!Existe($"entrada-a{n}.txt") || !Existe($"resposta-a{n}.txt")) divergencias.Add($"turno A{n} ausente");
            if (divergencias.Any(d => d.StartsWith("turno A", StringComparison.Ordinal))) break;
            var pedido = Ler($"entrada-a{nPedido}.txt");
            if (pedido != roteiro.PedidoDoCaso(caso, marcador)) divergencias.Add($"entrada-a{nPedido} diverge do roteiro do caso {caso.Ordem}");
            var snapshot = Ler($"entrada-a{nSnapshot}.txt");
            if (roteiro.LerPedidoDoSnapshot(snapshot) is not { } lido) divergencias.Add($"entrada-a{nSnapshot} não segue o roteiro do snapshot");
            else
            {
                if (lido.Decisao != decisao) divergencias.Add($"entrada-a{nSnapshot}: decisão diverge do estado");
                var refs = lido.Refs.Split(", ");
                foreach (var r in refs)
                    if (!RefExiste(estado, r)) divergencias.Add($"entrada-a{nSnapshot}: referência sem registro no estado: {r}");
                // O caso recém-registrado precisa estar entre as referências do snapshot daquele turno.
                var fontes = estado.FontesDoCaso(roteiro, caso, marcador);
                if (fontes.Length == 1 && !refs.Contains(EstadoExportado147.Ref("fonte", fontes[0]))) divergencias.Add($"entrada-a{nSnapshot}: fonte do caso {caso.Ordem} não referenciada");
            }
            historico.AddRange([pedido, Ler($"resposta-a{nPedido}.txt"), snapshot, Ler($"resposta-a{nSnapshot}.txt")]);
        }
        if (Existe($"entrada-a{2 * casos.Count + 1}.txt")) divergencias.Add("turnos de A além do roteiro");
        if (Ler("historico-visivel.txt") != string.Join(roteiro.Separador, historico)) divergencias.Add("historico-visivel diverge das entradas e respostas de A");

        VerificarEstado(estado, roteiro, casos, marcador, divergencias);
        if (resultado.GetProperty("estadoDepoisDeB").GetString() != resultado.GetProperty("estadoAntesDeB").GetString()) divergencias.Add("estado alterado pela sessão B");

        // Pacote: texto exportado, estrutura e entrada B são do mesmo pacote e da consulta pré-registrada.
        var consulta = roteiro.ConsultaB(duracao);
        if (resultado.GetProperty("consultaB").GetString() != consulta) divergencias.Add("consulta B diverge do roteiro");
        var texto = Ler("pacote-texto-para-injecao.txt");
        using (var pacote = JsonDocument.Parse(Ler("pacote.json")))
            if (pacote.RootElement.GetProperty("textoParaInjecao").GetString() != texto) divergencias.Add("pacote.json e pacote-texto divergem");
        var entradaB = Ler("entrada-b.txt");
        if (!PacoteInjetado147.Confere(entradaB, texto) || !entradaB.EndsWith("Mensagem do usuário:\n" + consulta, StringComparison.Ordinal))
            divergencias.Add("entrada B não é pacote + consulta pré-registrada");
        return new(manifesto.Id, duracao, marcador, divergencias, EssenciaisDaRetomada147.AvaliarPacote(texto, estado, roteiro, duracao, marcador));
    }

    // Um caso = uma fonte literal, um Incidente e uma Solução inferidos (nunca confirmados) com o identificador do caso
    // e uma relação ResolvidoPor entre eles; uma única Decisão confirmada, a do usuário. Nada além disso.
    internal static void VerificarEstado(EstadoExportado147 estado, RoteiroDaRetomada147 roteiro, IReadOnlyList<CasoDaRetomada147> casos, string marcador, List<string> divergencias)
    {
        int I(JsonElement e, string n) => EstadoExportado147.Int(e, n);
        var conhecimentos = estado.Colecao("conhecimentos").ToArray();
        var relacoes = estado.Colecao("relacoes").ToArray();
        if (estado.Colecao("fontes").Count() != casos.Count) divergencias.Add("quantidade de fontes difere da de casos");
        if (conhecimentos.Length != 2 * casos.Count + 1) divergencias.Add("quantidade de conhecimentos difere de 2 por caso + 1 decisão");
        if (relacoes.Length != casos.Count) divergencias.Add("quantidade de relações difere da de casos");
        var decisoes = estado.Decisoes;
        if (decisoes.Length != 1 || I(decisoes[0], "Status") != (int)StatusDoConhecimento.Confirmado || I(decisoes[0], "NaturezaDeOrigem") != (int)NaturezaDoConteudo.DitoPeloUsuario
            || EstadoExportado147.Str(decisoes[0], "Conteudo") != roteiro.DecisaoLiteral) divergencias.Add("decisão do usuário ausente, duplicada ou fora do literal");
        foreach (var k in conhecimentos.Where(k => I(k, "Status") == (int)StatusDoConhecimento.Confirmado && I(k, "Tipo") != (int)TipoDeConhecimento.Decisao))
            divergencias.Add("conhecimento confirmado indevidamente: " + EstadoExportado147.Ref("conhecimento", k));
        foreach (var k in conhecimentos.Where(k => I(k, "NaturezaDeOrigem") == (int)NaturezaDoConteudo.ConclusaoDoAgente))
            if (I(k, "Status") != (int)StatusDoConhecimento.Inferido || k.GetProperty("PodeSerConfirmado").GetBoolean())
                divergencias.Add("conclusão do agente fora de Inferido: " + EstadoExportado147.Ref("conhecimento", k));
        foreach (var caso in casos)
        {
            var identificador = RoteiroDaRetomada147.Identificador(caso.Ordem, marcador);
            var fontes = estado.FontesDoCaso(roteiro, caso, marcador);
            if (fontes.Length != 1) { divergencias.Add($"caso {caso.Ordem}: fonte literal ausente ou duplicada"); continue; }
            var inc = estado.ConhecimentosDoCaso(TipoDeConhecimento.Incidente, identificador);
            var sol = estado.ConhecimentosDoCaso(TipoDeConhecimento.Solucao, identificador);
            if (inc.Length != 1 || sol.Length != 1) { divergencias.Add($"caso {caso.Ordem}: incidente/solução ausente ou duplicado"); continue; }
            foreach (var k in inc.Concat(sol))
                if (I(k, "NaturezaDeOrigem") != (int)NaturezaDoConteudo.ConclusaoDoAgente) divergencias.Add($"caso {caso.Ordem}: incidente/solução sem natureza ConclusaoDoAgente");
            var (idInc, idSol) = (EstadoExportado147.Str(inc[0], "Id"), EstadoExportado147.Str(sol[0], "Id"));
            if (relacoes.Count(r => I(r, "Tipo") == (int)TipoDeRelacao.ResolvidoPor && EstadoExportado147.Str(r, "IdOrigem") == idInc && EstadoExportado147.Str(r, "IdDestino") == idSol) != 1)
                divergencias.Add($"caso {caso.Ordem}: relação ResolvidoPor incidente→solução ausente");
        }
        var snapshots = estado.Colecao("snapshots").ToArray();
        if (snapshots.Length != 1) { divergencias.Add("snapshot ausente ou duplicado"); return; }
        var dados = snapshots[0].GetProperty("Dados");
        if (decisoes.Length == 1 && !dados.GetProperty("IdsDecisoesConfirmadas").EnumerateArray().Any(x => x.GetString() == EstadoExportado147.Str(decisoes[0], "Id")))
            divergencias.Add("snapshot não referencia a decisão confirmada");
        var referencias = dados.GetProperty("Referencias").EnumerateArray().Select(x => x.GetString()).ToHashSet();
        foreach (var caso in casos)
            if (estado.FontesDoCaso(roteiro, caso, marcador) is { Length: 1 } f && !referencias.Contains(EstadoExportado147.Ref("fonte", f[0])))
                divergencias.Add($"snapshot não referencia a fonte do caso {caso.Ordem}");
    }

    private static bool RefExiste(EstadoExportado147 estado, string referencia)
    {
        var m = Regex.Match(referencia, @"^(conhecimento|fonte):([0-9a-f-]{36})@([1-9][0-9]*)$");
        if (!m.Success) return false;
        var colecao = m.Groups[1].Value == "fonte" ? "fontes" : "conhecimentos";
        return estado.Colecao(colecao).Any(x => EstadoExportado147.Str(x, "Id") == m.Groups[2].Value && EstadoExportado147.Int(x, "Revisao") >= int.Parse(m.Groups[3].Value));
    }
}
