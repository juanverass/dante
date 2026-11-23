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
// Nenhuma sessão comum ou etapa de escrita recebe este servidor. Cada chamada vira um caso de uso confiável da
// Application num escopo próprio; o agente nunca informa execução, etapa nem hash.
// Delegação (#309, PR 3), no mesmo servidor, pipe, token e limite: a etapa de leitura (plan) de uma Tarefa cuja versão
// concede coordenacao e pode consultar alguém recebe o perfil origem (consultar_agente e estado_da_execucao, sob a
// posse da etapa e só da sessão vinculada a ela); a sessão da subordinada recebe o perfil delegacao (entregar o resultado pelo contrato). O
// perfil é do canal, fixado aqui; o --perfil do processo só escolhe o que a CLI lista.
// Coordenação (#310, PR 3): a sessão do coordenador de uma Coordenada, em plan, recebe o perfil planejamento
// (propor_plano) ou síntese (entregar_sintese), conforme a etapa que o orquestrador abriu. Não exige concessão no agente:
// é o único canal de saída da etapa, de mundo fechado, e não amplia nada (como o perfil delegacao da subordinada).
// Gateway (AD-72, #311, PR 2): cada chamada passa pelo IGatewayDeFerramentas da Application com a identidade deste canal;
// as ferramentas estão no catálogo com política Nunca e continuam fazendo exatamente o que faziam.
public sealed class FerramentasDeCoordenacao(IServiceScopeFactory escopos, ILogger<FerramentasDeCoordenacao> logger,
    (string Command, IReadOnlyList<string> Prefix)? comando = null) : IProvedorDeFerramentas, IDisposable
{
    public const int MaximoDeChamadas = 50;

    private sealed class Canal(PerfilDeCoordenacao perfil, EscopoDaRevisaoDto? escopo, EscopoDeCoordenacao? coordenacao,
        string token, NamedPipeServerStream pipe)
    {
        public PerfilDeCoordenacao Perfil { get; } = perfil;
        public EscopoDaRevisaoDto? Escopo { get; } = escopo;
        public EscopoDeCoordenacao? Coordenacao { get; } = coordenacao;
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
        // Só a sessão reservada (com observador) que o orquestrador abriu com um escopo: a revisão confinada, ou a
        // origem/subordinada de uma delegação em plan.
        if (descartado || request.Observer is null || Perfil(request) is not { } perfil)
            return Task.FromResult<IReadOnlyList<AgentToolServer>>([]);
        var nome = ServidorMcpDeCoordenacao.PrefixoDoPipe + Guid.NewGuid().ToString("N");
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var pipe = new NamedPipeServerStream(nome, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        var agente = request.RegisteredAgent!;
        var canal = new Canal(perfil, agente.EscopoDaRevisao, agente.Coordenacao, token, pipe);
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
        // A revisão mantém os argumentos da #308; os perfis da #309 vêm antes do pipe (o pipe é sempre o último).
        string[] doPerfil = perfil == PerfilDeCoordenacao.Revisao ? []
            : [ServidorMcpDeCoordenacao.ArgumentoDoPerfil, ServidorMcpDeCoordenacao.NomeDoPerfil(perfil)];
        return Task.FromResult<IReadOnlyList<AgentToolServer>>(
        [
            new(ServidorMcpDeCoordenacao.Nome, executavel,
                [.. prefixo, ServidorMcpDeCoordenacao.Argumento, .. doPerfil, "--pipe", nome],
                new Dictionary<string, string> { [ServidorMcpDeCoordenacao.VariavelDoToken] = token },
                ServidorMcpDeCoordenacao.DeLeitura(perfil), ServidorMcpDeCoordenacao.PreAutorizadas(perfil))
        ]);
    }

    // O perfil que a sessão pode receber, ou nulo. Delegação só em plan e nunca numa sessão confinada de outro tipo; a
    // origem só com a concessão coordenacao e alguém a consultar na versão congelada.
    internal static PerfilDeCoordenacao? Perfil(SessionStartRequest request) => request.RegisteredAgent switch
    {
        { LeituraConfinada: true, EscopoDaRevisao: not null, Coordenacao: null } => PerfilDeCoordenacao.Revisao,
        { LeituraConfinada: false, EscritaConfinada: false, Coordenacao: CoordenacaoDaDelegacao }
            when request.Profile == AgentPermissionProfile.Plan => PerfilDeCoordenacao.Delegacao,
        { LeituraConfinada: false, EscritaConfinada: false, Coordenacao: CoordenacaoDoCoordenador coordenador }
            when request.Profile == AgentPermissionProfile.Plan => coordenador.Perfil,
        { LeituraConfinada: false, EscritaConfinada: false, Coordenacao: CoordenacaoDaOrigem } agente
            when request.Profile == AgentPermissionProfile.Plan &&
                 agente.Configuracao.PermiteFerramenta(Dante.Domain.Agentes.FerramentaDoDante.Coordenacao) &&
                 agente.Configuracao.PodeConsultar.Count > 0 => PerfilDeCoordenacao.Origem,
        _ => null
    };

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
                    resposta = await PeloGatewayAsync(sessao, canal, nome, argumentos, ct);
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

    // Toda chamada passa pelo gateway da Application (AD-72, #311, PR 2): identidade pelo canal (a sessão e o perfil
    // fixados aqui, nunca pelo agente), catálogo fechado, concessão do perfil, schema da ferramenta e política Nunca.
    // Sem gateway, nada executa (fail-closed). A auditoria registra ferramenta, perfil, sessão e desfecho, sem argumentos.
    private async Task<JsonObject> PeloGatewayAsync(string sessao, Canal canal, string nome, JsonElement argumentos,
        CancellationToken ct)
    {
        await using var escopo = escopos.CreateAsyncScope();
        var gateway = escopo.ServiceProvider.GetService<IGatewayDeFerramentas>();
        if (gateway is null)
        {
            logger.LogWarning("dante_coordenacao sem gateway; chamada {Ferramenta} recusada.", nome);
            return ServidorMcpDeCoordenacao.Erro("dante_coordenacao indisponível; tente de novo.");
        }
        var perfil = canal.Perfil;
        var chamada = new ChamadaDeFerramentaDto(
            new SessaoDaFerramentaDto(sessao, ServidorMcpDeCoordenacao.Nome,
                ServidorMcpDeCoordenacao.Catalogo(perfil).Select(f => f.Nome).ToArray()),
            nome, argumentos, a => ServidorMcpDeCoordenacao.Validar(nome, a, perfil));
        try
        {
            var resposta = await gateway.ChamarAsync(chamada, token => perfil switch
            {
                PerfilDeCoordenacao.Origem => ChamarDaOrigemAsync((CoordenacaoDaOrigem)canal.Coordenacao!, nome, argumentos,
                    token),
                PerfilDeCoordenacao.Delegacao => ChamarDaDelegacaoAsync((CoordenacaoDaDelegacao)canal.Coordenacao!, argumentos,
                    token),
                PerfilDeCoordenacao.Planejamento or PerfilDeCoordenacao.Sintese => ChamarDoCoordenadorAsync(
                    (CoordenacaoDoCoordenador)canal.Coordenacao!, argumentos, token),
                _ => ChamarAsync(sessao, canal.Escopo!, nome, argumentos, token)
            }, ct);
            logger.LogInformation("dante_coordenacao: {Ferramenta} ({Perfil}) na sessão {Sessao}: {Desfecho}.",
                Auditavel(nome), ServidorMcpDeCoordenacao.NomeDoPerfil(perfil), sessao,
                resposta["isError"]?.GetValue<bool>() == true ? "recusada pelo caso de uso" : "executada");
            return resposta;
        }
        catch (ChamadaRecusadaException)
        {
            logger.LogInformation("dante_coordenacao: {Ferramenta} ({Perfil}) na sessão {Sessao}: recusada pelo gateway.",
                Auditavel(nome), ServidorMcpDeCoordenacao.NomeDoPerfil(perfil), sessao);
            throw;
        }
    }

    // O nome vem do agente: só entra na auditoria se for de uma ferramenta conhecida.
    private static string Auditavel(string nome) =>
        CatalogoDeAcoes.Obter(ServidorMcpDeCoordenacao.Nome + "." + nome) is null ? "(desconhecida)" : nome;

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

    // Origem: o pedido usa a execução, a etapa e o dono do escopo, sob a posse da etapa e pela sessão vinculada a ela.
    // Do agente vêm só destinatário, contrato, objetivo, entrada e critérios; escrita nunca.
    private async Task<JsonObject> ChamarDaOrigemAsync(CoordenacaoDaOrigem escopo, string nome, JsonElement argumentos,
        CancellationToken ct)
    {
        var alvo = escopo.Controle.Alvo;
        if (nome == ServidorMcpDeCoordenacao.EstadoDaExecucao)
        {
            await using var consulta = escopos.CreateAsyncScope();
            var lista = await consulta.ServiceProvider.GetRequiredService<IDelegacoesAppService>()
                .AcompanharDaOrigemAsync(escopo.Proprietario, alvo.IdExecucao, ct);
            return ServidorMcpDeCoordenacao.Resultado(new
            {
                execucao = alvo.Codigo,
                delegacoes = lista.Select(a => new
                {
                    delegacao = a.Delegacao.CodigoDaSubordinada, destinatario = a.Delegacao.Destinatario,
                    contrato = a.Delegacao.Contrato, estado = a.Delegacao.Estado.ToString(),
                    progresso = a.UltimoMarco?.ToString(), motivo = a.Delegacao.Motivo,
                    resultado = a.Delegacao.Resultado is { } r ? JsonNode.Parse(r) : null,
                    criada_em = a.Delegacao.CriadaEm, encerrada_em = a.Delegacao.EncerradaEm
                })
            });
        }
        var entrada = argumentos.GetProperty("entrada");
        if (entrada.ValueKind != JsonValueKind.Object) throw new ArgumentException("entrada é um objeto do contrato.");
        var criterios = argumentos.TryGetProperty("criterios", out var c) ? Texto(c, "criterios") : null;
        var solicitada = await escopo.Controle.SolicitarDelegacaoAsync(new SolicitacaoDeDelegacaoDto(escopo.Proprietario,
            alvo.IdExecucao, alvo.IdEtapa, Texto(argumentos.GetProperty("destinatario"), "destinatario"),
            Texto(argumentos.GetProperty("contrato"), "contrato"), entrada.GetRawText(),
            Texto(argumentos.GetProperty("objetivo"), "objetivo"), criterios), ct);
        var d = solicitada.Delegacao;
        return ServidorMcpDeCoordenacao.Resultado(new
        {
            delegacao = d.CodigoDaSubordinada, destinatario = d.Destinatario, contrato = d.Contrato,
            estado = d.Estado.ToString(), nova = solicitada.Nova,
            mensagem = solicitada.Nova
                ? "Delegação registrada e enfileirada. Termine este turno: a tarefa aguarda as delegações e é " +
                  "retomada com os resultados."
                : "Esta unidade já estava delegada a este agente; nada novo foi criado."
        });
    }

    // Subordinada: o resultado do contrato, gravado na hora sob a posse da etapa e pela sessão vinculada a ela.
    private static async Task<JsonObject> ChamarDaDelegacaoAsync(CoordenacaoDaDelegacao escopo, JsonElement argumentos,
        CancellationToken ct)
    {
        var resultado = argumentos.GetProperty("resultado");
        if (resultado.ValueKind != JsonValueKind.Object) throw new ArgumentException("resultado é um objeto do contrato.");
        var (desfecho, jaRegistrado) = await escopo.Entrega.EntregarAsync(resultado.GetRawText(), ct);
        return desfecho.Estado == EstadoDaDelegacao.Respondida
            ? ServidorMcpDeCoordenacao.Resultado(new
            {
                registrado = true, ja_registrado = jaRegistrado,
                mensagem = "Resultado registrado. Encerre com uma resposta curta."
            })
            : ServidorMcpDeCoordenacao.Erro($"A delegação terminou como {desfecho.Estado}: {desfecho.Motivo}");
    }

    // Coordenador: o plano ou a síntese, gravados na hora sob a posse da etapa e pela sessão vinculada a ela. Recusas da
    // validação voltam como erro da ferramenta, com cada motivo, para a correção única.
    private static async Task<JsonObject> ChamarDoCoordenadorAsync(CoordenacaoDoCoordenador escopo, JsonElement argumentos,
        CancellationToken ct)
    {
        var entrega = escopo.Entrega;
        var planejamento = entrega.Passo == PassoDaCoordenacao.Planejamento;
        var valor = argumentos.GetProperty(planejamento ? "plano" : "sintese");
        if (valor.ValueKind != JsonValueKind.Object)
            throw new ArgumentException(planejamento ? "plano é um objeto." : "sintese é um objeto.");
        await entrega.EntregarAsync(valor.GetRawText(), ct);
        if (planejamento)
        {
            var proposta = entrega.Proposta!;
            return proposta.Desfecho switch
            {
                DesfechoDaProposta.GateAberto => ServidorMcpDeCoordenacao.Resultado(new
                {
                    registrado = true, revisao = proposta.Revisao, hash = proposta.HashDoPlano![..12],
                    mensagem = "Plano registrado e enviado à aprovação humana; nada é delegado antes dela. Encerre com " +
                        "uma resposta curta."
                }),
                DesfechoDaProposta.CorrecaoPedida => ServidorMcpDeCoordenacao.Erro(
                    "Plano recusado pela validação do servidor. Corrija e chame propor_plano de novo com o plano inteiro " +
                    "(uma correção): " + Recusas(proposta.Recusas.Select(r =>
                        $"{r.Motivo}{(r.Unidade is null ? "" : $" ({r.Unidade})")}: {r.Mensagem}"))),
                DesfechoDaProposta.Encerrada => ServidorMcpDeCoordenacao.Erro(
                    "Plano recusado de novo; a coordenação terminou sem plano: " + Recusas(proposta.Recusas.Select(r =>
                        $"{r.Motivo}{(r.Unidade is null ? "" : $" ({r.Unidade})")}: {r.Mensagem}"))),
                _ => ServidorMcpDeCoordenacao.Erro("A coordenação foi cancelada pelo dono.")
            };
        }
        var sintese = entrega.Sintese!;
        return sintese.Desfecho switch
        {
            DesfechoDaSintese.Registrada => ServidorMcpDeCoordenacao.Resultado(new
            {
                registrado = true, hash = sintese.Hash![..12],
                mensagem = "Síntese registrada ao lado da trilha das unidades. Encerre com uma resposta curta."
            }),
            DesfechoDaSintese.CorrecaoPedida => ServidorMcpDeCoordenacao.Erro(
                "Síntese recusada pela validação estrutural. Corrija e chame entregar_sintese de novo com a síntese inteira " +
                "(uma correção): " + Recusas(sintese.Recusas.Select(r =>
                    $"{r.Motivo}{(r.Referencia is null ? "" : $" ({r.Referencia})")}: {r.Mensagem}"))),
            DesfechoDaSintese.Encerrada => ServidorMcpDeCoordenacao.Erro(
                "Síntese recusada de novo; a coordenação terminou só com a trilha das unidades."),
            _ => ServidorMcpDeCoordenacao.Erro("A coordenação foi cancelada pelo dono.")
        };
    }

    // As recusas cabem na resposta do canal: no máximo 20, cada uma cortada.
    private static string Recusas(IEnumerable<string> recusas) =>
        string.Join("; ", recusas.Take(20).Select(r => r.Length <= 400 ? r : r[..400] + "…"));

    private static string Texto(JsonElement valor, string campo) => valor.ValueKind == JsonValueKind.String
        ? valor.GetString()! : throw new ArgumentException($"{campo} é um texto.");

    private static string Nome(VereditoDeRevisao veredito) => veredito switch
    {
        VereditoDeRevisao.Aprovado => "aprovado",
        VereditoDeRevisao.RequerCorrecoes => "requer_correcoes",
        _ => "bloqueado"
    };
}
