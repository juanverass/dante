namespace Dante.Application.Execucoes;

// Entrega do artefato aprovado de um roteiro (#311, PR 3; AD-72), primeiro consumidor do gateway, neutra de interface.
// Dois passos humanos separados: o gate do roteiro aprova o artefato (AD-69) e o gateway aprova a entrega. A entrega é
// exatamente um commit local na branch da worktree, pelo Git fechado, sem push, PR nem merge, e nunca no checkout
// principal. O autor é o configurado no host; sem ele, nada é pedido (fail-closed).
// Recusas de entrada: ArgumentException; de estado: InvalidOperationException.
public interface IEntregaDoArtefatoAppService
{
    // Só o dono, só um roteiro concluído com o gate aprovado, com a worktree principal deste host na base e com o
    // conteúdo igual ao aprovado. Cria o pedido no gateway (payload com repositório, branch, base, hash do diff aprovado,
    // mensagem e autor). Se já há uma entrega em aberto desta execução, age sobre ela: pendente é devolvida; aprovada
    // e ainda não executada é executada agora (o caminho depois de um reinício). Uma entrega executada é informada.
    Task<SolicitacaoDeEntregaDto> SolicitarAsync(ProprietarioDaExecucao dono, string codigo, string? mensagem,
        CancellationToken cancellationToken = default);

    // Executa a entrega aprovada pelo dono. Nulo quando o pedido não é uma entrega do artefato (ou não é do dono): quem
    // decide no gateway chama isto depois de toda aprovação, e só a entrega responde. O gateway confere o resto:
    // aprovação válida, prazo, payload remontado com os fatos atuais (conteúdo e HEAD da worktree, autor configurado) e a
    // execução única.
    Task<ExecucaoDeAcaoDto?> ExecutarAprovadaAsync(ProprietarioDaExecucao dono, Guid idAprovacao,
        CancellationToken cancellationToken = default);
}

// Quem conhece o efeito de uma ação protegida descreve o estado real quando a execução fica Incerta (reinício no meio,
// falha inesperada): o estado observado vai ao ledger com o Incerto, em vez de nada. Nunca lança por falha de leitura.
public interface IObservadorDeAcaoProtegida
{
    Dante.Domain.Execucoes.DefinicaoDeAcao Acao { get; }

    Task<string?> ObservarAsync(Dante.Domain.Execucoes.AprovacaoDeAcao aprovacao, CancellationToken cancellationToken = default);
}
