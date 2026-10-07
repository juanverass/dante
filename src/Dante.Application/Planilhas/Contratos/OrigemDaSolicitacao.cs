namespace Dante.Application.Planilhas;

// Quem pediu a operação, para a auditoria: o canal ("telegram", "mcp"), o usuário do canal e o agente responsável.
public sealed record OrigemDaSolicitacao(string Canal, string? Usuario = null, string? Agente = null)
{
    public override string ToString() => string.Join(':', new[] { Canal, Usuario }.Where(parte => parte is not null));
}
