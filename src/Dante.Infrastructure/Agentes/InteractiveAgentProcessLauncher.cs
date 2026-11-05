using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Dante.Infrastructure.Agentes;

// Legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
public sealed class AgentProcessStartException(string message, Exception? innerException = null)
    : Exception(message, innerException);

// Starts long-lived agent processes for interactive sessions; one-shot jobs keep using AgentProcessExecutor.
public sealed class InteractiveAgentProcessLauncher(IAgentExecutableResolver executableResolver, OpcoesContencaoAgentes? opcoes = null,
    ILogger<InteractiveAgentProcessLauncher>? logger = null)
    : IInteractiveAgentProcessLauncher
{
    // The structured protocols are UTF-8 JSONL; no BOM may precede the first line written to stdin.
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public async Task<InteractiveAgentProcess> StartAsync(
        AgentProcessRequest request,
        Func<string, string>? redactOutput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Arguments);
        cancellationToken.ThrowIfCancellationRequested();

        if (!AgentProcessStartInfo.TryCreate(request, executableResolver, out var startInfo, out var error))
        {
            throw new AgentProcessStartException(error);
        }

        startInfo.RedirectStandardInput = true;
        startInfo.StandardInputEncoding = Utf8;
        startInfo.StandardOutputEncoding = Utf8;
        startInfo.StandardErrorEncoding = Utf8;

        var modo = (opcoes ?? OpcoesContencaoAgentes.DoAmbiente()).Modo;
        if (!Enum.IsDefined(modo)) throw new AgentProcessStartException("Modo de contenção inválido.");
        if (modo == ModoContencaoAgentes.Estrito)
        {
            ProcessoContido contido;
            try
            {
                if (OperatingSystem.IsLinux()) contido = await ContencaoSystemd.IniciarAsync(startInfo, cancellationToken);
                else if (OperatingSystem.IsWindows()) contido = ContencaoWindows.Iniciar(startInfo, cancellationToken);
                else throw new PlatformNotSupportedException("Contenção nativa indisponível nesta plataforma.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                throw new AgentProcessStartException("Contenção estrita indisponível; agente não foi liberado sem contenção.", exception);
            }
            return new InteractiveAgentProcess(contido.Processo, redactOutput, contido: contido);
        }

        logger?.LogWarning("Modo compatibilidade de processos habilitado explicitamente; descendentes entre snapshots podem sobreviver.");
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new AgentProcessStartException("Agent process could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            process.Dispose();
            throw new AgentProcessStartException($"Agent process could not be started: {exception.Message}", exception);
        }
        catch
        {
            process.Dispose();
            throw;
        }

        return new InteractiveAgentProcess(process, redactOutput);
    }
}
