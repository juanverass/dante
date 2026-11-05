using System.Diagnostics;

namespace Dante.Infrastructure.Agentes;

public enum ModoContencaoAgentes { Estrito, Compatibilidade }

public sealed class OpcoesContencaoAgentes
{
    public ModoContencaoAgentes Modo { get; init; } = ModoContencaoAgentes.Estrito;

    internal static OpcoesContencaoAgentes DoAmbiente() => InterpretarModo(Environment.GetEnvironmentVariable("DANTE_AGENT_CONTAINMENT_MODE"));

    internal static OpcoesContencaoAgentes InterpretarModo(string? valor) => new()
    {
        Modo = valor switch
        {
            null or "" or "strict" => ModoContencaoAgentes.Estrito,
            "compatibility" => ModoContencaoAgentes.Compatibilidade,
            _ => throw new AgentProcessStartException("DANTE_AGENT_CONTAINMENT_MODE inválido; use strict ou compatibility.")
        }
    };
}

// Apenas infraestrutura: o núcleo não conhece cgroups, systemd nem handles Windows.
internal interface IContencaoDeAgente : IDisposable
{
    Task EncerrarEConfirmarVaziaAsync(CancellationToken cancellationToken);
}

internal sealed record ProcessoContido(Process Processo, IContencaoDeAgente Contencao,
    StreamWriter Entrada, StreamReader Saida, StreamReader Erro);
