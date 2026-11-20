namespace Dante.Application.Execucoes;

// Coordenação por papel (#310, AD-75): casos de uso neutros de interface (PR 2), chamados pelo executor da fila, pelas
// ferramentas do coordenador no dante_coordenacao e pela interface (PR 3). As regras são as do domínio da PR 1 (ValidacaoDoPlano, PlanoDaCoordenacao,
// PedidoDeAprovacao.DoPlano, Delegacao.CriarDoPlano); aqui ficam a ordem das gravações, a fila e o ciclo da execução.
// Toda gravação de quem executa é sob posse do trabalho (AD-67); as decisões da pessoa são do proprietário (AD-74), e
// uma execução de outro dono é indistinguível de inexistente. Paralelismo 1: uma unidade em curso por vez.
// Recusas de entrada: ArgumentException; de estado: InvalidOperationException; posse perdida:
// PosseDoTrabalhoPerdidaException, sem nada gravado.
public interface ICoordenacoesAppService
{
    // Numa transação: execução Coordenada com a versão do grupo e a do coordenador congeladas, o snapshot (participantes,
    // coordenação e hash), a etapa de planejamento e o trabalho que a despacha. Objetivo com aparência de credencial é
    // recusado (nada é gravado): o coordenador relê o objetivo do ledger em qualquer processo. Chave existente devolve a
    // execução dela (Novo=false).
    Task<ExecucaoNaFilaDto> EnfileirarAsync(NovaCoordenacaoDto nova, CancellationToken cancellationToken = default);

    Task<CoordenacaoDaExecucaoDto?> ObterAsync(Guid idExecucao, CancellationToken cancellationToken = default);

    // Acompanhamento: só do proprietário; de outro dono é indistinguível de inexistente.
    Task<CoordenacaoDaExecucaoDto?> ObterDoProprietarioAsync(ProprietarioDaExecucao proprietario, string codigo,
        CancellationToken cancellationToken = default);

    // Conferência antes de qualquer processo: o snapshot recalculado das linhas, a versão do grupo íntegra e com o hash
    // congelado e cada versão de agente íntegra e com o hash congelado. Nulo quando confere; senão, o motivo.
    Task<string?> ConferirIntegridadeAsync(Guid idExecucao, CancellationToken cancellationToken = default);

    // Para o executor da etapa de planejamento (só leitura): tudo vem do ledger e do snapshot. Etapa que não é de
    // planejamento em curso ou a planejar: InvalidOperationException.
    Task<PlanejamentoDaCoordenacaoDto> ObterPlanejamentoAsync(Guid idExecucao, Guid idEtapa,
        CancellationToken cancellationToken = default);

    // A proposta do coordenador, sob posse da etapa de planejamento em curso. A validação é a pura do domínio, contra o
    // snapshot, inteira antes de qualquer gravação de plano. Válida: revisão do plano (inicial, ou substituta pelo motivo
    // da etapa), gate aberto com o hash calculado no servidor e a execução aguardando a decisão; o trabalho termina.
    // Inválida: as recusas ficam no ledger (só motivos, nunca o conteúdo) e voltam ao coordenador; na segunda vez
    // (CorrecoesPermitidas = 1), a etapa e a execução falham sem plano, filhos nem trabalhos. Proposta nula: o
    // coordenador não entregou nada; conta como recusa.
    Task<RegistroDaPropostaDto> RegistrarPropostaAsync(PosseDoTrabalho posse, Guid idEtapa, string? proposta,
        CancellationToken cancellationToken = default);

    // O planejamento terminou sem proposta por falha do processo (sessão que não abriu, turno que falhou, integridade):
    // etapa e execução falham com o motivo; uma revisão em espera é encerrada. Mesma conclusão sob posse.
    Task<RegistroDaPropostaDto> FalharPlanejamentoAsync(PosseDoTrabalho posse, Guid idEtapa, string motivo,
        CancellationToken cancellationToken = default);

    // Decisão da pessoa no gate do plano, uma vez, com o hash do plano exibido (ao menos 12 caracteres) para aprovar ou
    // corrigir. Aprovar: plano Aprovado (só leitura; não é aprovação do gateway da #311), unidades materializadas e a
    // etapa de avanço que despacha a primeira. Corrigir: a instrução vai a uma nova etapa de planejamento (revisão
    // PedidoHumano), dentro do limite de 3 versões. Recusar: plano Recusado e execução Cancelada. Integridade conferida
    // antes de decidir.
    Task<ResultadoDaDecisaoDto> DecidirAsync(ProprietarioDaExecucao proprietario, string codigo, DecisaoHumanaDto decisao,
        CancellationToken cancellationToken = default);

    // Etapa mecânica de avanço, sob posse (o número da subordinada é reservado antes, fora da transação): encerra as unidades
    // cujas delegações terminaram (pelo desfecho delas), despacha a próxima unidade pronta do plano aprovado (uma por
    // vez) e, sem nenhuma, conclui a execução (todas respondidas), abre um replanejamento por unidade sem resposta (se
    // couber revisão e orçamento) ou falha com as dependentes ignoradas. Despacho recusado pelo domínio (agente
    // desativado, orçamento): a unidade falha com o motivo, sem delegação.
    Task<AvancoDaCoordenacaoDto> AvancarAsync(PosseDoTrabalho posse, Guid idEtapa,
        CancellationToken cancellationToken = default);

    // Consolidação mecânica (D7) a partir do ledger: todas as unidades de todas as revisões, com proveniência e desfecho.
    Task<ConsolidacaoMecanicaDto?> ConsolidarAsync(Guid idExecucao, CancellationToken cancellationToken = default);

    Task<ConsolidacaoMecanicaDto?> ConsolidarDoProprietarioAsync(ProprietarioDaExecucao proprietario, string codigo,
        CancellationToken cancellationToken = default);

    // Síntese do coordenador (D7, PR 3). Aberta pelo avanço que encerra as unidades, só se ao menos uma foi respondida;
    // o estado final da execução continua decidido pelas unidades. Para o executor (só leitura): a trilha mecânica e a
    // versão congelada do coordenador. Etapa que não é síntese em aberto: InvalidOperationException.
    Task<SinteseParaRedigirDto> ObterSinteseAsync(Guid idExecucao, Guid idEtapa, CancellationToken cancellationToken = default);

    // A síntese entregue pelo coordenador, sob posse da etapa de síntese em curso. Validação pura do domínio
    // (ValidacaoDaSintese) contra a trilha recalculada do ledger, inteira antes de qualquer gravação, e sem credenciais.
    // Válida: síntese gravada (hash do servidor), etapa concluída e a execução encerrada pelo desfecho das unidades.
    // Inválida: as recusas ficam no ledger (só motivos) e voltam ao coordenador; na segunda vez, a etapa falha e a
    // execução se encerra só com a trilha. Nula: nada entregue, conta como recusa.
    Task<RegistroDaSinteseDto> RegistrarSinteseAsync(PosseDoTrabalho posse, Guid idEtapa, string? sintese,
        CancellationToken cancellationToken = default);

    // A síntese terminou sem entrega por falha do processo (sessão, turno, integridade): a etapa falha com o motivo e a
    // execução se encerra pelo desfecho das unidades, só com a trilha.
    Task<RegistroDaSinteseDto> FalharSinteseAsync(PosseDoTrabalho posse, Guid idEtapa, string motivo,
        CancellationToken cancellationToken = default);

    // Trilha mecânica e síntese validada, separadas; do proprietário (de outro dono é indistinguível de inexistente).
    Task<ResultadoDaCoordenacaoDto?> ObterResultadoAsync(Guid idExecucao, CancellationToken cancellationToken = default);

    Task<ResultadoDaCoordenacaoDto?> ObterResultadoDoProprietarioAsync(ProprietarioDaExecucao proprietario, string codigo,
        CancellationToken cancellationToken = default);
}
