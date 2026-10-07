using Dante.Application.Conhecimentos;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
namespace Dante.Application.CapturaDeConhecimento;

public sealed record CapturaDeConhecimentoDto
{
    public Guid IdEspacoDeConhecimento { get; init; }
    public Guid? IdProjeto { get; init; }
    public TipoDeConhecimento Tipo { get; init; }
    public string? Titulo { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string Conteudo { get; init; } = "";
    public Sensibilidade Sensibilidade { get; init; } = Sensibilidade.Pessoal;
    public NaturezaDoConteudo Natureza { get; init; }
    public ModoDeCaptura Modo { get; init; }
    public string Justificativa { get; init; } = "";
    public ProvenienciaDto Proveniencia { get; init; } = new();
    public Guid? IdIncidente { get; init; }
    public Guid? IdSolucao { get; init; }
}
