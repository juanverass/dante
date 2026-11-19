using Dante.Domain.Comum;

namespace Dante.Domain.Execucoes;

// Snapshot de um participante de um Roteiro (AD-69, #308) ou de uma Coordenada (AD-75, #310): papel, escrita e
// responsabilidades da versão do grupo e a versão do agente resolvida no momento da criação, com o hash dela. Imutável
// (trigger): editar o agente ou o grupo depois nunca alcança a execução, e a reprodução usa exatamente estas versões.
public sealed class ParticipanteDaExecucao : EntidadeBase
{
    public const int TamanhoDoHash = 64;

    private ParticipanteDaExecucao()
    {
    }

    public ParticipanteDaExecucao(Execucao execucao, string papel, string handleDoAgente, Guid idVersaoDoAgente,
        int numeroDaVersaoDoAgente, string hashDaConfiguracaoDoAgente, bool podeEscrever, string? responsabilidades)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        if (execucao.Tipo is not (TipoDeExecucao.Roteiro or TipoDeExecucao.Coordenada))
            throw new InvalidOperationException("Participante só num Roteiro ou numa Coordenada.");
        if (string.IsNullOrWhiteSpace(papel) || string.IsNullOrWhiteSpace(handleDoAgente))
            throw new ArgumentException("Papel e agente obrigatórios.", nameof(papel));
        if (idVersaoDoAgente == Guid.Empty || numeroDaVersaoDoAgente < 1)
            throw new ArgumentException("Versão do agente inválida.", nameof(idVersaoDoAgente));
        if (hashDaConfiguracaoDoAgente is not { Length: TamanhoDoHash })
            throw new ArgumentException("Hash da versão do agente inválido.", nameof(hashDaConfiguracaoDoAgente));
        IdExecucao = execucao.Id;
        Papel = papel;
        HandleDoAgente = handleDoAgente;
        IdVersaoDoAgente = idVersaoDoAgente;
        NumeroDaVersaoDoAgente = numeroDaVersaoDoAgente;
        HashDaConfiguracaoDoAgente = hashDaConfiguracaoDoAgente;
        PodeEscrever = podeEscrever;
        Responsabilidades = responsabilidades;
    }

    public Guid IdExecucao { get; private set; }
    public string Papel { get; private set; } = "";
    public string HandleDoAgente { get; private set; } = "";
    public Guid IdVersaoDoAgente { get; private set; }
    public int NumeroDaVersaoDoAgente { get; private set; }
    public string HashDaConfiguracaoDoAgente { get; private set; } = "";
    public bool PodeEscrever { get; private set; }
    public string? Responsabilidades { get; private set; }
}
