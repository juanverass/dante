using Dante.Application.Contextos;
using Dante.Domain.Agentes;

namespace Dante.Application.Execucoes;

// Sessão interativa no momento em que o SessionRegistry a cria, antes de o processo iniciar.
public sealed record SessaoRegistradaDto(long Numero, AgentKind Agente, string Canal, long IdDoDono,
    JobExecutionContext Contexto, AgentPermissionProfile Perfil, string? Modelo, string? Esforco,
    DateTimeOffset CriadaEm);
