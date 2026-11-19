namespace Dante.Application.Execucoes;

// Delegações rastreadas entre execuções (#309, AD-70), PR 2: casos de uso internos, neutros de interface (o MCP do
// dante_coordenacao, no PR 3, é só um adaptador). As regras são as do domínio (Delegacao.Criar e as transições); aqui
// ficam a autorização pelo proprietário, a serialização por raiz, a fila e o ciclo da origem.
// Recusas de regra: DelegacaoRecusadaException com o motivo; de entrada: ArgumentException; de estado:
// InvalidOperationException; posse perdida: PosseDoTrabalhoPerdidaException, sem nada gravado.
public interface IDelegacoesAppService
{
    // Solicitar e aceitar numa transação, serializada por raiz: trava a raiz (e a origem) antes de contar e inserir,
    // então duas chaves diferentes nunca consomem juntas as últimas vagas do orçamento. A mesma unidade para o mesmo
    // destinatário devolve a original como está (Nova=false, em qualquer estado); para outro, UnidadeJaAtribuida. Nova
    // delegação: subordinada (Delegada, Pendente) com a etapa de leitura, delegação Solicitada → Aceita, eventos e o
    // trabalho da fila, tudo ou nada. Origem de outro proprietário é tratada como inexistente.
    Task<DelegacaoSolicitadaDto> SolicitarAsync(SolicitacaoDeDelegacaoDto solicitacao,
        CancellationToken cancellationToken = default);

    // A mesma solicitação feita pela sessão da etapa de origem pelo dante_coordenacao (#309, PR 3): gravada sob a posse
    // do trabalho da origem (fencing: posse perdida não cria nada) e só da sessão vinculada sob posse a essa etapa, do
    // mesmo dono e canal (outra sessão é recusada como não autorizada). Mesmas regras, trava e identidade.
    Task<DelegacaoSolicitadaDto> SolicitarSobPosseAsync(PosseDoTrabalho posse, long numeroDaSessao,
        SolicitacaoDeDelegacaoDto solicitacao, CancellationToken cancellationToken = default);

    // Delegações pedidas por uma execução de origem, com o estado e o último marco de progresso, para o proprietário
    // (de outro dono: lista vazia). Só leitura.
    Task<IReadOnlyList<AcompanhamentoDaDelegacaoDto>> AcompanharDaOrigemAsync(ProprietarioDaExecucao proprietario,
        Guid idExecucaoOrigem, CancellationToken cancellationToken = default);

    // Consulta pelo proprietário; de outro dono é indistinguível de inexistente.
    Task<DelegacaoDto?> ObterAsync(ProprietarioDaExecucao proprietario, Guid idDelegacao,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DelegacaoDto>> ListarDaRaizAsync(ProprietarioDaExecucao proprietario, Guid idExecucaoRaiz,
        CancellationToken cancellationToken = default);

    // Cancelamento pedido pelo proprietário da árvore para uma delegação: o cancelamento durável da subordinada (#305),
    // pelo ledger e pela fila. Sem dono, a subordinada termina na hora; com dono, quem tem a posse o honra. Em ambos, a
    // delegação termina Cancelada e a origem que espera é retomada.
    Task<CancelamentoDeExecucaoDto> CancelarAsync(ProprietarioDaExecucao proprietario, Guid idDelegacao, string? motivo,
        CancellationToken cancellationToken = default);

    // Executor (sob a posse do trabalho da subordinada). Conferência antes de qualquer processo: destinatário ativo, versão
    // congelada íntegra e ainda a do pedido, concessão de coordenação do solicitante, contrato, entrada canônica,
    // autoridade (canal e pessoa) e raiz, capacidade, só leitura e prazos. Só leitura.
    Task<ConferenciaDaDelegacaoDto> ConferirAsync(Guid idExecucaoSubordinada, CancellationToken cancellationToken = default);

    // Delegação Aceita → EmAndamento (uma vez; uma retomada da subordinada a encontra já em andamento) e etapa e
    // subordinada em execução. Devolve o limite da duração. Prazo excedido: PrazoDaDelegacaoExcedidoException; pedido de
    // cancelamento: CancelamentoSolicitadoException. Nada é gravado nesses casos.
    Task<DateTimeOffset> IniciarAsync(PosseDoTrabalho posse, Guid idEtapa, CancellationToken cancellationToken = default);

    Task RegistrarProgressoAsync(PosseDoTrabalho posse, Guid idEtapa, MarcoDaDelegacao marco,
        CancellationToken cancellationToken = default);

    // Validação do resultado sem gravar (para o executor pedir uma correção ao agente). Nulo quando vale.
    Task<string?> ValidarResultadoAsync(Guid idExecucaoSubordinada, string resultado,
        CancellationToken cancellationToken = default);

    // Resultado validado pelo contrato e gravado uma vez, da sessão vinculada à etapa (nunca outra), dentro da duração;
    // com a subordinada Concluida, os eventos e a retomada da origem, na transação que termina o trabalho. Repetir o
    // mesmo conteúdo depois de gravado é idempotente; outro conteúdo, recusado. Pedido de cancelamento registrado:
    // Cancelada no lugar do resultado. Resultado inválido: ArgumentException, nada gravado.
    Task<DesfechoDaDelegacaoDto> ConcluirAsync(PosseDoTrabalho posse, Guid idEtapa, EntregaDaDelegacaoDto entrega,
        CancellationToken cancellationToken = default);

    // Falha do executor (recusa na conferência, sessão que não abriu, resultado fora do contrato): delegação Falhou.
    Task<DesfechoDaDelegacaoDto> FalharAsync(PosseDoTrabalho posse, Guid idEtapa, string motivo,
        CancellationToken cancellationToken = default);

    // Prazo para iniciar ou duração máxima excedidos: delegação Expirada e subordinada Cancelada com o motivo.
    Task<DesfechoDaDelegacaoDto> ExpirarAsync(PosseDoTrabalho posse, Guid idEtapa, string motivo,
        CancellationToken cancellationToken = default);

    // A etapa de retomada da origem (a seguinte à que delegou): o pedido congelado e os desfechos, para o executor da
    // origem. Nulo quando a etapa não é uma retomada; versão congelada adulterada: InvalidOperationException.
    Task<RetomadaDaOrigemDto?> ObterRetomadaAsync(Guid idExecucao, Guid idEtapa, CancellationToken cancellationToken = default);

    // Recuperação: retoma as origens em AguardandoDelegacoes sem delegação aberta (por exemplo, gravadas antes da
    // retomada na mesma transação). Idempotente; devolve quantas foram retomadas.
    Task<int> RetomarOrigensProntasAsync(CancellationToken cancellationToken = default);
}
