namespace Dante.Application.Planilhas;

// Perfil local de uma planilha autorizada: alias, ID do provedor e descrições opcionais. Leitura automática só ocorre
// em planilha cadastrada.
public sealed record PlanilhaCadastradaDto
{
    public string Alias { get; init; } = string.Empty;
    public string IdDaPlanilha { get; init; } = string.Empty;
    public string? Titulo { get; init; }
    public string? Descricao { get; init; }
    public IReadOnlyList<RegiaoConhecidaDto> Regioes { get; init; } = [];
    public DateTimeOffset CadastradaEm { get; init; }
}
