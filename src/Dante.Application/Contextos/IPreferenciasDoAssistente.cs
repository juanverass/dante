using Dante.Domain.Preferencias;

namespace Dante.Application.Contextos;

// Porta de leitura das preferências do assistente (AD-13, AD-14) usadas na resolução de agente e contexto.
public interface IPreferenciasDoAssistente
{
    AssistantSettings Atual { get; }

    string? ObterRepositorioAtivo(long idUsuario);
}
