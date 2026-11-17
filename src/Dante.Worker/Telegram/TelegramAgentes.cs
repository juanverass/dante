using System.Globalization;
using Dante.Application.Agentes;
using Dante.Application.Comum;

namespace Dante.Worker.Telegram;

// Adapter de entrada do cadastro de agentes (AD-68): interpreta /agentes e /agente, chama o caso de uso num escopo
// próprio e formata a resposta. Só existe com banco e Orquestracao:Habilitada; sem ele, os comandos respondem que o
// cadastro está indisponível e nada mais muda. Nenhuma regra de cadastro vive aqui.
public sealed class TelegramAgentes(IServiceScopeFactory escopos)
{
    internal const string Uso =
        "Uso: /agente criar <handle> driver=claude|codex [model=…] [effort=…] [modo=plan|manual|auto] [maximo=…] " +
        "[escrita=sim|nao] [capacidades=revisar,…] [ferramentas=brain,planilhas] [repos=@alias,…|*] [simultaneas=N] " +
        "[tokens=N] [turnos=N] [minutos=N] (instruções nas linhas seguintes) | /agente ver|desativar|reativar|arquivar " +
        "<handle> | /agente editar <handle> [opções] [instrucoes=remover] | /agente nome|descricao <handle> <texto>";

    public async Task<ResolucaoDeAgenteDto> ResolverAsync(string handle, CancellationToken cancellationToken)
    {
        await using var escopo = escopos.CreateAsyncScope();
        return await Cadastro(escopo).ResolverAsync(handle, cancellationToken);
    }

    public async Task<string> ListarAsync(string argumentos, CancellationToken cancellationToken)
    {
        var todos = argumentos.Trim().Equals("todos", StringComparison.OrdinalIgnoreCase);
        if (!todos && argumentos.Trim().Length > 0) return "Uso: /agentes [todos]";
        return await ExecutarAsync(async cadastro =>
        {
            var lista = await cadastro.ListarAsync(todos, cancellationToken);
            return "Agentes:\n" + string.Join('\n', lista.Select(Resumo)) +
                (lista.Any(agente => !agente.Embutido) ? string.Empty
                    : "\nNenhum agente registrado. Crie um com /agente criar.");
        });
    }

    // estadoDasSessoes: sessões ainda vivas do handle, para a resposta de desativar e a recusa de arquivar.
    public async Task<string> AtenderAsync(long idDoUsuario, string argumentos,
        Func<string, (int Proprias, int Outras, IReadOnlyList<string> Codigos)> sessoesVivas,
        CancellationToken cancellationToken)
    {
        var linhas = argumentos.Replace("\r\n", "\n").Split('\n', 2);
        var partes = linhas[0].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var corpo = linhas.Length > 1 ? linhas[1] : null;
        if (partes.Length < 2) return Uso;
        var (acao, handle) = (partes[0].ToLowerInvariant(), partes[1]);
        return await ExecutarAsync(async cadastro =>
        {
            switch (acao)
            {
                case "criar" or "create":
                {
                    var configuracao = Criacao(partes[2..], corpo);
                    var novo = await cadastro.CriarAsync(new NovoAgenteDto(handle, configuracao, idDoUsuario),
                        cancellationToken);
                    return $"Agente {novo.Handle} criado (v1).\n{Detalhe(novo)}";
                }
                case "ver" or "show":
                    return await cadastro.ObterAsync(handle, cancellationToken) is { } agente
                        ? Detalhe(agente) : $"Agente {handle.ToLowerInvariant()} não cadastrado. Veja /agentes.";
                case "editar" or "edit":
                {
                    var editado = await cadastro.EditarAsync(handle, Alteracao(partes[2..], corpo), idDoUsuario,
                        cancellationToken);
                    return $"Agente {editado.Handle} agora está na v{editado.NumeroDaVersaoAtual}. Sessões já abertas " +
                        $"mantêm a versão com que começaram.\n{Detalhe(editado)}";
                }
                case "nome" or "descricao":
                {
                    var texto = string.Join(' ', partes[2..]) + (corpo is null ? string.Empty : "\n" + corpo);
                    var atual = await cadastro.ObterAsync(handle, cancellationToken)
                        ?? throw new InvalidOperationException($"Agente {handle.ToLowerInvariant()} não cadastrado. Veja /agentes.");
                    var apresentado = await cadastro.ApresentarAsync(handle, acao == "nome" ? texto : atual.Nome,
                        acao == "descricao" ? texto : atual.Descricao, cancellationToken);
                    return $"Agente {apresentado.Handle} atualizado, sem versão nova.";
                }
                case "desativar":
                {
                    var desativado = await cadastro.DesativarAsync(handle, cancellationToken);
                    var (proprias, outras, codigos) = sessoesVivas(desativado.Handle);
                    return $"Agente {desativado.Handle} desativado: não inicia sessões nem tarefas novas." +
                        (proprias + outras == 0 ? string.Empty
                            : $" Continuam até o fim: {proprias + outras} sessão(ões) ativa(s)" +
                              (codigos.Count == 0 ? "." : $" ({string.Join(", ", codigos)})."));
                }
                case "reativar":
                    return $"Agente {(await cadastro.ReativarAsync(handle, cancellationToken)).Handle} reativado.";
                case "arquivar":
                {
                    var (proprias, outras, _) = sessoesVivas(handle.ToLowerInvariant());
                    if (proprias + outras > 0)
                        return $"O agente {handle.ToLowerInvariant()} ainda tem {proprias + outras} sessão(ões) ativa(s); " +
                            "arquive depois que terminarem.";
                    var arquivado = await cadastro.ArquivarAsync(handle, cancellationToken);
                    return $"Agente {arquivado.Handle} arquivado. O handle não volta a ser usado; as versões ficam no histórico.";
                }
                default:
                    return Uso;
            }
        });
    }

    internal static string Resumo(AgenteDto agente) => agente.Embutido
        ? $"- {agente.Handle} (embutido; segue suas preferências)"
        : $"- {agente.Handle} v{agente.NumeroDaVersaoAtual} — {Estado(agente.Estado)}" +
          (agente.VersaoAtual is { } versao ? $", {Nome(versao.Configuracao.Driver)}, modo " +
              $"{Nome(versao.Configuracao.PerfilPadrao)} (máx. {Nome(versao.Configuracao.PerfilMaximo)})" : string.Empty) +
          (agente.Nome is null ? string.Empty : $" — {agente.Nome}");

    internal static string Detalhe(AgenteDto agente)
    {
        if (agente.Embutido) return $"{agente.Handle}: {agente.Descricao}";
        var linhas = new List<string> { $"{agente.Handle} v{agente.NumeroDaVersaoAtual} — {Estado(agente.Estado)}" };
        if (agente.Nome is not null) linhas.Add($"Nome: {agente.Nome}");
        if (agente.Descricao is not null) linhas.Add($"Descrição: {agente.Descricao}");
        if (agente.VersaoAtual is { } versao)
        {
            var c = versao.Configuracao;
            linhas.Add($"Driver: {Nome(c.Driver)}; modelo {c.Modelo ?? "padrão do driver"}; esforço {c.Esforco ?? "padrão do driver"}");
            linhas.Add($"Modo padrão: {Nome(c.PerfilPadrao)}; máximo: {Nome(c.PerfilMaximo)}; escrita: {(c.PodeEscrever ? "sim" : "não")}");
            linhas.Add($"Capacidades: {Lista(c.Capacidades.Select(Nome))}");
            linhas.Add($"Ferramentas do D.A.N.T.E.: {Lista(c.Ferramentas.Select(Nome))}");
            linhas.Add("Repositórios: " + (c.QualquerRepositorioPermitido ? "qualquer cadastrado"
                : c.Repositorios.Count == 0 ? "nenhum (só o workspace geral)" : string.Join(", ", c.Repositorios)));
            var limites = new[]
            {
                c.Limites.ExecucoesSimultaneas is { } s ? $"{s} sessão(ões) simultânea(s)" : null,
                c.Limites.TokensPorExecucao is { } t ? $"{t} tokens por execução" : null,
                c.Limites.TurnosPorEtapa is { } u ? $"{u} turnos por etapa" : null,
                c.Limites.MinutosPorEtapa is { } m ? $"{m} min por etapa" : null
            }.OfType<string>().ToArray();
            linhas.Add($"Limites: {(limites.Length == 0 ? "nenhum" : string.Join("; ", limites))}");
            linhas.Add($"Hash: {versao.HashDaConfiguracao[..12]}");
            linhas.Add(c.Instrucoes is null ? "Instruções: nenhuma" : $"Instruções:\n{c.Instrucoes}");
        }
        if (agente.Versoes.Count > 1)
            linhas.Add("Versões: " + string.Join(", ", agente.Versoes.Select(versao =>
                $"v{versao.Numero} ({versao.CriadaEm.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC)")));
        return string.Join('\n', linhas);
    }

    private async Task<string> ExecutarAsync(Func<ICadastroDeAgentesAppService, Task<string>> operacao)
    {
        await using var escopo = escopos.CreateAsyncScope();
        try
        {
            return await operacao(Cadastro(escopo));
        }
        // Recusas do caso de uso já vêm em texto para o usuário; nada interno vaza.
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or ConflitoDeConcorrenciaException or FormatException)
        {
            return exception is ConflitoDeConcorrenciaException
                ? "O cadastro mudou ao mesmo tempo por outra operação (ou o handle acabou de ser usado). Confira com /agentes."
                : exception.Message;
        }
    }

    private static ICadastroDeAgentesAppService Cadastro(AsyncServiceScope escopo) =>
        escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>();

    private static ConfiguracaoDoAgenteDto Criacao(string[] opcoes, string? instrucoes)
    {
        var valores = Opcoes(opcoes);
        if (!valores.TryGetValue("driver", out var driverTexto))
            throw new ArgumentException("Informe driver=claude ou driver=codex.");
        var modo = valores.TryGetValue("modo", out var m) ? Perfil(m) : AgentPermissionProfile.Plan;
        var maximo = valores.TryGetValue("maximo", out var x) ? Perfil(x) : modo;
        return new ConfiguracaoDoAgenteDto
        {
            Driver = Driver(driverTexto),
            Modelo = valores.GetValueOrDefault("model"),
            Esforco = valores.GetValueOrDefault("effort"),
            Instrucoes = instrucoes,
            PerfilPadrao = modo,
            PerfilMaximo = maximo,
            PodeEscrever = valores.TryGetValue("escrita", out var escrita) ? SimNao(escrita) : maximo != AgentPermissionProfile.Plan,
            Capacidades = valores.TryGetValue("capacidades", out var cap) ? Enums<CapacidadeDoAgente>(cap) : [],
            Ferramentas = valores.TryGetValue("ferramentas", out var fer) ? Enums<FerramentaDoDante>(fer) : [],
            Repositorios = valores.TryGetValue("repos", out var repos) ? Repos(repos) : [],
            Limites = new LimitesDoAgente(
                Curto(valores, "simultaneas"), Inteiro(valores, "tokens"), Curto(valores, "turnos"), Curto(valores, "minutos"))
        };
    }

    private static AlteracaoDoAgenteDto Alteracao(string[] opcoes, string? instrucoes)
    {
        var valores = Opcoes(opcoes);
        var remover = valores.TryGetValue("instrucoes", out var acao) &&
            (acao.Equals("remover", StringComparison.OrdinalIgnoreCase) ? true
                : throw new ArgumentException("Use instrucoes=remover, ou escreva as novas instruções nas linhas seguintes."));
        var alteracao = new AlteracaoDoAgenteDto
        {
            Driver = valores.TryGetValue("driver", out var driver) ? Driver(driver) : null,
            Modelo = valores.GetValueOrDefault("model"),
            Esforco = valores.GetValueOrDefault("effort"),
            Instrucoes = string.IsNullOrWhiteSpace(instrucoes) ? null : instrucoes,
            LimparInstrucoes = remover,
            PerfilPadrao = valores.TryGetValue("modo", out var modo) ? Perfil(modo) : null,
            PerfilMaximo = valores.TryGetValue("maximo", out var maximo) ? Perfil(maximo) : null,
            PodeEscrever = valores.TryGetValue("escrita", out var escrita) ? SimNao(escrita) : null,
            Capacidades = valores.TryGetValue("capacidades", out var cap) ? Enums<CapacidadeDoAgente>(cap) : null,
            Ferramentas = valores.TryGetValue("ferramentas", out var fer) ? Enums<FerramentaDoDante>(fer) : null,
            Repositorios = valores.TryGetValue("repos", out var repos) ? Repos(repos) : null,
            ExecucoesSimultaneas = Curto(valores, "simultaneas", aceitaZero: true),
            TokensPorExecucao = Inteiro(valores, "tokens", aceitaZero: true),
            TurnosPorEtapa = Curto(valores, "turnos", aceitaZero: true),
            MinutosPorEtapa = Curto(valores, "minutos", aceitaZero: true)
        };
        if (valores.Count == 0 && alteracao.Instrucoes is null)
            throw new ArgumentException("Nada para editar. " + Uso);
        return alteracao;
    }

    private static readonly string[] Chaves =
        ["driver", "model", "effort", "modo", "maximo", "escrita", "capacidades", "ferramentas", "repos", "simultaneas",
         "tokens", "turnos", "minutos", "instrucoes"];

    private static Dictionary<string, string> Opcoes(string[] opcoes)
    {
        var valores = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var opcao in opcoes)
        {
            var igual = opcao.IndexOf('=');
            var chave = igual <= 0 ? opcao : opcao[..igual];
            if (igual <= 0 || igual == opcao.Length - 1 || !Chaves.Contains(chave, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Opção inválida: {opcao}. {Uso}");
            if (!valores.TryAdd(chave, opcao[(igual + 1)..])) throw new ArgumentException($"Opção repetida: {chave}.");
        }
        return valores;
    }

    private static AgentKind Driver(string valor) => valor.ToLowerInvariant() switch
    {
        "claude" => AgentKind.Claude,
        "codex" => AgentKind.Codex,
        _ => throw new ArgumentException("Driver inválido: use claude ou codex.")
    };

    private static AgentPermissionProfile Perfil(string valor) =>
        Sessions.AgentSessionModes.TryParse(valor, out var perfil) ? perfil
            : throw new ArgumentException($"Modo inválido: {valor}. Use plan, manual ou auto.");

    private static bool SimNao(string valor) => valor.ToLowerInvariant() switch
    {
        "sim" or "s" or "true" => true,
        "nao" or "não" or "n" or "false" => false,
        _ => throw new ArgumentException("Use escrita=sim ou escrita=nao.")
    };

    private static IReadOnlyList<T> Enums<T>(string valor) where T : struct, Enum =>
        valor.Equals("nenhuma", StringComparison.OrdinalIgnoreCase) ? [] : valor.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => Enum.TryParse<T>(item, ignoreCase: true, out var lido) && Enum.IsDefined(lido) &&
                !int.TryParse(item, out _) ? lido
                : throw new ArgumentException($"Valor inválido: {item}. Opções: " +
                    $"{string.Join(", ", Enum.GetNames<T>().Select(nome => nome.ToLowerInvariant()))}."))
            .ToArray();

    private static IReadOnlyList<string> Repos(string valor) =>
        valor.Equals("nenhum", StringComparison.OrdinalIgnoreCase) ? [] : valor.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static long? Inteiro(Dictionary<string, string> valores, string chave, bool aceitaZero = false)
    {
        if (!valores.TryGetValue(chave, out var texto)) return null;
        return long.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var numero) && (numero > 0 || aceitaZero)
            ? numero : throw new ArgumentException($"Valor inválido para {chave}: {texto}.");
    }

    private static int? Curto(Dictionary<string, string> valores, string chave, bool aceitaZero = false) =>
        Inteiro(valores, chave, aceitaZero) is { } numero
            ? numero <= int.MaxValue ? (int)numero : throw new ArgumentException($"Valor alto demais para {chave}.")
            : null;

    private static string Lista(IEnumerable<string> valores) =>
        valores.ToArray() is { Length: > 0 } lista ? string.Join(", ", lista) : "nenhuma";

    private static string Nome<T>(T valor) where T : struct, Enum => valor.ToString().ToLowerInvariant();

    private static string Estado(EstadoDoAgente estado) => estado switch
    {
        EstadoDoAgente.Ativo => "ativo",
        EstadoDoAgente.Desativado => "desativado",
        _ => "arquivado"
    };
}
