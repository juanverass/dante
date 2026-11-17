namespace Dante.Application.Execucoes;

// Controle de execuções pelo proprietário (#305), neutro de interface: o Telegram é o primeiro adaptador; um chat
// próprio ou a WebApi consomem o mesmo caso de uso. Execução de outro proprietário responde exatamente como inexistente.
public interface IControleDeExecucoesAppService
{
    // codigo: J… ou E…, como exibido. motivo: opcional, cortado e redigido antes de gravar.
    //
    // Nenhum processo com a posse (trabalho pendente, ou execução interrompida/bloqueada sem trabalho ativo): a
    // execução, as etapas não terminais e o trabalho pendente são cancelados numa transação, com o trabalho travado
    // (FOR UPDATE) e a execução travada logo depois, na mesma ordem das gravações sob posse. Trabalho reivindicado:
    // quem pede não tem posse e não muda estado; grava só o evento CancelamentoSolicitado (autor, motivo), que quem tem
    // a posse honra antes de iniciar, no heartbeat e antes de concluir, e sinaliza o processo local (atalho). Job legado
    // (J…) com trabalho ativo pertence ao registro em memória do job e não é alterado aqui.
    Task<CancelamentoDeExecucaoDto> SolicitarCancelamentoAsync(ProprietarioDaExecucao proprietario, string codigo,
        string? motivo, CancellationToken cancellationToken = default);
}
