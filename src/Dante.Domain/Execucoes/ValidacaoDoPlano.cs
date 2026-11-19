using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dante.Domain.Agentes;

namespace Dante.Domain.Execucoes;

// Unidade de um plano já validado e normalizado (AD-75, #310): o papel do participante que a executa, o contrato do
// catálogo, o objetivo, a entrada canônica do contrato, os critérios e as unidades de que depende.
public sealed record UnidadeDoPlano(string Id, string Papel, string Contrato, string Objetivo, string Entrada,
    string? Criterios, IReadOnlyList<string> DependeDe);

// Plano validado: a forma canônica (unidades em ordem topológica), o SHA-256 dela calculado aqui e as unidades.
public sealed record PlanoValidado(string Conteudo, string Hash, IReadOnlyList<UnidadeDoPlano> Unidades);

public enum MotivoDaRecusaDoPlano
{
    FormaInvalida,
    PlanoVazio,
    OrcamentoExcedido,
    IdentificadorInvalido,
    IdentificadorRepetido,
    PapelInexistente,
    UnidadeDoCoordenador,
    ContratoInvalido,
    ContratoNaoPermitido,
    CapacidadeInsuficiente,
    EntradaInvalida,
    ObjetivoInvalido,
    CriteriosInvalidos,
    DependenciaInexistente,
    Ciclo,
    // Escrita ou outra ação protegida: depende do gateway de aprovação (#311), que não existe nesta entrega. Recusada
    // sempre, sem alternativa: nenhuma porta do gateway é simulada aqui.
    RequerGateway
}

// Unidade nula: o problema é do plano inteiro.
public sealed record RecusaDoPlano(MotivoDaRecusaDoPlano Motivo, string? Unidade, string Mensagem);

public sealed record ResultadoDaValidacaoDoPlano(PlanoValidado? Plano, IReadOnlyList<RecusaDoPlano> Recusas)
{
    public bool Valido => Plano is not null;
}

// Um papel do snapshot a que uma unidade pode ser atribuída, com as capacidades da versão congelada do agente.
public sealed record PapelDoPlano(string Papel, IReadOnlyList<CapacidadeDoAgente> Capacidades);

// Contra o que um plano é validado: o snapshot congelado da execução (papéis e capacidades das versões gravadas), o
// coordenador (que nunca recebe unidade) e quantas unidades ainda cabem no orçamento da raiz.
public sealed class ContextoDoPlano
{
    public ContextoDoPlano(string papelDoCoordenador, IEnumerable<PapelDoPlano> papeis, int unidadesDisponiveis)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(papelDoCoordenador);
        ArgumentNullException.ThrowIfNull(papeis);
        if (unidadesDisponiveis < 0) throw new ArgumentOutOfRangeException(nameof(unidadesDisponiveis));
        PapelDoCoordenador = papelDoCoordenador;
        var lista = new List<PapelDoPlano>();
        foreach (var papel in papeis)
        {
            ArgumentNullException.ThrowIfNull(papel, nameof(papeis));
            if (Papel(lista, papel.Papel) is not null) throw new ArgumentException("Papel repetido.", nameof(papeis));
            lista.Add(papel);
        }
        Papeis = lista.AsReadOnly();
        UnidadesDisponiveis = Math.Min(unidadesDisponiveis, ValidacaoDoPlano.MaximoDeUnidades);
    }

    public string PapelDoCoordenador { get; }
    public IReadOnlyList<PapelDoPlano> Papeis { get; }
    public int UnidadesDisponiveis { get; }

    // O contexto a partir das linhas gravadas: o snapshot precisa reproduzir o hash, e cada participante precisa da
    // própria versão congelada, íntegra e com o hash gravado. Qualquer divergência é falha de integridade (exceção),
    // nunca uma recusa do plano: o plano não é validado contra um snapshot adulterado. delegacoesDaRaiz: delegações já
    // criadas na árvore (o teto global vale para a execução inteira, também entre revisões do plano).
    public static ContextoDoPlano DoSnapshot(CoordenacaoDaExecucao coordenacao,
        IReadOnlyList<ParticipanteDaExecucao> participantes, IReadOnlyList<VersaoDoAgente> versoes, int delegacoesDaRaiz)
    {
        ArgumentNullException.ThrowIfNull(coordenacao);
        ArgumentNullException.ThrowIfNull(participantes);
        ArgumentNullException.ThrowIfNull(versoes);
        if (delegacoesDaRaiz < 0) throw new ArgumentOutOfRangeException(nameof(delegacoesDaRaiz));
        if (!coordenacao.SnapshotConfere(participantes))
            throw new InvalidOperationException("O snapshot da coordenação não confere com os participantes gravados.");
        var papeis = new List<PapelDoPlano>();
        foreach (var participante in participantes)
        {
            if (participante.IdExecucao != coordenacao.IdExecucao)
                throw new InvalidOperationException("Participante de outra execução.");
            VersaoDoAgente? versao = null;
            foreach (var candidata in versoes)
                if (candidata.Id == participante.IdVersaoDoAgente) versao = candidata;
            if (versao is null || versao.HashDaConfiguracao != participante.HashDaConfiguracaoDoAgente || !versao.Integra)
                throw new InvalidOperationException(
                    $"A versão congelada do papel {participante.Papel} não confere com o snapshot.");
            papeis.Add(new PapelDoPlano(participante.Papel, versao.Capacidades));
        }
        return new ContextoDoPlano(coordenacao.PapelDoCoordenador, papeis,
            Math.Max(0, LimitesDeDelegacao.Teto.DelegacoesPorExecucao - delegacoesDaRaiz));
    }

    internal static PapelDoPlano? Papel(IReadOnlyList<PapelDoPlano> papeis, string? nome)
    {
        foreach (var papel in papeis)
            if (papel.Papel == nome) return papel;
        return null;
    }
}

// Validação do plano proposto pelo coordenador (AD-75, #310): pura e determinística, sem banco, relógio nem
// cadastro; roda inteira antes de qualquer delegação. Recebe o texto da proposta (nunca um hash: o hash é calculado
// aqui, sobre a forma canônica) e devolve o plano canônico ou todas as recusas encontradas, com motivo e unidade.
//
// Proposta: {"unidades":[{"id","papel","contrato","objetivo","entrada":{…},"criterios"?,"dependeDe"?:[…],"escrita"?}]}.
// Campos desconhecidos ou repetidos são recusados. Normalização explícita: textos em Unicode NFC, quebras de linha
// "\n", sem espaços nas pontas; papel em minúsculas; dependências sem repetição, em ordem ordinal; entrada na forma
// canônica do contrato. Forma canônica: {"formato":"1","unidades":[…]} com as unidades em ordem topológica (empate
// pela ordem da proposta) e as chaves de cada unidade em ordem alfabética; hash = SHA-256 dos bytes UTF-8 dela.
public static class ValidacaoDoPlano
{
    public const int VersaoDoFormato = 1;
    public const int TamanhoMaximoDaProposta = 64_000;
    public const int TamanhoMaximoDoObjetivo = Execucao.TamanhoMaximoDoObjetivo;
    public const int TamanhoMaximoDosCriterios = Delegacao.TamanhoMaximoDosCriterios;
    // D3: no máximo 4 unidades por plano, o teto global de delegações por execução, que esta entrega não eleva.
    public static readonly int MaximoDeUnidades = LimitesDeDelegacao.Teto.DelegacoesPorExecucao;
    // Um plano inválido volta ao coordenador uma vez; inválido de novo, a execução termina sem delegar nada.
    public const int CorrecoesPermitidas = 1;

    private static readonly Regex FormatoDoId = new("^[a-z][a-z0-9-]{0,15}$", RegexOptions.CultureInvariant);
    private static readonly string[] CamposDaUnidade =
        ["contrato", "criterios", "dependeDe", "entrada", "escrita", "id", "objetivo", "papel"];

    public static ResultadoDaValidacaoDoPlano Validar(string? proposta, ContextoDoPlano contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        var recusas = new List<RecusaDoPlano>();
        if (string.IsNullOrWhiteSpace(proposta) || proposta.Length > TamanhoMaximoDaProposta)
            return Recusado(recusas, MotivoDaRecusaDoPlano.FormaInvalida, null,
                $"Plano ausente ou acima de {TamanhoMaximoDaProposta} caracteres.");
        JsonDocument documento;
        try
        {
            documento = JsonDocument.Parse(proposta);
        }
        catch (JsonException)
        {
            return Recusado(recusas, MotivoDaRecusaDoPlano.FormaInvalida, null, "O plano não é um JSON válido.");
        }
        using (documento)
        {
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object || !SoCampos(raiz, ["unidades"]) ||
                !raiz.TryGetProperty("unidades", out var lista) || lista.ValueKind != JsonValueKind.Array)
                return Recusado(recusas, MotivoDaRecusaDoPlano.FormaInvalida, null,
                    "O plano precisa ser um objeto com a lista \"unidades\" e nenhum outro campo.");
            var quantidade = lista.GetArrayLength();
            if (quantidade == 0)
                return Recusado(recusas, MotivoDaRecusaDoPlano.PlanoVazio, null, "O plano não tem unidades.");
            if (quantidade > contexto.UnidadesDisponiveis)
                recusas.Add(new(MotivoDaRecusaDoPlano.OrcamentoExcedido, null,
                    $"O plano tem {quantidade} unidades; cabem {contexto.UnidadesDisponiveis} " +
                    $"(máximo {MaximoDeUnidades} delegações por execução)."));

            var unidades = new List<UnidadeDoPlano?>();
            var posicao = 0;
            foreach (var elemento in lista.EnumerateArray())
                unidades.Add(LerUnidade(elemento, ++posicao, contexto, unidades, recusas));
            if (recusas.Count > 0) return new(null, recusas.AsReadOnly());

            var validas = new List<UnidadeDoPlano>();
            foreach (var unidade in unidades) validas.Add(unidade!);
            var ordenadas = OrdenarTopologicamente(validas, recusas);
            if (ordenadas is null) return new(null, recusas.AsReadOnly());
            var conteudo = Escrever(ordenadas);
            return new(new PlanoValidado(conteudo, Hash(conteudo), ordenadas.AsReadOnly()), []);
        }
    }

    // Relê uma forma canônica gravada. Só aceita o que esta classe escreveria a partir das próprias unidades (o texto
    // reescrito precisa ser idêntico), então um conteúdo adulterado ou de outra versão do formato é recusado.
    public static PlanoValidado LerCanonico(string conteudo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conteudo);
        var unidades = new List<UnidadeDoPlano>();
        try
        {
            using var documento = JsonDocument.Parse(conteudo);
            var raiz = documento.RootElement;
            if (raiz.GetProperty("formato").GetString() != VersaoDoFormato.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Formato de plano desconhecido.");
            foreach (var item in raiz.GetProperty("unidades").EnumerateArray())
            {
                var dependencias = new List<string>();
                foreach (var dependencia in item.GetProperty("dependeDe").EnumerateArray())
                    dependencias.Add(dependencia.GetString()!);
                var criterios = item.GetProperty("criterios");
                unidades.Add(new UnidadeDoPlano(item.GetProperty("id").GetString()!, item.GetProperty("papel").GetString()!,
                    item.GetProperty("contrato").GetString()!, item.GetProperty("objetivo").GetString()!,
                    item.GetProperty("entrada").GetRawText(),
                    criterios.ValueKind == JsonValueKind.Null ? null : criterios.GetString(), dependencias.AsReadOnly()));
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException
                                              or ArgumentNullException)
        {
            throw new InvalidOperationException("O plano gravado não está na forma canônica.", exception);
        }
        var reescrito = Escrever(unidades);
        if (!string.Equals(reescrito, conteudo, StringComparison.Ordinal))
            throw new InvalidOperationException("O plano gravado não está na forma canônica.");
        return new PlanoValidado(conteudo, Hash(conteudo), unidades.AsReadOnly());
    }

    public static string Hash(string conteudo) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(conteudo)));

    public static string NormalizarTexto(string texto) =>
        texto.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static UnidadeDoPlano? LerUnidade(JsonElement elemento, int posicao, ContextoDoPlano contexto,
        List<UnidadeDoPlano?> anteriores, List<RecusaDoPlano> recusas)
    {
        var rotulo = "#" + posicao.ToString(CultureInfo.InvariantCulture);
        if (elemento.ValueKind != JsonValueKind.Object || !SoCampos(elemento, CamposDaUnidade))
        {
            recusas.Add(new(MotivoDaRecusaDoPlano.FormaInvalida, rotulo,
                "Cada unidade é um objeto com id, papel, contrato, objetivo, entrada e, opcionais, criterios, dependeDe " +
                "e escrita; nenhum outro campo."));
            return null;
        }
        var antes = recusas.Count;
        var id = Texto(elemento, "id");
        if (id is null || !FormatoDoId.IsMatch(id))
        {
            recusas.Add(new(MotivoDaRecusaDoPlano.IdentificadorInvalido, rotulo,
                "Identificador inválido: de 1 a 16 caracteres minúsculos (letras, números e -), começando por letra."));
            id = null;
        }
        else
        {
            foreach (var anterior in anteriores)
                if (anterior?.Id == id)
                    recusas.Add(new(MotivoDaRecusaDoPlano.IdentificadorRepetido, id, $"O identificador {id} se repete."));
            rotulo = id;
        }

        // Ação protegida antes de tudo: escrita nunca é aceita sem o gateway (#311).
        if (elemento.TryGetProperty("escrita", out var escrita))
        {
            if (escrita.ValueKind == JsonValueKind.True)
                recusas.Add(new(MotivoDaRecusaDoPlano.RequerGateway, rotulo,
                    "Escrita é ação protegida e depende do gateway de aprovação (#311); este plano é somente leitura."));
            else if (escrita.ValueKind != JsonValueKind.False)
                recusas.Add(new(MotivoDaRecusaDoPlano.FormaInvalida, rotulo, "escrita é verdadeiro ou falso."));
        }

        var papel = Texto(elemento, "papel")?.Trim().ToLowerInvariant();
        var doPapel = ContextoDoPlano.Papel(contexto.Papeis, papel);
        if (papel is not null && papel == contexto.PapelDoCoordenador)
            recusas.Add(new(MotivoDaRecusaDoPlano.UnidadeDoCoordenador, rotulo,
                "O coordenador não executa unidades do próprio plano."));
        else if (doPapel is null)
            recusas.Add(new(MotivoDaRecusaDoPlano.PapelInexistente, rotulo,
                $"O papel {papel ?? "(ausente)"} não está no snapshot desta execução."));

        var identificador = Texto(elemento, "contrato");
        ContratoDeDelegacao? contrato = null;
        if (!ContratosDeDelegacao.TentarObter(identificador, out var conhecido))
            recusas.Add(new(MotivoDaRecusaDoPlano.ContratoInvalido, rotulo,
                $"Contrato desconhecido: {identificador ?? "(ausente)"}."));
        else if (conhecido.Identificador != ContratosDeDelegacao.Consulta.Identificador)
            recusas.Add(new(MotivoDaRecusaDoPlano.ContratoNaoPermitido, rotulo,
                $"Só {ContratosDeDelegacao.Consulta.Identificador} é permitido no plano nesta entrega."));
        else
        {
            contrato = conhecido;
            if (doPapel is not null && papel != contexto.PapelDoCoordenador && !contrato.Aceita(doPapel.Capacidades))
                recusas.Add(new(MotivoDaRecusaDoPlano.CapacidadeInsuficiente, rotulo,
                    $"O papel {papel} não tem capacidade para {contrato.Identificador}."));
        }

        var objetivo = Texto(elemento, "objetivo") is { } bruto ? NormalizarTexto(bruto) : "";
        if (objetivo.Length == 0 || objetivo.Length > TamanhoMaximoDoObjetivo)
            recusas.Add(new(MotivoDaRecusaDoPlano.ObjetivoInvalido, rotulo,
                $"Objetivo obrigatório, com até {TamanhoMaximoDoObjetivo} caracteres."));

        string? criterios = null;
        if (elemento.TryGetProperty("criterios", out var valorDosCriterios) && valorDosCriterios.ValueKind != JsonValueKind.Null)
        {
            if (valorDosCriterios.ValueKind != JsonValueKind.String)
                recusas.Add(new(MotivoDaRecusaDoPlano.FormaInvalida, rotulo, "criterios é texto."));
            else
            {
                criterios = NormalizarTexto(valorDosCriterios.GetString()!);
                if (criterios.Length == 0) criterios = null;
                else if (criterios.Length > TamanhoMaximoDosCriterios)
                    recusas.Add(new(MotivoDaRecusaDoPlano.CriteriosInvalidos, rotulo,
                        $"Critérios com até {TamanhoMaximoDosCriterios} caracteres."));
            }
        }

        string? entrada = null;
        if (!elemento.TryGetProperty("entrada", out var valorDaEntrada) || valorDaEntrada.ValueKind != JsonValueKind.Object)
            recusas.Add(new(MotivoDaRecusaDoPlano.EntradaInvalida, rotulo, "entrada é o objeto do contrato."));
        else if (contrato is not null)
        {
            try
            {
                entrada = contrato.CanonizarEntrada(valorDaEntrada.GetRawText());
            }
            catch (ArgumentException exception)
            {
                recusas.Add(new(MotivoDaRecusaDoPlano.EntradaInvalida, rotulo, exception.Message));
            }
        }

        var dependencias = new List<string>();
        if (elemento.TryGetProperty("dependeDe", out var valorDasDependencias) &&
            valorDasDependencias.ValueKind != JsonValueKind.Null)
        {
            if (valorDasDependencias.ValueKind != JsonValueKind.Array)
                recusas.Add(new(MotivoDaRecusaDoPlano.FormaInvalida, rotulo, "dependeDe é uma lista de identificadores."));
            else
                foreach (var dependencia in valorDasDependencias.EnumerateArray())
                {
                    var alvo = dependencia.ValueKind == JsonValueKind.String ? dependencia.GetString() : null;
                    if (alvo is null || !FormatoDoId.IsMatch(alvo))
                        recusas.Add(new(MotivoDaRecusaDoPlano.DependenciaInexistente, rotulo,
                            "Dependência com identificador inválido."));
                    else if (!dependencias.Contains(alvo)) dependencias.Add(alvo);
                }
        }
        dependencias.Sort(StringComparer.Ordinal);

        return recusas.Count > antes || id is null || papel is null || contrato is null || entrada is null
            ? null
            : new UnidadeDoPlano(id, papel, contrato.Identificador, objetivo, entrada, criterios, dependencias.AsReadOnly());
    }

    // Kahn com empate pela ordem da proposta: determinístico. Dependência desconhecida, autodependência ou ciclo
    // recusam o plano, citando as unidades envolvidas.
    private static List<UnidadeDoPlano>? OrdenarTopologicamente(List<UnidadeDoPlano> unidades, List<RecusaDoPlano> recusas)
    {
        var indice = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < unidades.Count; i++) indice[unidades[i].Id] = i;
        var pendentes = new int[unidades.Count];
        for (var i = 0; i < unidades.Count; i++)
            foreach (var dependencia in unidades[i].DependeDe)
            {
                if (dependencia == unidades[i].Id)
                    recusas.Add(new(MotivoDaRecusaDoPlano.Ciclo, unidades[i].Id, "Uma unidade não depende de si mesma."));
                else if (!indice.ContainsKey(dependencia))
                    recusas.Add(new(MotivoDaRecusaDoPlano.DependenciaInexistente, unidades[i].Id,
                        $"A unidade {dependencia} não existe neste plano."));
                pendentes[i]++;
            }
        if (recusas.Count > 0) return null;

        var ordem = new List<UnidadeDoPlano>();
        var feitas = new bool[unidades.Count];
        while (ordem.Count < unidades.Count)
        {
            var proxima = -1;
            for (var i = 0; i < unidades.Count && proxima < 0; i++)
                if (!feitas[i] && pendentes[i] == 0) proxima = i;
            if (proxima < 0)
            {
                var envolvidas = new List<string>();
                for (var i = 0; i < unidades.Count; i++)
                    if (!feitas[i]) envolvidas.Add(unidades[i].Id);
                recusas.Add(new(MotivoDaRecusaDoPlano.Ciclo, null,
                    $"Dependências em ciclo entre {string.Join(", ", envolvidas)}."));
                return null;
            }
            feitas[proxima] = true;
            ordem.Add(unidades[proxima]);
            for (var i = 0; i < unidades.Count; i++)
                foreach (var dependencia in unidades[i].DependeDe)
                    if (dependencia == unidades[proxima].Id) pendentes[i]--;
        }
        return ordem;
    }

    private static string Escrever(IReadOnlyList<UnidadeDoPlano> unidades)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("formato", VersaoDoFormato.ToString(CultureInfo.InvariantCulture));
            json.WriteStartArray("unidades");
            foreach (var unidade in unidades)
            {
                json.WriteStartObject();
                json.WriteString("contrato", unidade.Contrato);
                if (unidade.Criterios is null) json.WriteNull("criterios");
                else json.WriteString("criterios", unidade.Criterios);
                json.WriteStartArray("dependeDe");
                foreach (var dependencia in unidade.DependeDe) json.WriteStringValue(dependencia);
                json.WriteEndArray();
                json.WritePropertyName("entrada");
                json.WriteRawValue(unidade.Entrada);
                json.WriteString("id", unidade.Id);
                json.WriteString("objetivo", unidade.Objetivo);
                json.WriteString("papel", unidade.Papel);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool SoCampos(JsonElement objeto, string[] permitidos)
    {
        var vistos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var propriedade in objeto.EnumerateObject())
            if (!vistos.Add(propriedade.Name) || Array.IndexOf(permitidos, propriedade.Name) < 0) return false;
        return true;
    }

    private static string? Texto(JsonElement objeto, string campo) =>
        objeto.TryGetProperty(campo, out var valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString() : null;

    private static ResultadoDaValidacaoDoPlano Recusado(List<RecusaDoPlano> recusas, MotivoDaRecusaDoPlano motivo,
        string? unidade, string mensagem)
    {
        recusas.Add(new(motivo, unidade, mensagem));
        return new(null, recusas.AsReadOnly());
    }
}
