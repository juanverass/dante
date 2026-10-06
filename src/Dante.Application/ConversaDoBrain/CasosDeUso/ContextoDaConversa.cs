using Dante.Application.Conhecimentos;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;

namespace Dante.Application.ConversaDoBrain;

internal sealed class ContextoDaConversa(AcessoAoBrain acesso, PedidoDeConversaDto pedido,
    ChaveDeConversaDto chave, IEstadoDeConversaDoBrain estados)
{
    public AcessoAoBrain Acesso { get; } = acesso;
    public PedidoDeConversaDto Pedido { get; } = pedido;
    public EstadoDeConversaDto Estado { get; private set; } = estados.Obter(chave);
    public EstadoDeConversaDto Guardar(EstadoDeConversaDto novo) => Estado = estados.Salvar(chave, novo);
    public bool ConsumirPendente() => estados.ConsumirPendente(chave, Estado.Versao);
    public AlteracaoPendenteDto Pendente(string acao, AlvoDeConversaDto? alvo = null, string? texto = null,
        AlvoDeConversaDto? segundo = null) => new(acao, alvo, segundo, texto, DateTimeOffset.UtcNow.AddMinutes(5));
    public AlvoDeConversaDto? Escolher(int? numero) => numero is { } n
        ? n >= 1 && n <= Estado.Resultados.Count ? Estado.Resultados[n - 1] : null
        : Estado.Resultados.Count == 1 ? Estado.Resultados[0] : null;
    public ProvenienciaDto Prova(string trecho) => new() { IdResponsavel = Acesso.IdUsuario,
        Origem = "conversa explícita", ReferenciaDaFonte = Pedido.ReferenciaDaMensagem, TrechoDaFonte = trecho };
    public bool Permitida(Sensibilidade classe) => classe < Sensibilidade.Confidencial ||
        classe == Sensibilidade.Confidencial && Acesso.PermitirConfidencial || classe == Sensibilidade.Secreto && Acesso.PermitirSecreto;
}
