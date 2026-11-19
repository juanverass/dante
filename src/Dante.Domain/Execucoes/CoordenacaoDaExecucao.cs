using Dante.Domain.Comum;

namespace Dante.Domain.Execucoes;

// Estado durável de uma Coordenada (AD-75, #310), um por execução: o snapshot da versão do grupo (id, número, hash), o
// papel do coordenador, o hash do snapshot inteiro (grupo + versão e hash de cada participante, o mesmo cálculo do
// Roteiro) e o objetivo como pode ser guardado. É a autoridade congelada da execução: o plano só é validado contra
// estes participantes e as versões deles, e uma mudança posterior no cadastro (agente editado, grupo reativado em outra
// versão) nunca amplia o que a execução pode fazer. O objetivo bruto nunca é persistido (mesma regra do Roteiro).
public sealed class CoordenacaoDaExecucao : EntidadeBase
{
    public const int TamanhoMaximoDoObjetivo = RoteiroDaExecucao.TamanhoMaximoDoObjetivo;
    public const int TamanhoDoHash = 64;

    private CoordenacaoDaExecucao()
    {
    }

    public CoordenacaoDaExecucao(Execucao execucao, Guid idGrupo, string nomeDoGrupo, Guid idVersaoDoGrupo,
        int numeroDaVersaoDoGrupo, string hashDaVersaoDoGrupo, string papelDoCoordenador,
        IReadOnlyList<ParticipanteDaExecucao> participantes, string objetivoRedigido, bool objetivoAlteradoPelaRedacao,
        string hashDoObjetivoOriginal)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        ArgumentNullException.ThrowIfNull(participantes);
        if (execucao.Tipo != TipoDeExecucao.Coordenada || execucao.IdVersaoDoGrupo != idVersaoDoGrupo)
            throw new InvalidOperationException("O snapshot precisa ser da versão do grupo congelada na coordenação.");
        if (hashDaVersaoDoGrupo is not { Length: TamanhoDoHash })
            throw new ArgumentException("Hash da versão do grupo inválido.", nameof(hashDaVersaoDoGrupo));
        if (participantes.Count < Grupos.ConfiguracaoDoGrupo.MinimoDeParticipantesCoordenados)
            throw new ArgumentException("A coordenação precisa do coordenador e de quem receba as unidades.",
                nameof(participantes));
        ParticipanteDaExecucao? coordenador = null;
        // Laço explícito: o Domain não gera classes de closure (teste de arquitetura).
        foreach (var participante in participantes)
        {
            if (participante.IdExecucao != execucao.Id)
                throw new ArgumentException("Participante de outra execução.", nameof(participantes));
            // Escrita em grupo coordenado depende do gateway (#311): o snapshot também a recusa.
            if (participante.PodeEscrever)
                throw new ArgumentException($"O papel {participante.Papel} escreve; a coordenação é somente leitura.",
                    nameof(participantes));
            if (participante.Papel == papelDoCoordenador) coordenador = participante;
        }
        if (coordenador is null)
            throw new ArgumentException("O coordenador não está entre os participantes.", nameof(papelDoCoordenador));
        // A execução aponta para a versão do coordenador congelada no snapshot, e só para ela.
        if (execucao.IdVersaoDoAgente != coordenador.IdVersaoDoAgente)
            throw new InvalidOperationException("A execução não aponta para a versão congelada do coordenador.");
        IdExecucao = execucao.Id;
        IdGrupo = idGrupo;
        NomeDoGrupo = nomeDoGrupo;
        IdVersaoDoGrupo = idVersaoDoGrupo;
        NumeroDaVersaoDoGrupo = numeroDaVersaoDoGrupo;
        HashDaVersaoDoGrupo = hashDaVersaoDoGrupo;
        PapelDoCoordenador = papelDoCoordenador;
        HashDoSnapshot = RoteiroDaExecucao.CalcularHashDoSnapshot(hashDaVersaoDoGrupo, participantes);
        DefinirObjetivo(objetivoRedigido, objetivoAlteradoPelaRedacao, hashDoObjetivoOriginal);
    }

    public Guid IdExecucao { get; private set; }
    public Guid IdGrupo { get; private set; }
    public string NomeDoGrupo { get; private set; } = "";
    public Guid IdVersaoDoGrupo { get; private set; }
    public int NumeroDaVersaoDoGrupo { get; private set; }
    public string HashDaVersaoDoGrupo { get; private set; } = "";
    public string PapelDoCoordenador { get; private set; } = "";
    public string HashDoSnapshot { get; private set; } = "";
    public string ObjetivoRedigido { get; private set; } = "";
    public bool ObjetivoAlteradoPelaRedacao { get; private set; }
    public string HashDoObjetivoOriginal { get; private set; } = "";

    public bool ObjetivoUtilizavel => !ObjetivoAlteradoPelaRedacao;

    // Confere, pelas linhas gravadas, que ninguém trocou participante nem versão depois da criação.
    public bool SnapshotConfere(IEnumerable<ParticipanteDaExecucao> participantes) =>
        RoteiroDaExecucao.CalcularHashDoSnapshot(HashDaVersaoDoGrupo, participantes) == HashDoSnapshot;

    // Objetivo sanitizado novo quando o anterior foi alterado pela redação (mesma regra do Roteiro).
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
