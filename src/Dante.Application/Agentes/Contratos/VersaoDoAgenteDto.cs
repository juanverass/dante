using Dante.Domain.Agentes;

namespace Dante.Application.Agentes;

// Versão congelada de um agente registrado (AD-68). Configuracao é a configuração normalizada que o host usa para
// conferir perfil, repositórios e ferramentas; o hash identifica a versão para reprodução.
public sealed record VersaoDoAgenteDto
{
    public Guid Id { get; init; }
    public Guid IdAgente { get; init; }
    public int Numero { get; init; }
    public ConfiguracaoDoAgente Configuracao { get; init; } = null!;
    public string HashDaConfiguracao { get; init; } = "";
    public long IdDoAutor { get; init; }
    public DateTimeOffset CriadaEm { get; init; }
}
