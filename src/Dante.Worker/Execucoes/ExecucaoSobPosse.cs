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
