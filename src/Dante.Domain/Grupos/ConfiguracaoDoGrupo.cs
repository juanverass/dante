using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dante.Domain.Grupos;

// O que uma versão de grupo define (AD-69), já normalizado e validado no que não depende de outros agregados:
// participantes ordenados por papel, papéis únicos, roteiro na forma suportada e política dentro dos limites.
// O que depende dos agentes (existência, estado, capacidades, escrita, driver) é conferido pela Application ao salvar,
// ao ativar e ao executar. O hash é o SHA-256 do JSON canônico (chaves em ordem fixa, listas ordenadas, enums pelo
// nome, formato versionado): duas configurações iguais têm o mesmo hash em qualquer máquina e em qualquer ordem de
// entrada. Nada aqui é credencial.
public sealed class ConfiguracaoDoGrupo
{
    public const int VersaoDoFormato = 1;
    public const int MaximoDeParticipantes = 10;
    public const int TamanhoMaximoDoPapel = 31;
    public const int TamanhoMaximoDasResponsabilidades = 1000;
    public const int RodadasMaximasPermitidas = 5;
    public const int MaximoDeRepositorios = 20;
    public const int DuracaoMaximaPermitidaEmMinutos = 7 * 24 * 60;

    private static readonly Regex FormatoDoPapel = new("^[a-z][a-z0-9-]{1,30}$", RegexOptions.CultureInvariant);
    private static readonly Regex FormatoDoAlias = new("^@[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);

    public ConfiguracaoDoGrupo(ModoDeCoordenacao modo, IEnumerable<ParticipanteDoGrupo> participantes,
        IEnumerable<PassoDoRoteiro> roteiro, PoliticaDoGrupo politica)
    {
        if (!Enum.IsDefined(modo)) throw new ArgumentOutOfRangeException(nameof(modo), "Modo de coordenação inválido.");
        ArgumentNullException.ThrowIfNull(participantes);
        ArgumentNullException.ThrowIfNull(roteiro);
        ArgumentNullException.ThrowIfNull(politica);
        Modo = modo;
        Participantes = NormalizarParticipantes(participantes);
        Roteiro = NormalizarRoteiro(roteiro);
        Politica = NormalizarPolitica(politica);
        ValidarRoteiroContraParticipantes();
    }

    public ModoDeCoordenacao Modo { get; }
    public IReadOnlyList<ParticipanteDoGrupo> Participantes { get; }
    public IReadOnlyList<PassoDoRoteiro> Roteiro { get; }
    public PoliticaDoGrupo Politica { get; }

    public bool TemCorrecao => Roteiro.Any(passo => passo.Tipo == TipoDePassoDoRoteiro.Corrigir);

    // Laços explícitos em vez de lambdas com captura: o Domain não gera classes de closure (teste de arquitetura).
    public ParticipanteDoGrupo Participante(string papel) =>
        DoPapel(Participantes, papel) ?? throw new ArgumentException($"O papel {papel} não existe no grupo.", nameof(papel));

    public PassoDoRoteiro Passo(TipoDePassoDoRoteiro tipo)
    {
        foreach (var passo in Roteiro)
            if (passo.Tipo == tipo) return passo;
        throw new InvalidOperationException($"O roteiro não tem o passo {tipo}.");
    }

    private static ParticipanteDoGrupo? DoPapel(IReadOnlyList<ParticipanteDoGrupo> participantes, string? papel)
    {
        foreach (var participante in participantes)
            if (participante.Papel == papel) return participante;
        return null;
    }

    public static bool PapelValido(string? papel) => papel is not null && FormatoDoPapel.IsMatch(papel);

    public string JsonCanonico()
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("formato", VersaoDoFormato.ToString(CultureInfo.InvariantCulture));
            json.WriteString("modo", Modo.ToString());
            json.WriteStartArray("participantes");
            foreach (var participante in Participantes)
            {
                json.WriteStartObject();
                json.WriteString("idAgente", participante.IdAgente.ToString("D"));
                json.WriteString("papel", participante.Papel);
                json.WriteBoolean("podeEscrever", participante.PodeEscrever);
                json.WriteString("responsabilidades", participante.Responsabilidades);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartObject("politica");
            if (Politica.DuracaoMaximaEmMinutos is { } duracao) json.WriteNumber("duracaoMaximaEmMinutos", duracao);
            else json.WriteNull("duracaoMaximaEmMinutos");
            json.WriteNumber("paralelismoMaximo", Politica.ParalelismoMaximo);
            json.WriteStartArray("repositoriosPermitidos");
            foreach (var alias in Politica.RepositoriosPermitidos!) json.WriteStringValue(alias);
            json.WriteEndArray();
            json.WriteNumber("rodadasDeRevisaoMaximas", Politica.RodadasDeRevisaoMaximas);
            json.WriteEndObject();
            // O roteiro é ordenado por natureza: a ordem dos passos faz parte da configuração.
            json.WriteStartArray("roteiro");
            foreach (var passo in Roteiro)
            {
                json.WriteStartObject();
                json.WriteString("papel", passo.Papel);
                json.WriteString("tipo", passo.Tipo.ToString());
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonCanonico())));

    private static IReadOnlyList<ParticipanteDoGrupo> NormalizarParticipantes(IEnumerable<ParticipanteDoGrupo> valores)
    {
        var lista = new List<ParticipanteDoGrupo>();
        foreach (var participante in valores)
        {
            ArgumentNullException.ThrowIfNull(participante, nameof(valores));
            if (participante.IdAgente == Guid.Empty)
                throw new ArgumentException("Participante sem agente.", nameof(valores));
            var papel = participante.Papel?.Trim().ToLowerInvariant();
            if (!PapelValido(papel))
                throw new ArgumentException(
                    $"Papel inválido: {participante.Papel}. Use de 2 a 31 caracteres minúsculos (letras, números e -), " +
                    "começando por letra.", nameof(valores));
            lista.Add(participante with
            {
                Papel = papel!,
                Responsabilidades = NormalizarTexto(participante.Responsabilidades)
            });
        }
        if (lista.Count == 0) throw new ArgumentException("O grupo precisa de ao menos um participante.", nameof(valores));
        if (lista.Count > MaximoDeParticipantes)
            throw new ArgumentException($"No máximo {MaximoDeParticipantes} participantes.", nameof(valores));
        var repetido = lista.GroupBy(participante => participante.Papel).FirstOrDefault(grupo => grupo.Count() > 1);
        if (repetido is not null)
            throw new ArgumentException($"O papel {repetido.Key} aparece mais de uma vez.", nameof(valores));
        lista.Sort((a, b) => string.CompareOrdinal(a.Papel, b.Papel));
        return lista.AsReadOnly();
    }

    // Forma suportada nesta etapa (#308): Implementar, Revisar, Corrigir opcional e AprovacaoHumana, nesta ordem.
    // A transição é fixa: Implementar → Revisar; Revisar aprovado → AprovacaoHumana; Revisar pedindo correção →
    // Corrigir → Revisar enquanto houver rodadas; sem Corrigir ou sem rodadas, bloqueia.
    private static IReadOnlyList<PassoDoRoteiro> NormalizarRoteiro(IEnumerable<PassoDoRoteiro> valores)
    {
        var lista = new List<PassoDoRoteiro>();
        foreach (var passo in valores)
        {
            ArgumentNullException.ThrowIfNull(passo, nameof(valores));
            if (!Enum.IsDefined(passo.Tipo)) throw new ArgumentOutOfRangeException(nameof(valores), "Passo inválido.");
            var papel = passo.Papel?.Trim().ToLowerInvariant();
            if (passo.Tipo == TipoDePassoDoRoteiro.AprovacaoHumana)
            {
                if (!string.IsNullOrEmpty(papel))
                    throw new ArgumentException("A aprovação humana não é executada por um papel.", nameof(valores));
                lista.Add(new PassoDoRoteiro(passo.Tipo));
                continue;
            }
            if (!PapelValido(papel))
                throw new ArgumentException($"O passo {passo.Tipo} precisa de um papel válido.", nameof(valores));
            lista.Add(new PassoDoRoteiro(passo.Tipo, papel));
        }
        var tipos = lista.Select(passo => passo.Tipo).ToArray();
        TipoDePassoDoRoteiro[] semCorrecao =
            [TipoDePassoDoRoteiro.Implementar, TipoDePassoDoRoteiro.Revisar, TipoDePassoDoRoteiro.AprovacaoHumana];
        TipoDePassoDoRoteiro[] comCorrecao =
        [
            TipoDePassoDoRoteiro.Implementar, TipoDePassoDoRoteiro.Revisar, TipoDePassoDoRoteiro.Corrigir,
            TipoDePassoDoRoteiro.AprovacaoHumana
        ];
        if (!tipos.SequenceEqual(semCorrecao) && !tipos.SequenceEqual(comCorrecao))
            throw new ArgumentException(
                "Roteiro não suportado: use implementar, revisar, corrigir (opcional) e aprovar, nesta ordem.",
                nameof(valores));
        return lista.AsReadOnly();
    }

    private PoliticaDoGrupo NormalizarPolitica(PoliticaDoGrupo politica)
    {
        var temCorrecao = Roteiro.Any(passo => passo.Tipo == TipoDePassoDoRoteiro.Corrigir);
        if (temCorrecao && politica.RodadasDeRevisaoMaximas is < 1 or > RodadasMaximasPermitidas)
            throw new ArgumentException(
                $"Rodadas de revisão: de 1 a {RodadasMaximasPermitidas} quando o roteiro tem correção.", nameof(politica));
        if (!temCorrecao && politica.RodadasDeRevisaoMaximas != 0)
            throw new ArgumentException("Sem o passo corrigir, o roteiro não tem rodadas de correção (use 0).",
                nameof(politica));
        // Estritamente sequencial nesta etapa: limite global operacional 1 (AD-63 inativa).
        if (politica.ParalelismoMaximo != 1)
            throw new ArgumentException("O roteiro é sequencial: paralelismo máximo 1.", nameof(politica));
        if (politica.DuracaoMaximaEmMinutos is < 1 or > DuracaoMaximaPermitidaEmMinutos)
            throw new ArgumentException(
                $"Duração máxima: de 1 a {DuracaoMaximaPermitidaEmMinutos} minutos.", nameof(politica));
        var repositorios = (politica.RepositoriosPermitidos ?? []).Select(alias => alias?.Trim().ToLowerInvariant() ?? "")
            .Distinct(StringComparer.Ordinal).ToArray();
        if (repositorios.Any(alias => !FormatoDoAlias.IsMatch(alias)))
            throw new ArgumentException("Repositório inválido: use @alias.", nameof(politica));
        if (repositorios.Length > MaximoDeRepositorios)
            throw new ArgumentException($"No máximo {MaximoDeRepositorios} repositórios.", nameof(politica));
        Array.Sort(repositorios, StringComparer.Ordinal);
        return politica with { RepositoriosPermitidos = Array.AsReadOnly(repositorios) };
    }

    private void ValidarRoteiroContraParticipantes()
    {
        foreach (var passo in Roteiro.Where(passo => passo.ExecutadoPorAgente))
        {
            var participante = DoPapel(Participantes, passo.Papel)
                ?? throw new ArgumentException($"O roteiro usa o papel {passo.Papel}, que não está no grupo.",
                    nameof(Roteiro));
            if (passo.Escreve && !participante.PodeEscrever)
                throw new ArgumentException(
                    $"O papel {participante.Papel} executa {Nome(passo.Tipo)} e precisa de escrita no grupo.", nameof(Roteiro));
            // O revisor lê a worktree em sandbox somente leitura; um papel com escrita não revisa.
            if (passo.Tipo == TipoDePassoDoRoteiro.Revisar && participante.PodeEscrever)
                throw new ArgumentException(
                    $"O papel {participante.Papel} revisa e não pode ter escrita no grupo.", nameof(Roteiro));
        }
        // Implementar exige escrita e revisar a recusa: os dois nunca caem no mesmo papel.
        foreach (var participante in Participantes)
        {
            var executa = false;
            foreach (var passo in Roteiro) executa |= passo.Papel == participante.Papel;
            if (!executa)
                throw new ArgumentException($"O papel {participante.Papel} não executa nenhum passo do roteiro.",
                    nameof(Roteiro));
        }
    }

    private static string? NormalizarTexto(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;
        var texto = valor.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (texto.Length > TamanhoMaximoDasResponsabilidades)
            throw new ArgumentException(
                $"Responsabilidades passam de {TamanhoMaximoDasResponsabilidades} caracteres.", nameof(valor));
        return texto;
    }

    internal static string Nome(TipoDePassoDoRoteiro tipo) => tipo switch
    {
        TipoDePassoDoRoteiro.Implementar => "implementar",
        TipoDePassoDoRoteiro.Revisar => "revisar",
        TipoDePassoDoRoteiro.Corrigir => "corrigir",
        _ => "aprovar"
    };
}
