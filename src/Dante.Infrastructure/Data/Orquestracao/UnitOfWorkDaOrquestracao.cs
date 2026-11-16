using Dante.Application.Execucoes;
using Dante.Infrastructure.Persistence;

namespace Dante.Infrastructure.Data.Orquestracao;

public sealed class UnitOfWorkDaOrquestracao(OrquestracaoDbContext context) : UnitOfWork(context), IUnitOfWorkDaOrquestracao;
