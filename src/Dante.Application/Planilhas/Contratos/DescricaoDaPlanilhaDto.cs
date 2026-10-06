namespace Dante.Application.Planilhas;

public sealed record DescricaoDaPlanilhaDto
{
    public PlanilhaCadastradaDto Cadastro { get; init; } = new();
    public PlanilhaDto Planilha { get; init; } = new();
}
