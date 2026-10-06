namespace Dante.Application.ConstrucaoDeContexto;

internal static class ConstrucaoDeContextoValidator
{
    internal static void ValidarPedido(PedidoDeContextoDto pedido)
    {
        if (string.IsNullOrWhiteSpace(pedido.Mensagem) || pedido.Mensagem.Length > 2000 || pedido.OrcamentoDeTokens is < 64 or > 32000 ||
            pedido.LimiteDeItens is < 1 or > 100 || pedido.LimiteDeCandidatos is < 1 or > 100 || pedido.ProfundidadeDeRelacoes is < 0 or > 3 ||
            pedido.FragmentosJaPresentes.Count > 20 || pedido.FragmentosJaPresentes.Any(x => x.Length > 8000) || pedido.FragmentosJaPresentes.Sum(x => x.Length) > 32000 ||
            pedido.JaInjetados.Count > 500)
            throw new ArgumentException("Limites do contexto inválidos.");
    }
}
