namespace Dante.Application.Execucoes;

// Execução Avulsa (job legado) gravada com o seu trabalho na fila. Novo=false: a chave de deduplicação já existia e
// nada foi criado; Numero é o da execução existente.
public sealed record JobNaFilaDto(long Numero, Guid IdExecucao, bool Novo);
