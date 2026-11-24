namespace Dante.Application.IntegracaoDeCi;

// Resposta bruta (já neutra) do provedor a uma ConsultaDeCi. Completo é falso quando a paginação parou no limite da
// política: o check agregado nunca declara sucesso sobre um resultado incompleto.
public sealed record ResultadoDaConsultaDeCi
{
    public required bool PossuiWorkflowsConfigurados { get; init; }
    public required bool Completo { get; init; }
    public required DateTimeOffset ObtidoEm { get; init; }
    public IReadOnlyList<ExecucaoDeCiDto> Execucoes { get; init; } = [];
}
