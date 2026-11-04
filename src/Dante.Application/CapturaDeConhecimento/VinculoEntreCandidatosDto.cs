using Dante.Domain.CapturaDeConhecimento;
namespace Dante.Application.CapturaDeConhecimento;

// Intenção de ResolvidoPor entre candidatos (#259); IdRelacao só existe depois dos dois lados consolidados.
public sealed record VinculoEntreCandidatosDto(Guid Id, Guid IdCandidatoIncidente, Guid IdCandidatoSolucao, EstadoDoVinculo Estado,
    Guid? IdConhecimentoIncidente, Guid? IdConhecimentoSolucao, Guid? IdRelacao, string? MotivoDoEncerramento, DateTimeOffset CriadoEm)
{
    internal static VinculoEntreCandidatosDto De(VinculoEntreCandidatos v) => new(v.Id, v.IdCandidatoIncidente, v.IdCandidatoSolucao, v.Estado,
        v.IdConhecimentoIncidente, v.IdConhecimentoSolucao, v.IdRelacao, v.MotivoDoEncerramento, v.CriadoEm);
}
