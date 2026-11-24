namespace Dante.Application.IntegracaoDeCi;

// Porta do provedor de CI (AD-76): somente leitura. O CI externo é a fonte de verdade e roda sem o D.A.N.T.E.; esta
// porta não dispara, reexecuta, cancela nem comenta nada. Qualquer efeito colateral futuro entra como ação protegida
// do catálogo (AD-72), nunca como método aqui. Cada adapter da Infrastructure resolve a credencial da conexão do
// repositório na hora da chamada, obedece à PoliticaDeConsultaDeCi e devolve só tipos neutros; falhas chegam como
// FalhaNaConsultaDeCiException.
public interface IProvedorDeCi
{
    // Execuções do repositório para o SHA consultado, com seus jobs, paginadas até o limite da política. Pode devolver
    // tentativas anteriores e execuções de outros SHAs: separar o que vale é da ConsolidacaoDoCheckDeCi.
    Task<ResultadoDaConsultaDeCi> ConsultarAsync(ConsultaDeCi consulta, CancellationToken cancellationToken = default);

    // Uma tentativa específica, para acompanhar a execução já correlacionada sem refazer a consulta inteira.
    Task<ExecucaoDeCiDto> ObterExecucaoAsync(IdentidadeDaExecucaoDeCi execucao,
        CancellationToken cancellationToken = default);

    // Log do job, já sanitizado e limitado a limiteEmBytes. Log ausente, expirado ou negado volta como disponibilidade,
    // não como exceção, para não esconder o estado do job.
    Task<LogDeJobDeCiDto> ObterLogDoJobAsync(IdentidadeDoJobDeCi job, int limiteEmBytes,
        CancellationToken cancellationToken = default);
}
