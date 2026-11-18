using System.Globalization;
using Dante.Application.Comum;
using Dante.Application.Grupos;
using Dante.Domain.Grupos;

namespace Dante.Worker.Telegram;

// Adapter de entrada do cadastro de grupos (AD-69, #308): interpreta /grupos e /grupo, chama o caso de uso num escopo
// próprio e formata a resposta. Só existe com banco e Orquestracao:Habilitada. Nenhuma regra de cadastro vive aqui:
// validação de papéis, roteiro, política, agentes e ativação é toda da Application e do domínio.
public sealed class TelegramGrupos(IServiceScopeFactory escopos)
{
    internal const string Uso =
        "Uso: /grupo criar <nome> roteiro=implementar:<papel>,revisar:<papel>[,corrigir:<papel>],aprovar [rodadas=N] " +
        "[repos=@alias,…] [duracao=minutos] (participantes nas linhas seguintes: <papel>=<handle> [escrita] [: " +
        "responsabilidades]) | /grupo ver|desativar|reativar|arquivar <nome> | /grupo editar <nome> [opções] " +
        "[participantes nas linhas seguintes] | /grupo ativar <nome> v<N> <hash> | /grupo descricao <nome> <texto>";

    // Grupo pronto para uma execução nova (versão ativa e versão atual de cada participante), ou o motivo da recusa.
    public async Task<ResolucaoDeGrupoDto> ResolverAsync(string nome, CancellationToken cancellationToken)
    {
        await using var escopo = escopos.CreateAsyncScope();
        return await escopo.ServiceProvider.GetRequiredService<ICadastroDeGruposAppService>()
            .ResolverParaExecucaoAsync(nome, cancellationToken);
    }

    // /grupo <nome> @alias <tarefa>: o primeiro termo não é ação e o segundo é um repositório.
    internal static bool PedidoDeExecucao(string argumentos, out string nome, out string resto)
    {
        var texto = argumentos.TrimStart();
        var separador = texto.IndexOfAny([' ', '\t', '\r', '\n']);
        nome = separador < 0 ? texto : texto[..separador];
        resto = separador < 0 ? "" : texto[(separador + 1)..].TrimStart();
        return nome.Length > 0 && !GrupoDeAgentes.NomesReservados.Contains(nome.ToLowerInvariant()) && resto.StartsWith('@');
    }

    public async Task<string> ListarAsync(string argumentos, CancellationToken cancellationToken)
    {
        var todos = argumentos.Trim().Equals("todos", StringComparison.OrdinalIgnoreCase);
        if (!todos && argumentos.Trim().Length > 0) return "Uso: /grupos [todos]";
        return await ExecutarAsync(async cadastro =>
        {
            var lista = await cadastro.ListarAsync(todos, cancellationToken);
            return lista.Count == 0 ? "Nenhum grupo cadastrado. Crie um com /grupo criar."
                : "Grupos:\n" + string.Join('\n', lista.Select(Resumo));
        });
    }

    public async Task<string> AtenderAsync(long idDoUsuario, string argumentos, CancellationToken cancellationToken)
    {
        var linhas = argumentos.Replace("\r\n", "\n").Split('\n', 2);
        var partes = linhas[0].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var corpo = linhas.Length > 1 ? linhas[1] : null;
        if (partes.Length < 2) return Uso;
        var (acao, nome) = (partes[0].ToLowerInvariant(), partes[1]);
        return await ExecutarAsync(async cadastro =>
        {
            switch (acao)
            {
                case "criar" or "create":
                {
                    var novo = await cadastro.CriarAsync(new NovoGrupoDto(nome, Criacao(partes[2..], corpo), idDoUsuario),
                        cancellationToken);
                    return $"Grupo {novo.Nome} criado (v1), ainda não ativado.\n{Detalhe(novo)}\n\n{ComoAtivar(novo)}";
                }
                case "ver" or "show":
                    return await cadastro.ObterAsync(nome, cancellationToken) is { } grupo
                        ? Detalhe(grupo) : $"Grupo {nome.ToLowerInvariant()} não cadastrado. Veja /grupos.";
                case "editar" or "edit":
                {
                    var editado = await cadastro.EditarAsync(nome, Alteracao(partes[2..], corpo), idDoUsuario,
                        cancellationToken);
                    return $"Grupo {editado.Nome} agora tem a v{editado.NumeroDaVersaoAtual}, ainda não ativada; " +
                        (editado.NumeroDaVersaoAtiva is { } ativa ? $"execuções novas seguem com a v{ativa}."
                            : "nenhuma versão está ativa.") + $"\n{Detalhe(editado)}\n\n{ComoAtivar(editado)}";
                }
                case "ativar":
                {
                    if (partes.Length != 4 || !TryVersao(partes[2], out var numero))
                        return "Uso: /grupo ativar <nome> v<N> <hash> (o hash exibido em /grupo ver, ao menos 12 caracteres).";
                    var ativado = await cadastro.AtivarAsync(nome, numero, partes[3], idDoUsuario, cancellationToken);
                    return $"Grupo {ativado.Nome}: v{ativado.NumeroDaVersaoAtiva} ativada. Execuções novas usam esta " +
                        "versão; as já iniciadas mantêm a delas. A ativação não concede nada além das permissões já dadas " +
                        "a cada agente.";
                }
                case "descricao":
                {
                    var texto = string.Join(' ', partes[2..]) + (corpo is null ? string.Empty : "\n" + corpo);
                    var descrito = await cadastro.DescreverAsync(nome, texto, cancellationToken);
                    return $"Grupo {descrito.Nome} atualizado, sem versão nova.";
                }
                case "desativar":
                    return $"Grupo {(await cadastro.DesativarAsync(nome, cancellationToken)).Nome} desativado: não inicia " +
                        "execuções novas; as já iniciadas seguem até o fim.";
                case "reativar":
                    return $"Grupo {(await cadastro.ReativarAsync(nome, cancellationToken)).Nome} reativado.";
                case "arquivar":
                    return $"Grupo {(await cadastro.ArquivarAsync(nome, cancellationToken)).Nome} arquivado. O nome não " +
                        "volta a ser usado; as versões e ativações ficam no histórico.";
                default:
                    return Uso;
            }
        });
    }

    internal static string Resumo(GrupoDeAgentesDto grupo) =>
        $"- {grupo.Nome} v{grupo.NumeroDaVersaoAtual} — {Estado(grupo.Estado)}, " +
        (grupo.NumeroDaVersaoAtiva is { } ativa ? $"ativa v{ativa}" : "sem versão ativa") +
        (grupo.VersaoAtiva is { } versao ? $" ({Roteiro(versao)})" : string.Empty) +
        (grupo.Descricao is null ? string.Empty : $" — {grupo.Descricao}");

    internal static string Detalhe(GrupoDeAgentesDto grupo)
    {
        var linhas = new List<string>
        {
            $"{grupo.Nome} — {Estado(grupo.Estado)}; versão atual v{grupo.NumeroDaVersaoAtual}; " +
            (grupo.NumeroDaVersaoAtiva is { } ativa ? $"ativa v{ativa}" : "nenhuma versão ativa")
        };
        if (grupo.Descricao is not null) linhas.Add($"Descrição: {grupo.Descricao}");
        if (grupo.VersaoAtual is { } atual) linhas.AddRange(Versao("Versão atual", atual));
        if (grupo.VersaoAtiva is { } versaoAtiva && versaoAtiva.Id != grupo.VersaoAtual?.Id)
            linhas.AddRange(Versao("Versão ativa", versaoAtiva));
        if (grupo.Versoes.Count > 1)
            linhas.Add("Versões: " + string.Join(", ", grupo.Versoes.Select(versao =>
                $"v{versao.Numero} ({Data(versao.CriadaEm)})")));
        if (grupo.Ativacoes.Count > 0)
            linhas.Add("Ativações: " + string.Join(", ", grupo.Ativacoes.Select(ativacao =>
                $"v{ativacao.NumeroDaVersao} em {Data(ativacao.Em)} por {ativacao.IdDoAutor} (hash {ativacao.HashAprovado[..12]})")));
        return string.Join('\n', linhas);
    }

    private static IEnumerable<string> Versao(string titulo, VersaoDoGrupoDto versao)
    {
        yield return $"{titulo} v{versao.Numero} — hash {versao.HashDaConfiguracao[..12]}" +
            (versao.Integra ? string.Empty : " (NÃO CONFERE com o conteúdo gravado)");
        if (versao.Configuracao is not { } configuracao) yield break;
        yield return $"Roteiro: {Roteiro(versao)}";
        var politica = configuracao.Politica;
        yield return $"Rodadas de correção: {politica.RodadasDeRevisaoMaximas}; sequencial; repositórios: " +
            (politica.RepositoriosPermitidos!.Count == 0 ? "os do pedido" : string.Join(", ", politica.RepositoriosPermitidos)) +
            (politica.DuracaoMaximaEmMinutos is { } minutos ? $"; duração máxima {minutos} min" : string.Empty);
        foreach (var participante in versao.Participantes)
            yield return $"- {participante.Papel}: {participante.Handle}" +
                (participante.PodeEscrever ? " (escrita)" : " (leitura)") +
                (participante.Responsabilidades is null ? string.Empty : $" — {participante.Responsabilidades}");
    }

    private static string ComoAtivar(GrupoDeAgentesDto grupo) => grupo.VersaoAtual is { } versao
        ? $"Para usá-la, confira e ative: /grupo ativar {grupo.Nome} v{versao.Numero} {versao.HashDaConfiguracao[..12]}"
        : string.Empty;

    private static string Roteiro(VersaoDoGrupoDto versao) => versao.Configuracao is not { } configuracao ? "?"
        : string.Join(" → ", configuracao.Roteiro.Select(passo => passo.Papel is null
            ? "aprovação humana" : $"{Passo(passo.Tipo)} ({passo.Papel})"));

    // Opções da linha de comando e participantes nas linhas seguintes. Interpretação só de forma; o caso de uso valida.
    internal static ConfiguracaoDoGrupoDto Criacao(string[] opcoes, string? corpo)
    {
        var lidas = Opcoes(opcoes);
        if (lidas.Roteiro is null) throw new ArgumentException("Informe o roteiro: roteiro=implementar:<papel>,revisar:<papel>,…,aprovar.");
        var participantes = Participantes(corpo);
        if (participantes is null) throw new ArgumentException("Informe os participantes nas linhas seguintes: <papel>=<handle> [escrita].");
        var temCorrecao = lidas.Roteiro.Any(passo => passo.Tipo == TipoDePassoDoRoteiro.Corrigir);
        return new ConfiguracaoDoGrupoDto
        {
            Participantes = participantes,
            Roteiro = lidas.Roteiro,
            RodadasDeRevisaoMaximas = lidas.Rodadas ?? (temCorrecao ? 2 : 0),
            Repositorios = lidas.Repositorios ?? [],
            DuracaoMaximaEmMinutos = lidas.Duracao is 0 ? null : lidas.Duracao
        };
    }

    internal static AlteracaoDoGrupoDto Alteracao(string[] opcoes, string? corpo)
    {
        var lidas = Opcoes(opcoes);
        return new AlteracaoDoGrupoDto
        {
            Participantes = Participantes(corpo),
            Roteiro = lidas.Roteiro,
            RodadasDeRevisaoMaximas = lidas.Rodadas,
            Repositorios = lidas.Repositorios,
            DuracaoMaximaEmMinutos = lidas.Duracao
        };
    }

    private static (IReadOnlyList<PassoDoRoteiro>? Roteiro, int? Rodadas, IReadOnlyList<string>? Repositorios, int? Duracao)
        Opcoes(string[] opcoes)
    {
        IReadOnlyList<PassoDoRoteiro>? roteiro = null;
        int? rodadas = null, duracao = null;
        IReadOnlyList<string>? repositorios = null;
        foreach (var opcao in opcoes)
        {
            var separador = opcao.IndexOf('=');
            if (separador <= 0) throw new ArgumentException($"Opção inválida: {opcao}. {Uso}");
            var (chave, valor) = (opcao[..separador].ToLowerInvariant(), opcao[(separador + 1)..]);
            switch (chave)
            {
                case "roteiro":
                    roteiro = valor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(PassoInformado).ToArray();
                    break;
                case "rodadas":
                    rodadas = Numero(valor, chave);
                    break;
                case "repos":
                    repositorios = valor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "duracao":
                    duracao = Numero(valor, chave);
                    break;
                default:
                    throw new ArgumentException($"Opção desconhecida: {chave}. {Uso}");
            }
        }
        return (roteiro, rodadas, repositorios, duracao);
    }

    private static PassoDoRoteiro PassoInformado(string texto)
    {
        var separador = texto.IndexOf(':');
        var (tipo, papel) = separador < 0 ? (texto, null) : (texto[..separador], texto[(separador + 1)..]);
        return tipo.ToLowerInvariant() switch
        {
            "implementar" => new PassoDoRoteiro(TipoDePassoDoRoteiro.Implementar, papel),
            "revisar" => new PassoDoRoteiro(TipoDePassoDoRoteiro.Revisar, papel),
            "corrigir" => new PassoDoRoteiro(TipoDePassoDoRoteiro.Corrigir, papel),
            "aprovar" or "aprovacao" => new PassoDoRoteiro(TipoDePassoDoRoteiro.AprovacaoHumana, papel),
            _ => throw new ArgumentException($"Passo desconhecido: {tipo}. Use implementar, revisar, corrigir ou aprovar.")
        };
    }

    // <papel>=<handle> [escrita] [: responsabilidades], uma linha por participante. Nulo quando não há linhas.
    private static IReadOnlyList<ParticipanteInformadoDto>? Participantes(string? corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return null;
        var lista = new List<ParticipanteInformadoDto>();
        foreach (var linha in corpo.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var doisPontos = linha.IndexOf(':');
            var cabeca = doisPontos < 0 ? linha : linha[..doisPontos];
            var responsabilidades = doisPontos < 0 ? null : linha[(doisPontos + 1)..].Trim();
            var termos = cabeca.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var igual = termos.Length == 0 ? -1 : termos[0].IndexOf('=');
            if (igual <= 0 || termos.Length > 2 || (termos.Length == 2 && !termos[1].Equals("escrita", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Participante inválido: {linha}. Use <papel>=<handle> [escrita] [: responsabilidades].");
            lista.Add(new ParticipanteInformadoDto(termos[0][..igual], termos[0][(igual + 1)..], termos.Length == 2,
                string.IsNullOrEmpty(responsabilidades) ? null : responsabilidades));
        }
        return lista;
    }

    private static int Numero(string valor, string chave) =>
        int.TryParse(valor, NumberStyles.None, CultureInfo.InvariantCulture, out var numero)
            ? numero : throw new ArgumentException($"{chave}= espera um número inteiro não negativo.");

    private static bool TryVersao(string texto, out int numero)
    {
        numero = 0;
        return texto.Length > 1 && (texto[0] is 'v' or 'V') &&
            int.TryParse(texto[1..], NumberStyles.None, CultureInfo.InvariantCulture, out numero) && numero > 0;
    }

    private async Task<string> ExecutarAsync(Func<ICadastroDeGruposAppService, Task<string>> operacao)
    {
        try
        {
            await using var escopo = escopos.CreateAsyncScope();
            return await operacao(escopo.ServiceProvider.GetRequiredService<ICadastroDeGruposAppService>());
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }
        catch (ConflitoDeConcorrenciaException)
        {
            return "O cadastro mudou ao mesmo tempo por outra operação (ou o nome acabou de ser usado). Confira com /grupos.";
        }
    }

    private static string Passo(TipoDePassoDoRoteiro tipo) => tipo switch
    {
        TipoDePassoDoRoteiro.Implementar => "implementar",
        TipoDePassoDoRoteiro.Revisar => "revisar",
        TipoDePassoDoRoteiro.Corrigir => "corrigir",
        _ => "aprovar"
    };

    private static string Estado(EstadoDoGrupo estado) => estado switch
    {
        EstadoDoGrupo.Desativado => "desativado",
        EstadoDoGrupo.Arquivado => "arquivado",
        _ => "ativo"
    };

    private static string Data(DateTimeOffset quando) =>
        quando.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}
