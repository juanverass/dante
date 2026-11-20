using Dante.Application.Execucoes;

namespace Dante.Worker.Execucoes;

// Entrega do coordenador pelo dante_coordenacao (#310, PR 3): o plano (propor_plano) na etapa de planejamento ou a
// síntese (entregar_sintese) na etapa de síntese. A chamada valida e grava na hora, sob a posse do trabalho da etapa e
// só pela sessão vinculada a ela, pelos mesmos casos de uso da PR 2: nenhuma regra nova aqui. Recusada, as recusas
// voltam ao coordenador na resposta da ferramenta e a correção única é contada no ledger. Entrega pela ferramenta e
// encerramento pelo executor (turno sem entrega, falha, prazo) são serializados aqui: o primeiro grava, o outro encontra
// a etapa concluída.
public sealed class EntregaDoCoordenador(ExecucaoSobPosse controle, PassoDaCoordenacao passo)
{
    private readonly SemaphoreSlim trava = new(1, 1);

    public ExecucaoSobPosse Controle => controle;

    public PassoDaCoordenacao Passo { get; } = passo is PassoDaCoordenacao.Planejamento or PassoDaCoordenacao.Sintese
        ? passo : throw new ArgumentOutOfRangeException(nameof(passo), "Só o planejamento e a síntese têm entrega.");

    // Os registros mais recentes (nulos enquanto nada foi registrado).
    public RegistroDaPropostaDto? Proposta { get; private set; }
    public RegistroDaSinteseDto? Sintese { get; private set; }

    // Conteúdo nulo: o turno terminou sem entrega (conta como recusa). Etapa já encerrada ou sessão não vinculada:
    // InvalidOperationException, nada gravado.
    public async Task EntregarAsync(string? conteudo, CancellationToken cancellationToken)
    {
        await trava.WaitAsync(cancellationToken);
        try
        {
            if (controle.Concluida) throw new InvalidOperationException("Esta etapa já terminou; nada mais é aceito.");
            if (controle.NumeroDaSessao is null)
                throw new InvalidOperationException("A sessão do coordenador não está vinculada à etapa.");
            // Uma vez iniciada, a gravação não é abandonada pelo fim do turno ou da sessão.
            if (Passo == PassoDaCoordenacao.Planejamento)
                Proposta = await controle.RegistrarPropostaDoPlanoAsync(conteudo, CancellationToken.None);
            else Sintese = await controle.RegistrarSinteseAsync(conteudo, CancellationToken.None);
        }
        finally
        {
            trava.Release();
        }
    }

    // Encerramento pelo executor (falha do processo). Falso quando a etapa já foi concluída por uma entrega.
    public async Task<bool> EncerrarAsync(string motivo)
    {
        await trava.WaitAsync(CancellationToken.None);
        try
        {
            if (controle.Concluida) return false;
            if (Passo == PassoDaCoordenacao.Planejamento)
                Proposta = await controle.FalharPlanejamentoAsync(motivo, CancellationToken.None);
            else Sintese = await controle.FalharSinteseAsync(motivo, CancellationToken.None);
            return true;
        }
        finally
        {
            trava.Release();
        }
    }
}
