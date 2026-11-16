using Dante.Application.Contextos;
using Dante.Domain.Agentes;

namespace Dante.Application.Execucoes;

// Sessão interativa no momento em que o SessionRegistry a cria, antes de o processo iniciar. O agente congelado
// (AD-68) vem do embutido materializado ou da versão registrada; sem ele, o registro segue como antes.
public sealed record SessaoRegistradaDto(long Numero, AgentKind Agente, string Canal, long IdDoDono,
    JobExecutionContext Contexto, AgentPermissionProfile Perfil, string? Modelo, string? Esforco,
    DateTimeOffset CriadaEm, string? HandleDoAgente = null, string? HashDaConfiguracaoDoAgente = null,
    Guid? IdVersaoDoAgente = null);
