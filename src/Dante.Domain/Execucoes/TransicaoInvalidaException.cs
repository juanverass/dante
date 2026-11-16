namespace Dante.Domain.Execucoes;

// Transição recusada pela máquina de estados de execução, etapa ou sessão do ledger (AD-66). Nada foi alterado.
public sealed class TransicaoInvalidaException(string entidade, string de, string para) : InvalidOperationException(
    $"Transição inválida de {entidade}: {de} → {para}.")
{
    public string De { get; } = de;
    public string Para { get; } = para;
}
