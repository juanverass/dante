using Dante.Application.Contextos;
using Dante.Domain.Agentes;

namespace Dante.Application.Execucoes;

// Job one-shot legado (AD-06) no momento em que o JobRegistry o cria. O texto do pedido não é registrado.
public sealed record JobRegistradoDto(long Numero, AgentKind Agente, string Canal, long IdDoUsuarioNoCanal,
    JobExecutionContext Contexto, string? Modelo, string? Esforco, DateTimeOffset CriadoEm);
