namespace Dante.Application.IntegracaoDeCi;

// Consulta sob demanda do andamento do CI de um PR (#238), neutra de interface: resolve o repositório, lê o head SHA
// atual do PR e consolida o check desse SHA. Somente leitura; não acompanha periodicamente.
public interface IAcompanhamentoDeCiAppService
{
    Task<SituacaoDoCiDoPullRequestDto> ConsultarPullRequestAsync(PedidoDeCiDoPullRequestDto pedido,
        CancellationToken cancellationToken = default);
}
