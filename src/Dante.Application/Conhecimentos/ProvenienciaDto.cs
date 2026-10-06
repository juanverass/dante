
namespace Dante.Application.Conhecimentos;

public sealed record ProvenienciaDto
{
    public Guid IdResponsavel { get; init; }
    public string Origem { get; init; } = string.Empty;
    public string? ReferenciaDaFonte { get; init; }
    public string? RevisaoDaFonte { get; init; }
    public string? TrechoDaFonte { get; init; }
}
