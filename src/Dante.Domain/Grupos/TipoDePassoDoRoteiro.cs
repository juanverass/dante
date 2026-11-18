namespace Dante.Domain.Grupos;

// O que cada passo do roteiro faz (AD-69, #308). O significado é fixo e conhecido pelo orquestrador; o roteiro só
// escolhe quais passos usa e qual papel executa cada um.
// Implementar: escreve numa worktree da execução. Revisar: lê a worktree e registra um veredito estruturado.
// Corrigir: escreve na mesma worktree a partir do veredito, e o roteiro volta a Revisar. AprovacaoHumana: o gate final.
public enum TipoDePassoDoRoteiro
{
    Implementar,
    Revisar,
    Corrigir,
    AprovacaoHumana
}
