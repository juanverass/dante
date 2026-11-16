using Dante.Application.Comum;

namespace Dante.Application.Execucoes;

// Confirma as alterações dos repositories do ledger (AD-66), que vivem num contexto próprio, separado do Brain.
// Conflito de xmin ou de chave única vira ConflitoDeConcorrenciaException, como no IUnitOfWork do Brain.
public interface IUnitOfWorkDaOrquestracao : IUnitOfWork;
