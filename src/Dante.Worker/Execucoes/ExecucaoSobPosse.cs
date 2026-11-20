using Dante.Application.Execucoes;

namespace Dante.Worker.Execucoes;

// Controle que o executor recebe para gravar o início e o resultado da etapa sob a posse do trabalho (AD-67). Cada
// gravação é um escopo e uma transação curtos, conferindo geração, dono e lease; entre elas o processo do agente roda
// sem nenhuma transação aberta. Posse perdida vira PosseDoTrabalhoPerdidaException e nada é gravado.
public sealed class ExecucaoSobPosse
{
    private readonly IServiceScopeFactory escopos;
    private readonly PosseDoTrabalho posse;
    private readonly Func<Task> pararHeartbeat;

    internal ExecucaoSobPosse(IServiceScopeFactory escopos, TrabalhoReivindicado trabalho, AlvoDoTrabalhoDto alvo,
        CancellationToken posseCancelada, Func<Task> pararHeartbeat)
    {
        this.escopos = escopos;
        this.pararHeartbeat = pararHeartbeat;
        posse = trabalho.Posse;
        Alvo = alvo;
        PosseCancelada = posseCancelada;
    }

    public AlvoDoTrabalhoDto Alvo { get; }

    // Cancelado quando o heartbeat foi recusado ou não conseguiu renovar antes de o lease vencer.
    public CancellationToken PosseCancelada { get; }

    public bool Iniciada { get; private set; }
    public bool Concluida { get; private set; }

    // Estado da etapa efetivamente gravado. Pode diferir do resultado pedido: com pedido de cancelamento do dono
    // registrado (#305), a gravação termina Cancelada e o resultado é descartado.
    public Dante.Domain.Execucoes.EstadoDaEtapa? EstadoFinal { get; private set; }

    // Etapa e execução para EmExecucao, antes de iniciar o processo.
    public async Task IniciarAsync(CancellationToken cancellationToken)
    {
        if (Iniciada) return;
        await using var escopo = escopos.CreateAsyncScope();
        await escopo.ServiceProvider.GetRequiredService<IFilaDeExecucoesAppService>()
            .IniciarEtapaAsync(posse, Alvo.IdEtapa, cancellationToken);
        Iniciada = true;
    }

    // Sessão do ledger (S…) que executa a etapa (#305), gravada sob posse antes do turno. Posse perdida lança
    // PosseDoTrabalhoPerdidaException e nada é gravado.
    public async Task VincularSessaoAsync(long numeroDaSessao, CancellationToken cancellationToken)
    {
        await using var escopo = escopos.CreateAsyncScope();
        await escopo.ServiceProvider.GetRequiredService<IFilaDeExecucoesAppService>()
            .VincularSessaoAsync(posse, Alvo.IdEtapa, numeroDaSessao, cancellationToken);
        NumeroDaSessao = numeroDaSessao;
    }

    // A sessão (S…) vinculada à etapa nesta posse; nula antes do vínculo.
    public long? NumeroDaSessao { get; private set; }

    // Delegação pedida pela sessão da etapa de origem pelo dante_coordenacao (#309, PR 3): sob esta posse e só da sessão
    // vinculada. Sem vínculo (ou com a etapa já concluída), nada é pedido.
    internal async Task<DelegacaoSolicitadaDto> SolicitarDelegacaoAsync(SolicitacaoDeDelegacaoDto solicitacao,
        CancellationToken cancellationToken)
    {
        if (Concluida || NumeroDaSessao is not { } numero)
            throw new InvalidOperationException(DelegacoesAppService.SessaoNaoAutorizada);
        await using var escopo = escopos.CreateAsyncScope();
        return await escopo.ServiceProvider.GetRequiredService<IDelegacoesAppService>()
            .SolicitarSobPosseAsync(posse, numero, solicitacao, cancellationToken);
    }

    // Worktrees prontas da execução (#307), entregues à sessão da etapa de escrita; vazio numa etapa de leitura.
    public IReadOnlyList<WorktreeDaExecucaoDto> Worktrees { get; private set; } = [];

    // Reserva atômica sob posse (todas ou nenhuma). Capacidade esgotada: CapacidadeDeWorktreesIndisponivelException.
    internal async Task<IReadOnlyList<WorktreeDaExecucaoDto>> ReservarWorktreesAsync(
        IReadOnlyList<WorktreePlanejada> planejadas, string maquina, CancellationToken cancellationToken)
    {
        await using var escopo = escopos.CreateAsyncScope();
        return await escopo.ServiceProvider.GetRequiredService<IReservasDeWorktreeAppService>()
            .ReservarAsync(posse, Alvo.IdEtapa, planejadas, maquina, cancellationToken);
    }

    internal async Task MarcarWorktreesProntasAsync(CancellationToken cancellationToken)
    {
        await using var escopo = escopos.CreateAsyncScope();
        Worktrees = await escopo.ServiceProvider.GetRequiredService<IReservasDeWorktreeAppService>()
            .MarcarProntasAsync(posse, Alvo.IdExecucao, cancellationToken);
    }

    internal async Task FalharWorktreesAsync(string alias, string motivo, CancellationToken cancellationToken)
    {
        await using var escopo = escopos.CreateAsyncScope();
        await escopo.ServiceProvider.GetRequiredService<IReservasDeWorktreeAppService>()
            .FalharAsync(posse, Alvo.IdExecucao, alias, motivo, cancellationToken);
    }

    // Roteiro (#308): as reservas da execução voltam a ocupar capacidade para uma etapa de escrita posterior (correção),
    // sob a trava de capacidade. Sem capacidade, CapacidadeDeWorktreesIndisponivelException (o processador espera sem
    // contar tentativa).
    internal async Task ReadquirirWorktreesAsync(string maquina, CancellationToken cancellationToken)
    {
        await using var escopo = escopos.CreateAsyncScope();
        Worktrees = await escopo.ServiceProvider.GetRequiredService<IReservasDeWorktreeAppService>()
            .ReadquirirAsync(posse, Alvo.IdEtapa, maquina, cancellationToken);
    }

    // Roteiro (#308): a revisão lê as worktrees prontas da execução, sem reservar nada.
    internal void UsarWorktrees(IReadOnlyList<WorktreeDaExecucaoDto> worktrees) => Worktrees = worktrees;

    // Roteiro (#308): o fim do passo e a transição seguinte numa transação, e o fim do trabalho.
    internal async Task<AvancoDoRoteiroDto> ConcluirPassoAsync(ConclusaoDoPassoDto conclusao,
        CancellationToken cancellationToken)
    {
        if (Concluida) throw new InvalidOperationException("Etapa já concluída.");
        await pararHeartbeat();
        await using var escopo = escopos.CreateAsyncScope();
        var avanco = await escopo.ServiceProvider.GetRequiredService<IRoteirosAppService>()
            .ConcluirPassoAsync(posse, Alvo.IdEtapa, conclusao, cancellationToken);
        EstadoFinal = avanco.EstadoDaEtapa;
        Concluida = true;
        return avanco;
    }

    // Roteiro (#308): a execução passa a aguardar um objetivo sanitizado e o trabalho termina, sem iniciar a etapa.
    internal async Task SolicitarObjetivoAsync(CancellationToken cancellationToken)
    {
        if (Concluida) throw new InvalidOperationException("Etapa já concluída.");
        await pararHeartbeat();
        await using var escopo = escopos.CreateAsyncScope();
        await escopo.ServiceProvider.GetRequiredService<IRoteirosAppService>()
            .SolicitarObjetivoAsync(posse, Alvo.IdEtapa, cancellationToken);
        Concluida = true;
    }

    // Delegação (#309): delegação em andamento, etapa e subordinada em execução, antes do processo. Devolve o limite da
    // duração gravado. Prazo excedido: PrazoDaDelegacaoExcedidoException, nada gravado.
    internal async Task<DateTimeOffset> IniciarDelegacaoAsync(CancellationToken cancellationToken)
    {
        await using var escopo = escopos.CreateAsyncScope();
        var limite = await escopo.ServiceProvider.GetRequiredService<IDelegacoesAppService>()
            .IniciarAsync(posse, Alvo.IdEtapa, cancellationToken);
        Iniciada = true;
        return limite;
    }

    internal async Task RegistrarProgressoAsync(MarcoDaDelegacao marco, CancellationToken cancellationToken)
    {
        await using var escopo = escopos.CreateAsyncScope();
        await escopo.ServiceProvider.GetRequiredService<IDelegacoesAppService>()
            .RegistrarProgressoAsync(posse, Alvo.IdEtapa, marco, cancellationToken);
    }

    // Delegação (#309): resultado gravado com a subordinada e a retomada da origem, e o fim do trabalho.
    internal Task<DesfechoDaDelegacaoDto> ConcluirDelegacaoAsync(EntregaDaDelegacaoDto entrega,
        CancellationToken cancellationToken) =>
        EncerrarDelegacaoAsync(servico => servico.ConcluirAsync(posse, Alvo.IdEtapa, entrega, cancellationToken));

    // Delegação (#309): falha (Falhou) ou prazo (Expirada), com o fim do trabalho.
    internal Task<DesfechoDaDelegacaoDto> EncerrarDelegacaoAsync(string motivo, bool expirada,
        CancellationToken cancellationToken) =>
        EncerrarDelegacaoAsync(servico => expirada
            ? servico.ExpirarAsync(posse, Alvo.IdEtapa, motivo, cancellationToken)
            : servico.FalharAsync(posse, Alvo.IdEtapa, motivo, cancellationToken));

    private async Task<DesfechoDaDelegacaoDto> EncerrarDelegacaoAsync(
        Func<IDelegacoesAppService, Task<DesfechoDaDelegacaoDto>> gravacao)
    {
        if (Concluida) throw new InvalidOperationException("Etapa já concluída.");
        await pararHeartbeat();
        await using var escopo = escopos.CreateAsyncScope();
        var desfecho = await gravacao(escopo.ServiceProvider.GetRequiredService<IDelegacoesAppService>());
        Concluida = true;
        return desfecho;
    }

    // Coordenação (#310): a proposta do coordenador, validada e gravada sob posse. Correção pedida mantém o trabalho
    // (o coordenador corrige na mesma sessão); gate aberto ou execução encerrada terminam o trabalho.
    internal async Task<RegistroDaPropostaDto> RegistrarPropostaDoPlanoAsync(string? proposta,
        CancellationToken cancellationToken)
    {
        if (Concluida) throw new InvalidOperationException("Etapa já concluída.");
        await using var escopo = escopos.CreateAsyncScope();
        var registro = await escopo.ServiceProvider.GetRequiredService<ICoordenacoesAppService>()
            .RegistrarPropostaAsync(posse, Alvo.IdEtapa, proposta, cancellationToken);
        if (registro.Desfecho != DesfechoDaProposta.CorrecaoPedida)
        {
            await pararHeartbeat();
            Concluida = true;
        }
        return registro;
    }

    // Coordenação (#310): o planejamento terminou sem proposta (sessão, turno, integridade); etapa e execução falham.
    internal async Task<RegistroDaPropostaDto> FalharPlanejamentoAsync(string motivo, CancellationToken cancellationToken)
    {
        if (Concluida) throw new InvalidOperationException("Etapa já concluída.");
        await pararHeartbeat();
        await using var escopo = escopos.CreateAsyncScope();
        var registro = await escopo.ServiceProvider.GetRequiredService<ICoordenacoesAppService>()
            .FalharPlanejamentoAsync(posse, Alvo.IdEtapa, motivo, cancellationToken);
        Concluida = true;
        return registro;
    }

    // Coordenação (#310): a etapa mecânica de avanço (encerrar unidades, despachar a próxima, concluir ou replanejar) e
    // o fim do trabalho.
    internal async Task<AvancoDaCoordenacaoDto> AvancarCoordenacaoAsync(CancellationToken cancellationToken)
    {
        if (Concluida) throw new InvalidOperationException("Etapa já concluída.");
        await pararHeartbeat();
        await using var escopo = escopos.CreateAsyncScope();
        var avanco = await escopo.ServiceProvider.GetRequiredService<ICoordenacoesAppService>()
            .AvancarAsync(posse, Alvo.IdEtapa, cancellationToken);
        Concluida = true;
        return avanco;
    }

    // Grava o resultado e encerra o trabalho numa transação, antes de qualquer efeito seguinte (entrega da resposta).
    // O heartbeat para antes: renovar a linha travada pela gravação só a atrasaria.
    public async Task ConcluirAsync(ResultadoDaEtapaDto resultado, CancellationToken cancellationToken)
    {
        if (Concluida) throw new InvalidOperationException("Etapa já concluída.");
        await pararHeartbeat();
        await using var escopo = escopos.CreateAsyncScope();
        EstadoFinal = await escopo.ServiceProvider.GetRequiredService<IFilaDeExecucoesAppService>()
            .ConcluirEtapaAsync(posse, Alvo.IdEtapa, resultado, cancellationToken);
        Concluida = true;
    }
}
