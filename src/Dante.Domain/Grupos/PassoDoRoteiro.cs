namespace Dante.Domain.Grupos;

// Um passo do roteiro: o tipo e o papel do grupo que o executa. A aprovação humana não tem papel.
public sealed record PassoDoRoteiro(TipoDePassoDoRoteiro Tipo, string? Papel = null)
{
    public bool ExecutadoPorAgente => Tipo != TipoDePassoDoRoteiro.AprovacaoHumana;

    // Implementar e Corrigir alteram a worktree; Revisar e a aprovação só leem.
    public bool Escreve => Tipo is TipoDePassoDoRoteiro.Implementar or TipoDePassoDoRoteiro.Corrigir;
}
