using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Roteiro de um grupo de agentes como execução durável (AD-69, #308, decisões B, C, F e G). Neutro de interface: o
// Telegram é só um adaptador. Toda gravação feita por quem executa é sob posse do trabalho (AD-67); as decisões da
// pessoa são do proprietário da execução (AD-74), e execução de outro dono é indistinguível de inexistente.
// Recusas de entrada: ArgumentException; de estado: InvalidOperationException; corrida: ConflitoDeConcorrenciaException.
public interface IRoteirosAppService
{
    // Numa transação: execução Roteiro com a versão do grupo congelada, o snapshot (versão do grupo e versão e hash de
    // cada participante), o objetivo redigido com o hash do original, a etapa implementar (rodada 0) e o trabalho que
    // a despacha. O objetivo bruto nunca é gravado. Mesma deduplicação da /tarefa: chave existente devolve a execução
    // dela (Novo=false).
    Task<RoteiroCriadoDto> EnfileirarAsync(NovoRoteiroDto novo, CancellationToken cancellationToken = default);

    Task<RoteiroDaExecucaoDto?> ObterAsync(Guid idExecucao, CancellationToken cancellationToken = default);

    // Para a interface: só do proprietário; de outro dono é indistinguível de inexistente.
    Task<RoteiroDaExecucaoDto?> ObterDoProprietarioAsync(ProprietarioDaExecucao proprietario, string codigo,
        CancellationToken cancellationToken = default);

    // Conferência antes de continuar (decisão C): o hash do snapshot recalculado das linhas, a versão do grupo íntegra
    // e com o hash congelado, e cada versão de agente íntegra e com o hash congelado. Nulo quando confere; senão, o
    // motivo da recusa (nada deve ser executado).
    Task<string?> ConferirIntegridadeAsync(Guid idExecucao, CancellationToken cancellationToken = default);

    // Fim de um passo de agente, sob posse, numa transação com a transição seguinte da progressão fixa: o próximo
    // passo e seu trabalho, o gate aberto (etapa de aprovação, pedido com o hash do artefato e execução aguardando, o
    // que libera o slot de escrita sem liberar a worktree) ou o bloqueio. Falha e cancelamento (inclusive o pedido do
    // dono) seguem a conclusão comum da fila.
    Task<AvancoDoRoteiroDto> ConcluirPassoAsync(PosseDoTrabalho posse, Guid idEtapa, ConclusaoDoPassoDto conclusao,
        CancellationToken cancellationToken = default);

    // Quem executa precisa do objetivo e só tem o redigido (reinício depois de uma redação que o alterou): a execução
    // passa a aguardar um objetivo sanitizado da pessoa, sem iniciar a etapa, e o trabalho termina.
    Task SolicitarObjetivoAsync(PosseDoTrabalho posse, Guid idEtapa, CancellationToken cancellationToken = default);

    // Decisão da pessoa no gate (decisão F), uma vez só: aprovar encerra o roteiro (sem commit, push nem PR; a
    // worktree alterada é preservada); corrigir abre a próxima rodada dentro do limite e devolve a execução à fila
    // (o slot é readquirido ao reservar); recusar cancela. Integridade conferida antes de decidir. Aprovar e corrigir
    // valem só para o artefato revisado: o hash do conteúdo atual da worktree é recalculado dentro da transação, antes
    // e depois de gravar a decisão (ainda sem commit); qualquer diferença desfaz tudo e devolve ArtefatoDivergente,
    // com o gate pendente.
    Task<ResultadoDaDecisaoDto> DecidirAsync(ProprietarioDaExecucao proprietario, string codigo, DecisaoHumanaDto decisao,
        CancellationToken cancellationToken = default);

    // Veredito do revisor pela ferramenta do dante_coordenacao (decisões D e E), dentro do escopo fixado pelo
    // orquestrador: só a etapa de revisão em curso, na rodada e com o hash do diff entregues à sessão. Texto com
    // aparência de credencial é recusado. Repetir a mesma entrega é idempotente; outra entrega depois da primeira é
    // recusada. Fora do escopo ou fora de hora: InvalidOperationException.
    Task<RegistroDeVereditoDto> RegistrarVereditoAsync(EscopoDaRevisaoDto escopo, VereditoInformadoDto veredito,
        string? sessao, CancellationToken cancellationToken = default);

    // Versão congelada do agente de um papel do snapshot, conferida (íntegra e com o hash gravado), para o executor
    // abrir a sessão do passo exatamente com ela.
    Task<Dante.Application.Agentes.VersaoDoAgenteDto> ObterVersaoDoParticipanteAsync(Guid idExecucao, string papel,
        CancellationToken cancellationToken = default);

    Task<VereditoRegistrado?> ObterVereditoAsync(Guid idEtapa, CancellationToken cancellationToken = default);

    // Fatos da própria execução para o revisor (somente leitura): rodada, limite, hash revisado, vereditos anteriores e
    // a instrução humana da correção em curso.
    Task<EstadoDaRevisaoDto> ObterEstadoDaRevisaoAsync(EscopoDaRevisaoDto escopo, CancellationToken cancellationToken = default);

    // O conteúdo da worktree mudou depois da revisão (a decisão foi recusada como ArtefatoDivergente): o dono pede uma
    // nova revisão do conteúdo atual. O pedido em vigor é substituído e uma revisão da mesma rodada é enfileirada; com
    // o conteúdo ainda igual ao revisado, recusa (decida sobre ele).
    Task<ResultadoDaDecisaoDto> RevisarNovamenteAsync(ProprietarioDaExecucao proprietario, string codigo,
        CancellationToken cancellationToken = default);

    // Objetivo sanitizado novo para uma execução que o aguarda; recusado se a redação também o alterar.
    Task<bool> SubstituirObjetivoAsync(ProprietarioDaExecucao proprietario, string codigo, string objetivo,
        CancellationToken cancellationToken = default);
}
