namespace Dante.Domain.Execucoes;

// O que uma decisão do gate humano significa (D4, AD-75). ArtefatoDoRoteiro: aceitar o diff revisado de um Roteiro
// (#308). PlanoSomenteLeitura: autorizar um plano de coordenação, identificado pelo hash da forma canônica, a ser
// executado só com delegações de leitura (#310). Nenhuma das duas é aprovação do gateway de ações protegidas (#311):
// não autorizam escrita, commit, push, merge nem outra ação protegida.
public enum FinalidadeDaAprovacao
{
    ArtefatoDoRoteiro,
    PlanoSomenteLeitura
}
