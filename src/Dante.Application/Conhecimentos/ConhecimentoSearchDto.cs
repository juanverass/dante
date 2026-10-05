using Dante.Domain.Conhecimentos;

namespace Dante.Application.Conhecimentos;

public sealed record ConhecimentoSearchDto
{
    public Guid IdEspacoDeConhecimento { get; init; }
    // Sem projeto: conhecimento de todo o espaço. SomenteSemProjeto restringe aos itens diretos do espaço.
    public Guid? IdProjeto { get; init; }
    public bool SomenteSemProjeto { get; init; }
    public TipoDeConhecimento? Tipo { get; init; }
    public StatusDoConhecimento? Status { get; init; }
    public string? Tag { get; init; }
    public bool IncluirInativosOuSubstituidos { get; init; }
    public DateTimeOffset? ValidoEm { get; init; }
    public int Limite { get; init; } = 50;
}
