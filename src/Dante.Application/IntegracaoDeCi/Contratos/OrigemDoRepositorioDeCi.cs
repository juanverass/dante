namespace Dante.Application.IntegracaoDeCi;

// De onde veio o repositório consultado: o alias dito no pedido, o repositório da sessão ativa ou o repositório ativo
// do usuário. Nunca é deduzido do texto além do alias explícito.
public enum OrigemDoRepositorioDeCi
{
    Explicito,
    Sessao,
    Ativo
}
