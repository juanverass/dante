using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Worker.Brain;
using Dante.Worker.Sessions;

namespace Dante.Worker.Coordenacao;

// Lado do Worker do dante_coordenacao (#308, decisão E): um canal por sessão, criado só para a sessão de revisão
// confinada de um Roteiro, com escopo fixado pelo orquestrador (execução, etapa, rodada e hash do diff). Autenticação:
// pipe local de nome aleatório, só do mesmo usuário (CurrentUserOnly), uma conexão (a primeira) e um token aleatório
// da sessão que o processo do servidor apresenta antes de qualquer chamada; sem o token, o canal fecha sem atender.
// Nenhuma sessão comum, Tarefa ou etapa de escrita recebe este servidor. Cada chamada vira um caso de uso confiável da
// Application num escopo próprio; o agente nunca informa execução, etapa nem hash.
public sealed class FerramentasDeCoordenacao(IServiceScopeFactory escopos, ILogger<FerramentasDeCoordenacao> logger,
    (string Command, IReadOnlyList<string> Prefix)? comando = null) : IProvedorDeFerramentas, IDisposable
{
    public const int MaximoDeChamadas = 50;

    private sealed class Canal(EscopoDaRevisaoDto escopo, string token, NamedPipeServerStream pipe)
    {
        public EscopoDaRevisaoDto Escopo { get; } = escopo;
        public string Token { get; } = token;
        public NamedPipeServerStream Pipe { get; } = pipe;
        public CancellationTokenSource Cancelamento { get; } = new();
    }

    private readonly ConcurrentDictionary<string, Canal> canais = new();
    private bool descartado;

    public Task<IReadOnlyList<AgentToolServer>> ForAsync(long ownerUserId, AgentKind agent,
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentToolServer>>([]);

    public Task<IReadOnlyList<AgentToolServer>> ForSessionAsync(string sessionId, SessionStartRequest request,
        CancellationToken ct = default)
    {
        // Só a sessão reservada de revisão confinada, com o escopo do orquestrador.
        if (descartado || request.Observer is null ||
            request.RegisteredAgent is not { LeituraConfinada: true, EscopoDaRevisao: { } escopo })
            return Task.FromResult<IReadOnlyList<AgentToolServer>>([]);
        var nome = ServidorMcpDeCoordenacao.PrefixoDoPipe + Guid.NewGuid().ToString("N");
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var pipe = new NamedPipeServerStream(nome, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        var canal = new Canal(escopo, token, pipe);
        if (!canais.TryAdd(sessionId, canal))
        {
            pipe.Dispose();
            canal.Cancelamento.Dispose();
            return Task.FromResult<IReadOnlyList<AgentToolServer>>([]);
        }
        _ = AtenderAsync(sessionId, canal);
        var executavel = Environment.ProcessPath ?? throw new InvalidOperationException();
        string[] prefixo = Path.GetFileNameWithoutExtension(executavel).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [Assembly.GetEntryAssembly()!.Location] : [];
        if (comando is { } customizado) (executavel, prefixo) = (customizado.Command, customizado.Prefix.ToArray());
        return Task.FromResult<IReadOnlyList<AgentToolServer>>(
        [
            new(ServidorMcpDeCoordenacao.Nome, executavel, [.. prefixo, ServidorMcpDeCoordenacao.Argumento, "--pipe", nome],
                new Dictionary<string, string> { [ServidorMcpDeCoordenacao.VariavelDoToken] = token },
                ServidorMcpDeCoordenacao.FerramentasDeLeitura)
        ]);
    }

    public void BeginTurn(string sessionId, AgentInput input) { }

    public void EndSession(string sessionId)
    {
        if (!canais.TryRemove(sessionId, out var canal)) return;
        canal.Cancelamento.Cancel();
        canal.Pipe.Dispose();
    }

    public void Dispose()
    {
        descartado = true;
        foreach (var sessao in canais.Keys.ToArray()) EndSession(sessao);
    }

    private async Task AtenderAsync(string sessao, Canal canal)
    {
        var ct = canal.Cancelamento.Token;
        try
        {
            // Sem conexão em 2 minutos, o canal expira em vez de esperar um consumidor que não vem.
            using (var conectar = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                conectar.CancelAfter(TimeSpan.FromMinutes(2));
                await canal.Pipe.WaitForConnectionAsync(conectar.Token);
            }
            using var leitor = new StreamReader(canal.Pipe, Encoding.UTF8, false, 4096, true);
            using var escritor = new StreamWriter(canal.Pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            if (!await AutenticarAsync(leitor, canal, ct))
            {
                logger.LogWarning("Canal do dante_coordenacao recusado: token da sessão ausente ou inválido.");
                return;
            }
            var chamadas = 0;
            while (await ServidorMcpDoBrain.LerOuDescartarLinhaAsync(leitor, ServidorMcpDeCoordenacao.MaximoEntrada, ct)
                   is { } lida)
            {
                JsonObject resposta;
                try
                {
                    if (lida.Excedida) throw new ArgumentException("Entrada acima do limite.");
                    if (++chamadas > MaximoDeChamadas) throw new InvalidOperationException("Limite de chamadas da sessão atingido.");
                    using var documento = JsonDocument.Parse(lida.Texto!);
                    var nome = documento.RootElement.GetProperty("nome").GetString()!;
                    var argumentos = documento.RootElement.GetProperty("argumentos");
                    ServidorMcpDeCoordenacao.Validar(nome, argumentos);
                    resposta = await ChamarAsync(sessao, canal.Escopo, nome, argumentos, ct);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException
                                                      or KeyNotFoundException)
                {
                    // Recusas do caso de uso são mensagens para o agente; forma inválida também.
                    resposta = ServidorMcpDeCoordenacao.Erro(exception is ArgumentException or InvalidOperationException
                        ? exception.Message : "Chamada inválida para o dante_coordenacao.");
                }
                catch (Exception exception) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("Chamada do dante_coordenacao falhou ({ErrorType}).", exception.GetType().Name);
                    resposta = ServidorMcpDeCoordenacao.Erro("dante_coordenacao indisponível; tente de novo.");
                }
                await escritor.WriteLineAsync(resposta.ToJsonString().AsMemory(), ct);
                if (lida.Abortada) return;
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            EndSession(sessao);
        }
    }

    private static async Task<bool> AutenticarAsync(StreamReader leitor, Canal canal, CancellationToken ct)
    {
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        prazo.CancelAfter(TimeSpan.FromSeconds(10));
        string? linha;
        try { linha = await ServidorMcpDoBrain.LerLinhaAsync(leitor, 256, prazo.Token); }
        catch (Exception exception) when (exception is ArgumentException or OperationCanceledException) { return false; }
        if (linha is null) return false;
        try
        {
            using var documento = JsonDocument.Parse(linha);
            var token = documento.RootElement.GetProperty("token").GetString() ?? "";
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(canal.Token));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<JsonObject> ChamarAsync(string sessao, EscopoDaRevisaoDto escopo, string nome, JsonElement argumentos,
        CancellationToken ct)
    {
        await using var escopoDeServicos = escopos.CreateAsyncScope();
        var roteiros = escopoDeServicos.ServiceProvider.GetRequiredService<IRoteirosAppService>();
        if (nome == ServidorMcpDeCoordenacao.EstadoDaExecucao)
        {
            var estado = await roteiros.ObterEstadoDaRevisaoAsync(escopo, ct);
            return ServidorMcpDeCoordenacao.Resultado(new
            {
                execucao = estado.Codigo, grupo = estado.Grupo, rodada = estado.Rodada,
                rodadas_maximas = estado.RodadasDeRevisaoMaximas, hash_do_diff = estado.HashDoArtefato,
                instrucao_humana = estado.InstrucaoHumana,
                vereditos_anteriores = estado.VereditosAnteriores.Select(v => new
                {
                    rodada = v.Rodada, veredito = Nome(v.Veredito), resumo = v.Resumo, itens = v.Itens
                })
            });
        }
        var veredito = argumentos.GetProperty("veredito").GetString() switch
        {
            "aprovado" => VereditoDeRevisao.Aprovado,
            "requer_correcoes" => VereditoDeRevisao.RequerCorrecoes,
            "bloqueado" => VereditoDeRevisao.Bloqueado,
            _ => throw new ArgumentException("Veredito inválido: use aprovado, requer_correcoes ou bloqueado.")
        };
        var itens = argumentos.TryGetProperty("itens", out var lista)
            ? lista.ValueKind == JsonValueKind.Array
                ? lista.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
                    ? item.GetString()! : throw new ArgumentException("Cada item é um texto.")).ToArray()
                : throw new ArgumentException("itens é uma lista de textos.")
            : [];
        var resumo = argumentos.GetProperty("resumo").ValueKind == JsonValueKind.String
            ? argumentos.GetProperty("resumo").GetString()! : throw new ArgumentException("resumo é um texto.");
        var registro = await roteiros.RegistrarVereditoAsync(escopo, new VereditoInformadoDto(veredito, resumo, itens), sessao, ct);
        return ServidorMcpDeCoordenacao.Resultado(new
        {
            registrado = true, veredito = Nome(registro.Veredito), ja_registrado = registro.JaRegistrado,
            mensagem = "Veredito registrado. Encerre a revisão com uma resposta curta."
        });
    }

    private static string Nome(VereditoDeRevisao veredito) => veredito switch
    {
        VereditoDeRevisao.Aprovado => "aprovado",
        VereditoDeRevisao.RequerCorrecoes => "requer_correcoes",
        _ => "bloqueado"
    };
}
