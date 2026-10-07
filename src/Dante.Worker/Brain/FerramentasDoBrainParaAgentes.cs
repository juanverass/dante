using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;

namespace Dante.Worker.Brain;

public sealed class FerramentasDoBrainParaAgentes(TelegramBrain brain, OperacoesMcpDoBrain operacoes,
    RegistroDeOperacoesBrain registro, ILogger<FerramentasDoBrainParaAgentes> logger,
    (string Command, IReadOnlyList<string> Prefix)? comando = null) : IProvedorDeFerramentas, IDisposable
{
    private sealed class Canal(CancellationTokenSource cancelamento, NamedPipeServerStream pipe)
    {
        public CancellationTokenSource Cancelamento { get; } = cancelamento;
        public NamedPipeServerStream Pipe { get; } = pipe;
        public Task? Execucao { get; set; }
    }
    private readonly ConcurrentDictionary<string, Canal> canais = [];
    private bool disposed;
    public Task<IReadOnlyList<AgentToolServer>> ForAsync(long ownerUserId, AgentKind agent, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgentToolServer>>([]); // Sem conversa autenticada, falha fechado.
    public async Task<IReadOnlyList<AgentToolServer>> ForSessionAsync(string sessionId, SessionStartRequest request, CancellationToken ct = default)
    {
        if (disposed || request.BrainConversation is not { } contexto || contexto.Usuario != request.OwnerUserId) return [];
        var mensagem = contexto.ParaMensagem();
        try
        {
            using var disponibilidade = CancellationTokenSource.CreateLinkedTokenSource(ct);
            disponibilidade.CancelAfter(TimeSpan.FromSeconds(5));
            var escopo = await brain.ResolverEscopoMcpAsync(mensagem, disponibilidade.Token);
            if (escopo is null) return [];
            registro.Observar(mensagem, sessionId);
            var nome = "dante-brain-" + Guid.NewGuid().ToString("N");
            // Endpoint aleatório, local, same-user e exclusivo desta sessão. Sem token persistente/credenciais.
            var pipe = new NamedPipeServerStream(nome, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
            var canal = new Canal(new CancellationTokenSource(), pipe);
            if (!canais.TryAdd(sessionId, canal)) { pipe.Dispose(); canal.Cancelamento.Dispose(); return []; }
            canal.Execucao = AtenderAsync(canal, sessionId, request.Agent.ToString().ToLowerInvariant(), mensagem, escopo, registro.VersaoConversa(mensagem));
            var executavel = Environment.ProcessPath ?? throw new InvalidOperationException();
            string[] prefixo = Path.GetFileNameWithoutExtension(executavel).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? [Assembly.GetEntryAssembly()!.Location] : [];
            if (comando is { } customizado) { executavel = customizado.Command; prefixo = customizado.Prefix.ToArray(); }
            return [new(ServidorMcpDoBrain.Nome, executavel, [.. prefixo, ServidorMcpDoBrain.Argumento, "--pipe", nome],
                new Dictionary<string, string>(), ServidorMcpDoBrain.FerramentasDeLeitura)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            EndSession(sessionId);
            logger.LogWarning("Ferramentas Brain indisponíveis ({ErrorType}).", ex.GetType().Name);
            return [];
        }
    }
    private async Task AtenderAsync(Canal canal, string sessao, string agente, TelegramMessage inicial, TelegramBrain.EscopoBrainDaSessao escopo, int geracao)
    {
        var ct = canal.Cancelamento.Token;
        try
        {
            // Se a CLI nunca conectar, o endpoint expira em vez de sobreviver sem consumidor.
            using var conectar = CancellationTokenSource.CreateLinkedTokenSource(ct); conectar.CancelAfter(TimeSpan.FromMinutes(2));
            await canal.Pipe.WaitForConnectionAsync(conectar.Token);
            using var leitor = new StreamReader(canal.Pipe, Encoding.UTF8, false, 4096, true);
            using var escritor = new StreamWriter(canal.Pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            var chamadas = 0;
            while (await ServidorMcpDoBrain.LerLinhaAsync(leitor, ServidorMcpDoBrain.MaximoEntrada, ct) is { } linha)
            {
                JsonObject resposta;
                try
                {
                    if (++chamadas > 1000) throw new InvalidOperationException("Limite de chamadas da sessão atingido.");
                    using var prazo = CancellationTokenSource.CreateLinkedTokenSource(ct); prazo.CancelAfter(TimeSpan.FromSeconds(30));
                    // Resolver de novo em cada operação: troca de espaço/projeto revoga a capacidade antiga.
                    var atual = await brain.ResolverEscopoMcpAsync(inicial, prazo.Token);
                    if (atual != escopo || !registro.ConversaPermitida(sessao, inicial) || registro.VersaoConversa(inicial) != geracao)
                    {
                        await escritor.WriteLineAsync(ServidorMcpDoBrain.Resultado("Escopo/conversa mudou; inicie nova sessão.", true).ToJsonString().AsMemory(), ct);
                        return;
                    }
                    using var documento = JsonDocument.Parse(linha); var pedido = documento.RootElement;
                    var nome = pedido.GetProperty("nome").GetString()!; var argumentos = pedido.GetProperty("argumentos");
                    var resultado = await operacoes.ExecutarAsync(escopo, registro.Atual(inicial), sessao, agente, nome, argumentos, prazo.Token);
                    var texto = JsonSerializer.Serialize(resultado);
                    if (texto.Length > 45_000) resposta = ServidorMcpDoBrain.Resultado("Resposta excede o limite; reduza a página ou use consulta mais específica.", true);
                    else resposta = ServidorMcpDoBrain.Resultado(resultado);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                { resposta = ServidorMcpDoBrain.Resultado("Brain indisponível ou entrada/escopo/revisão inválidos. Refaça a consulta. Mudança de escopo exige nova sessão.", true); }
                var json = resposta.ToJsonString();
                if (json.Length > ServidorMcpDoBrain.MaximoResposta - 1000) json = ServidorMcpDoBrain.Resultado("Resposta excede o limite; reduza a consulta.", true).ToJsonString();
                await escritor.WriteLineAsync(json.AsMemory(), ct);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or ArgumentException) { }
        finally { EndSession(sessao); }
    }
    public void EndSession(string sessionId)
    {
        registro.Revogar(sessionId);
        if (!canais.TryRemove(sessionId, out var canal)) return;
        canal.Cancelamento.Cancel(); canal.Pipe.Dispose();
        // Token source é liberado depois que o pump terminar, sem bloquear shutdown/loop do driver.
        if (canal.Execucao is { } tarefa) _ = tarefa.ContinueWith(_ => canal.Cancelamento.Dispose(), TaskScheduler.Default);
        else canal.Cancelamento.Dispose();
    }
    public void Dispose()
    { disposed = true; foreach (var sessao in canais.Keys) EndSession(sessao); }
}
