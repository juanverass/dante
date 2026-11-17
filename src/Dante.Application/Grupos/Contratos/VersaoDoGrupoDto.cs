using Dante.Domain.Grupos;

namespace Dante.Application.Grupos;

// Versão congelada de um grupo. Configuracao é nula só se o conteúdo gravado não passa mais na validação (adulterado).
public sealed record VersaoDoGrupoDto
{
    public Guid Id { get; init; }
    public Guid IdGrupo { get; init; }
    public int Numero { get; init; }
    public ConfiguracaoDoGrupo? Configuracao { get; init; }
    public IReadOnlyList<ParticipanteDto> Participantes { get; init; } = [];
    public string HashDaConfiguracao { get; init; } = "";
    public bool Integra { get; init; }
    public long IdDoAutor { get; init; }
    public DateTimeOffset CriadaEm { get; init; }
}
