using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;

namespace Dante.Worker.Execucoes;

// Resultado da delegação entregue pela ferramenta entregar_resultado do dante_coordenacao (#309, PR 3), no lugar do
// bloco JSON da última mensagem. A chamada valida o resultado (contrato e credenciais pelo servidor, evidências de
// arquivo existentes no repositório do contexto) e o grava na hora, sob a posse do trabalho da subordinada e só da
// sessão vinculada à etapa: durável antes de o turno terminar. Entrega e encerramento pelo executor (falha, prazo)
// são serializados aqui: o primeiro grava, o outro encontra a etapa concluída. Repetir o mesmo conteúdo depois de
// gravado é idempotente; outro conteúdo é recusado (o original não muda).
public sealed class EntregaPelaFerramenta(IServiceScopeFactory escopos, ExecucaoSobPosse controle,
    Guid idExecucaoSubordinada, string contrato, JobExecutionContext contexto)
{
    private readonly SemaphoreSlim trava = new(1, 1);
    private string? gravado;
    private DesfechoDaDelegacaoDto? desfecho;

    public ExecucaoSobPosse Controle => controle;

    // Desfecho gravado pela entrega (nulo enquanto nada foi entregue).
    public DesfechoDaDelegacaoDto? Desfecho => desfecho;

    // Inválido: ArgumentException com o motivo, nada gravado (o agente corrige e entrega de novo). Etapa já encerrada
    // com outro desfecho ou posse perdida: InvalidOperationException.
    public async Task<(DesfechoDaDelegacaoDto Desfecho, bool JaRegistrado)> EntregarAsync(string resultado,
        CancellationToken cancellationToken)
    {
        string? invalido;
        await using (var escopo = escopos.CreateAsyncScope())
            invalido = await escopo.ServiceProvider.GetRequiredService<IDelegacoesAppService>()
                .ValidarResultadoAsync(idExecucaoSubordinada, resultado, cancellationToken);
        if (invalido is null && ExecutorDeDelegacoesDaFila.EvidenciaInexistente(resultado, contexto) is { } ausente)
            invalido = $"a evidência {ausente} não existe no repositório.";
        if (invalido is not null) throw new ArgumentException($"Resultado não aceito: {invalido}");
        await trava.WaitAsync(cancellationToken);
        try
        {
            if (controle.Concluida)
            {
                if (gravado is not null && desfecho is not null && Canonico(gravado) == Canonico(resultado))
                    return (desfecho, true);
                throw new InvalidOperationException("A delegação já terminou; o resultado não é aceito.");
            }
            if (controle.NumeroDaSessao is not { } numero)
                throw new InvalidOperationException("A sessão desta delegação não está vinculada à etapa.");
            await controle.RegistrarProgressoAsync(MarcoDaDelegacao.ResultadoRecebido, cancellationToken);
            // Uma vez iniciada, a gravação não é abandonada pelo fim do turno ou da sessão.
            desfecho = await controle.ConcluirDelegacaoAsync(new EntregaDaDelegacaoDto(resultado, numero),
                CancellationToken.None);
            if (desfecho.Estado == EstadoDaDelegacao.Respondida) gravado = resultado;
            return (desfecho, false);
        }
        finally
        {
            trava.Release();
        }
    }

    // Encerramento pelo executor (Falhou ou Expirada). Falso quando a entrega já concluiu a etapa.
    public async Task<bool> EncerrarAsync(string motivo, bool expirada)
    {
        await trava.WaitAsync(CancellationToken.None);
        try
        {
            if (controle.Concluida) return false;
            await controle.EncerrarDelegacaoAsync(motivo, expirada, CancellationToken.None);
            return true;
        }
        finally
        {
            trava.Release();
        }
    }

    private string Canonico(string json) =>
        ContratosDeDelegacao.TentarObter(contrato, out var c) ? c.CanonizarSaida(json) : json;
}
