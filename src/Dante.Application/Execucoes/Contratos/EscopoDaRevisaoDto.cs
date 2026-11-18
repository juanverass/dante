namespace Dante.Application.Execucoes;

// O que uma sessão de revisão pode alcançar (#308, decisões D e E), fixado pelo orquestrador ao abrir a sessão e nunca
// informado pelo agente: a execução, a etapa de revisão em curso, a rodada e o hash do diff entregue ao revisor.
public sealed record EscopoDaRevisaoDto(Guid IdExecucao, string Codigo, Guid IdEtapa, int Rodada, string HashDoArtefato);
