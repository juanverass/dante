namespace Dante.Application.EspacosDeConhecimento;

// Entrada e saída do CRUD (AD-37). Na criação, Id e Arquivado são ignorados: o domínio gera a identidade e todo espaço
// nasce ativo. Na atualização, só Nome e Descricao são aplicados; proprietário e estado não mudam por este contrato.
public sealed record EspacoDeConhecimentoDto
{
    public Guid Id { get; init; }
    public Guid? IdTenant { get; init; } = Guid.Parse("da17e000-0000-0000-0000-000000000001");
    public Guid IdUsuario { get; init; }
    public string Nome { get; init; } = string.Empty;
    public string? Descricao { get; init; }
    public bool Arquivado { get; init; }
}
