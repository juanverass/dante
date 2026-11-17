namespace Dante.Application.Execucoes;

// Quem consulta ou controla uma execução (#305): o canal que recebeu o pedido e a pessoa nele, a mesma autoridade de
// origem gravada na execução (AD-66). Neutro de interface: o Telegram informa o seu canal e o id do usuário autorizado;
// uma interface futura informa o próprio canal e identidade autenticada. Execução de outro proprietário é tratada
// exatamente como inexistente.
public sealed record ProprietarioDaExecucao
{
    public ProprietarioDaExecucao(string canal, long idDoUsuario)
    {
        if (string.IsNullOrWhiteSpace(canal) || canal.Length > Dante.Domain.Execucoes.Execucao.TamanhoMaximoDoCanal)
            throw new ArgumentException("Canal do proprietário inválido.", nameof(canal));
        Canal = canal;
        IdDoUsuario = idDoUsuario;
    }

    public string Canal { get; }
    public long IdDoUsuario { get; }

    public bool EDono(string canal, long idDoUsuario) =>
        string.Equals(Canal, canal, StringComparison.Ordinal) && IdDoUsuario == idDoUsuario;

    // Registrado como autor nos eventos do ledger.
    public string Autor => $"{Canal}:{IdDoUsuario.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
