namespace Dante.Application.Planilhas;

// Estado da conta conectada, sem token: Conta é o e-mail informado pelo provedor, quando disponível.
public sealed record EstadoDaConexaoDto
{
    public string Provedor { get; init; } = string.Empty;
    public bool Configurada { get; init; }
    public bool Conectada { get; init; }
    public string? Conta { get; init; }
    public DateTimeOffset? ConectadaEm { get; init; }
    public string? Problema { get; init; }
}
