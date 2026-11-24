namespace Dante.Application.IntegracaoDeCi;

// Pedido de andamento do CI de um PR. AliasExplicito vence AliasDaSessao (repositório principal da sessão ativa, quando
// ela trabalha num repositório), que vence o repositório ativo do usuário. Repositórios vinculados não entram: cada
// alias só é consultado pela conexão que o libera.
public sealed record PedidoDeCiDoPullRequestDto(long IdUsuario, int NumeroDoPr, string? AliasExplicito = null,
    string? AliasDaSessao = null);
