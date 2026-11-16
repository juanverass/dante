namespace Dante.Application.Execucoes;

// Ledger de execuções (AD-66): registra no PostgreSQL os jobs e as sessões que continuam vivendo em memória
// (JobRegistry, SessionRegistry) e responde consultas. Cada operação é uma transação; conflito de xmin ou de chave
// única vira ConflitoDeConcorrenciaException, transição inválida vira TransicaoInvalidaException, e registro
// inexistente vira InvalidOperationException. Nada aqui inicia processo de agente.
public interface ILedgerDeExecucoesAppService
{
    Task<long> ReservarNumeroDeExecucaoAsync(CancellationToken cancellationToken = default);
    Task<long> ReservarNumeroDeSessaoAsync(CancellationToken cancellationToken = default);

    Task RegistrarJobCriadoAsync(JobRegistradoDto job, CancellationToken cancellationToken = default);
    Task RegistrarJobIniciadoAsync(long numero, DateTimeOffset iniciadoEm, CancellationToken cancellationToken = default);
    Task RegistrarJobEncerradoAsync(JobEncerradoDto fim, CancellationToken cancellationToken = default);

    Task RegistrarSessaoCriadaAsync(SessaoRegistradaDto sessao, CancellationToken cancellationToken = default);
    Task RegistrarSessaoIniciadaAsync(long numero, string? idUpstream, string? modeloInformado, DateTimeOffset iniciadaEm,
        CancellationToken cancellationToken = default);
    Task RegistrarSessaoEncerradaAsync(SessaoEncerradaDto fim, CancellationToken cancellationToken = default);
    Task RegistrarEventoDaSessaoAsync(EventoDeSessaoDto evento, CancellationToken cancellationToken = default);

    Task<ExecucaoDto?> ObterExecucaoAsync(long numero, CancellationToken cancellationToken = default);
    Task<SessaoDeAgenteDto?> ObterSessaoAsync(long numero, CancellationToken cancellationToken = default);
}
