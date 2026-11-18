using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dante.Domain.Comum;
using Dante.Domain.Grupos;

namespace Dante.Domain.Execucoes;

// Estado durável de um Roteiro (AD-69, #308, decisão B), um por execução: o snapshot da versão do grupo (id, número,
// hash, roteiro e limite de rodadas), o hash do snapshot inteiro (grupo + versão e hash de cada participante) e o
// objetivo como pode ser guardado. O objetivo bruto nunca é persistido: é redigido antes, e o que fica é o texto
// redigido, se a redação o alterou e o SHA-256 do original (com a execução como sal) para detectar divergência, sem
// guardá-lo. Objetivo alterado pela redação não serve para uma etapa posterior a um reinício: o orquestrador pede um
// objetivo sanitizado novo à pessoa, que substitui este (único campo mutável, além da versão técnica).
public sealed class RoteiroDaExecucao : EntidadeBase
{
    public const int TamanhoMaximoDoObjetivo = 8000;
    public const int TamanhoDoHash = 64;

    private RoteiroDaExecucao()
    {
    }

    public RoteiroDaExecucao(Execucao execucao, Guid idGrupo, string nomeDoGrupo, Guid idVersaoDoGrupo,
        int numeroDaVersaoDoGrupo, string hashDaVersaoDoGrupo, IReadOnlyList<PassoDoRoteiro> passos,
        int rodadasDeRevisaoMaximas, IReadOnlyList<ParticipanteDaExecucao> participantes, string objetivoRedigido,
        bool objetivoAlteradoPelaRedacao, string hashDoObjetivoOriginal)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        ArgumentNullException.ThrowIfNull(passos);
        ArgumentNullException.ThrowIfNull(participantes);
        if (execucao.Tipo != TipoDeExecucao.Roteiro || execucao.IdVersaoDoGrupo != idVersaoDoGrupo)
            throw new InvalidOperationException("O snapshot precisa ser da versão do grupo congelada na execução.");
        if (hashDaVersaoDoGrupo is not { Length: TamanhoDoHash })
            throw new ArgumentException("Hash da versão do grupo inválido.", nameof(hashDaVersaoDoGrupo));
        if (participantes.Count == 0) throw new ArgumentException("Snapshot sem participantes.", nameof(participantes));
        // Laço explícito: o Domain não gera classes de closure (teste de arquitetura).
        foreach (var participante in participantes)
            if (participante.IdExecucao != execucao.Id)
                throw new ArgumentException("Participante de outra execução.", nameof(participantes));
        if (passos.Count == 0) throw new ArgumentException("Roteiro vazio.", nameof(passos));
        if (rodadasDeRevisaoMaximas < 0) throw new ArgumentOutOfRangeException(nameof(rodadasDeRevisaoMaximas));
        IdExecucao = execucao.Id;
        IdGrupo = idGrupo;
        NomeDoGrupo = nomeDoGrupo;
        IdVersaoDoGrupo = idVersaoDoGrupo;
        NumeroDaVersaoDoGrupo = numeroDaVersaoDoGrupo;
        HashDaVersaoDoGrupo = hashDaVersaoDoGrupo;
        Passos = Array.AsReadOnly(passos.ToArray());
        RodadasDeRevisaoMaximas = rodadasDeRevisaoMaximas;
        HashDoSnapshot = CalcularHashDoSnapshot(hashDaVersaoDoGrupo, participantes);
        DefinirObjetivo(objetivoRedigido, objetivoAlteradoPelaRedacao, hashDoObjetivoOriginal);
    }

    public Guid IdExecucao { get; private set; }
    public Guid IdGrupo { get; private set; }
    public string NomeDoGrupo { get; private set; } = "";
    public Guid IdVersaoDoGrupo { get; private set; }
    public int NumeroDaVersaoDoGrupo { get; private set; }
    public string HashDaVersaoDoGrupo { get; private set; } = "";
    public IReadOnlyList<PassoDoRoteiro> Passos { get; private set; } = [];
    public int RodadasDeRevisaoMaximas { get; private set; }
    public string HashDoSnapshot { get; private set; } = "";
    public string ObjetivoRedigido { get; private set; } = "";
    public bool ObjetivoAlteradoPelaRedacao { get; private set; }
    public string HashDoObjetivoOriginal { get; private set; } = "";

    public bool TemCorrecao => Passos.Any(passo => passo.Tipo == TipoDePassoDoRoteiro.Corrigir);

    // O objetivo guardado é utilizável sem o original em memória (por exemplo, depois de um reinício).
    public bool ObjetivoUtilizavel => !ObjetivoAlteradoPelaRedacao;

    public static string HashDoObjetivo(Guid idExecucao, string objetivoOriginal) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            idExecucao.ToString("N", CultureInfo.InvariantCulture) + "\n" + objetivoOriginal)));

    // Hash do snapshot: versão do grupo e, por papel, a versão e o hash de cada participante. Recalculado das linhas
    // gravadas, confirma que ninguém trocou participante nem versão depois da criação.
    public static string CalcularHashDoSnapshot(string hashDaVersaoDoGrupo, IEnumerable<ParticipanteDaExecucao> participantes)
    {
        var texto = new StringBuilder("formato=1\ngrupo=").Append(hashDaVersaoDoGrupo).Append('\n');
        foreach (var participante in participantes.OrderBy(p => p.Papel, StringComparer.Ordinal))
            texto.Append(participante.Papel).Append('=').Append(participante.IdVersaoDoAgente.ToString("N"))
                .Append(':').Append(participante.HashDaConfiguracaoDoAgente).Append(':')
                .Append(participante.PodeEscrever ? "escreve" : "le").Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(texto.ToString())));
    }

    // Objetivo sanitizado novo, fornecido pela pessoa quando o anterior foi alterado pela redação (decisão B). O novo
    // precisa chegar intacto: se a redação também o alterasse, nada muda.
    public void SubstituirObjetivo(string objetivoSanitizado, string hashDoOriginal)
    {
        if (!ObjetivoAlteradoPelaRedacao) throw new InvalidOperationException("O objetivo guardado já é utilizável.");
        DefinirObjetivo(objetivoSanitizado, false, hashDoOriginal);
    }

    private void DefinirObjetivo(string objetivo, bool alterado, string hash)
    {
        if (string.IsNullOrWhiteSpace(objetivo)) throw new ArgumentException("Objetivo obrigatório.", nameof(objetivo));
        if (objetivo.Length > TamanhoMaximoDoObjetivo)
            throw new ArgumentException($"Objetivo passa de {TamanhoMaximoDoObjetivo} caracteres.", nameof(objetivo));
        if (hash is not { Length: TamanhoDoHash }) throw new ArgumentException("Hash do objetivo inválido.", nameof(hash));
        ObjetivoRedigido = objetivo;
        ObjetivoAlteradoPelaRedacao = alterado;
        HashDoObjetivoOriginal = hash;
    }
}
