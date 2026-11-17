namespace Dante.Application.Execucoes;

// Execução nova (Tarefa, AD-68) gravada com o seu trabalho na fila. Novo=false: a chave de deduplicação já existia e
// nada foi criado; Numero e Codigo são os da execução existente.
public sealed record ExecucaoNaFilaDto(long Numero, string Codigo, Guid IdExecucao, bool Novo);
