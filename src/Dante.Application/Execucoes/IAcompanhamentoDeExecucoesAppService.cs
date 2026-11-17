namespace Dante.Application.Execucoes;

// Consulta de execuções pelo proprietário (#305), neutra de interface: o Telegram é o primeiro adaptador; um chat
// próprio ou a WebApi consomem os mesmos contratos. Toda consulta é filtrada pelo proprietário, e uma execução de outro
// proprietário é indistinguível de uma inexistente. Os textos devolvidos (objetivo, motivo, erro) já passaram pela
// proteção de segredos e foram cortados; diretórios e saída de agente nunca são devolvidos.
public interface IAcompanhamentoDeExecucoesAppService
{
    // Recentes: todas as não terminais e as últimas terminais (mais com todas=true), da mais recente para a mais antiga.
    Task<IReadOnlyList<ResumoDaExecucaoDto>> ListarAsync(ProprietarioDaExecucao proprietario, bool todas,
        CancellationToken cancellationToken = default);

    // codigo: J… ou E…, como exibido. Nulo: inexistente, de outro proprietário ou código inválido.
    Task<DetalheDaExecucaoDto?> ObterAsync(ProprietarioDaExecucao proprietario, string codigo,
        CancellationToken cancellationToken = default);

    // Não terminais que não estão rodando, cada uma com o motivo da espera.
    Task<IReadOnlyList<ResumoDaExecucaoDto>> ListarAguardandoAsync(ProprietarioDaExecucao proprietario,
        CancellationToken cancellationToken = default);

    Task<PainelDeExecucoesDto> ResumirAsync(ProprietarioDaExecucao proprietario,
        CancellationToken cancellationToken = default);
}
