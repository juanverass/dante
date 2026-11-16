namespace Dante.Application.Execucoes;

// Recuperação na inicialização (AD-67), antes de o processador da fila reivindicar qualquer trabalho:
// 1. unidades dante-agent-* registradas no ledger por sessões de um boot anterior são paradas; as desconhecidas do
//    ledger (ou do boot atual) são só relatadas, nunca paradas;
// 2. sessões não terminais de um boot anterior viram Interrompida (processo e conversa upstream não são retomados);
// 3. trabalhos reivindicados por um boot anterior deste host voltam à fila; ao reivindicá-los, o processador aplica
//    a política de retomada da etapa interrompida.
public interface IRecuperacaoDeExecucoesAppService
{
    Task<RelatorioDeRecuperacaoDto> RecuperarAsync(IdentidadeDoProcessador atual,
        CancellationToken cancellationToken = default);
}
