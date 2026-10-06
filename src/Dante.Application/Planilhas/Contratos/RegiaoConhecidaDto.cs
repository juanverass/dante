namespace Dante.Application.Planilhas;

// Descrição opcional e genérica de uma região, anotada pelo usuário ou pelo agente para consultas recorrentes.
// É metadado local do cadastro: não altera a planilha nem impõe formato a ela.
public sealed record RegiaoConhecidaDto
{
    public string Nome { get; init; } = string.Empty;
    public string Intervalo { get; init; } = string.Empty;
    public string? Descricao { get; init; }
}
