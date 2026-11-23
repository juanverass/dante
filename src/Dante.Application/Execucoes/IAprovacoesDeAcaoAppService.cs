namespace Dante.Application.Execucoes;

// Aprovação de ação protegida no gateway (AD-72, #311, PR 2), neutra de interface. Toda regra vive aqui e no domínio:
// catálogo e política, solicitante pela identidade da sessão ou do comando, decisão única só do dono da raiz e só com o
// hash exibido, expiração, revogação (agente desativado ou com outra versão) e execução única por CAS. A interface
// (Telegram hoje) só apresenta e encaminha. Fail-closed: sem este caso de uso (sem banco ou sem a orquestração),
// nenhuma ação protegida é pedida, decidida nem executada.
public interface IAprovacoesDeAcaoAppService
{
    // Cria o pedido (ou devolve o vigente do mesmo payload). Ação fora do catálogo ou não protegida é recusada.
    Task<PedidoDeAcaoDto> SolicitarAsync(SolicitacaoDeAcaoDto solicitacao, CancellationToken cancellationToken = default);

    // Pedidos pendentes ou aprovados e ainda não executados do dono, do ledger (sobrevivem ao reinício).
    Task<IReadOnlyList<AprovacaoDeAcaoDto>> ListarEmAbertoAsync(ProprietarioDaExecucao dono,
        CancellationToken cancellationToken = default);

    Task<AprovacaoDeAcaoDto?> ObterAsync(ProprietarioDaExecucao dono, Guid id, CancellationToken cancellationToken = default);

    // Aprovar exige o hash exibido (ao menos 12 caracteres do início); hash diferente é recusado com a mensagem.
    Task<DecisaoDeAcaoDto> AprovarAsync(ProprietarioDaExecucao decisor, Guid id, string? hashConfirmado,
        CancellationToken cancellationToken = default);

    Task<DecisaoDeAcaoDto> RecusarAsync(ProprietarioDaExecucao decisor, Guid id, string? motivo,
        CancellationToken cancellationToken = default);

    // Execução única de uma ação aprovada, pelo consumidor da ação. Remonta o payload, compara o hash, grava o início
    // (CAS) e só então executa; exceção ou cancelamento durante a ação vira Incerta, nunca repetida.
    Task<ExecucaoDeAcaoDto> ExecutarAsync(Guid id, ExecucaoDaAcaoProtegida execucao,
        CancellationToken cancellationToken = default);

    // Expira os pedidos com prazo vencido (pendentes ou aprovados e não iniciados).
    Task<IReadOnlyList<AprovacaoDeAcaoDto>> ExpirarVencidasAsync(CancellationToken cancellationToken = default);

    // Depois de um reinício: as execuções que estavam em andamento ficam Incertas (o processo que as executava não
    // existe mais e o efeito não é conhecido) e os vencidos expiram. Nada é repetido.
    Task<ManutencaoDeAprovacoesDto> RecuperarAposReinicioAsync(CancellationToken cancellationToken = default);
}
