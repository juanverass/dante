using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dante.Domain.Agentes;
using Dante.Domain.Comum;

namespace Dante.Domain.Execucoes;

// O que a Application informa para criar uma delegação (AD-70, #309). Tudo vem do ledger e do cadastro, nunca do
// agente: a origem e a etapa em curso, as versões congeladas, a cadeia de agentes da raiz até o solicitante, os
// repositórios do contexto congelado da origem e as contagens já gravadas. Do agente vêm só o destinatário (resolvido
// pela Application a partir de um handle), o contrato (do catálogo fechado), a entrada (validada pelo contrato), o
// objetivo (já redigido pela Application, com o original só para o hash) e os critérios.
public sealed record PedidoDeDelegacao(
    Execucao Origem,
    EtapaDaExecucao EtapaDeOrigem,
    Agente Solicitante,
    VersaoDoAgente VersaoSolicitante,
    Agente Destinatario,
    VersaoDoAgente VersaoDestinataria,
    IReadOnlyList<Guid> CadeiaDeAgentes,
    IReadOnlyList<string> RepositoriosDaOrigem,
    int DelegacoesDaOrigem,
    int DelegacoesDaRaiz,
    string Contrato,
    string Entrada,
    string ObjetivoOriginal,
    string ObjetivoRedigido,
    bool ObjetivoAlteradoPelaRedacao,
    string? Criterios = null,
    int? MinutosParaIniciar = null,
    int? MinutosDeDuracao = null,
    bool Escrita = false);

public sealed record NovaDelegacao(Delegacao Delegacao, Execucao Subordinada);

// Delegação rastreada (AD-70, #309): uma linha ligada a uma execução subordinada (tipo Delegada), nunca uma mensagem.
// Criada só por Criar, que aplica todas as regras por interseção (nada é ampliado: o destinatário recebe o mínimo entre
// o que o solicitante pode, o que ele próprio pode e o teto global). Nesta etapa é só de leitura: Escrita é sempre
// falso, e o banco também o exige. A identidade da unidade de trabalho é a chave de deduplicação (raiz, etapa de
// origem, contrato, objetivo normalizado e entrada canônica), sem a tentativa e sem o destinatário, e vale em qualquer
// estado: repetir o pedido devolve a mesma delegação (mesmo terminal), e a mesma unidade para outro agente é recusada.
// O resultado, uma vez gravado, não muda (método único, estado terminal; o banco também o protege).
public sealed class Delegacao : EntidadeBase
{
    public const int TamanhoDoHash = 64;
    public const int TamanhoMaximoDoContrato = 40;
    public const int TamanhoMaximoDosCriterios = 2000;
    public const int TamanhoMaximoDoHandle = Agente.TamanhoMaximoDoHandle;
    public const int TamanhoMaximoDoMotivo = Execucao.TamanhoMaximoDoTexto;

    private static readonly Regex Sha256 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Espacos = new(@"\s+", RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<EstadoDaDelegacao, EstadoDaDelegacao[]> Transicoes =
        new Dictionary<EstadoDaDelegacao, EstadoDaDelegacao[]>
        {
            [EstadoDaDelegacao.Solicitada] =
                [EstadoDaDelegacao.Aceita, EstadoDaDelegacao.Recusada, EstadoDaDelegacao.Cancelada],
            [EstadoDaDelegacao.Aceita] =
            [
                EstadoDaDelegacao.EmAndamento, EstadoDaDelegacao.Falhou, EstadoDaDelegacao.Expirada,
                EstadoDaDelegacao.Cancelada
            ],
            [EstadoDaDelegacao.EmAndamento] =
            [
                EstadoDaDelegacao.Respondida, EstadoDaDelegacao.Falhou, EstadoDaDelegacao.Expirada,
                EstadoDaDelegacao.Cancelada
            ],
            [EstadoDaDelegacao.Respondida] = [],
            [EstadoDaDelegacao.Falhou] = [],
            [EstadoDaDelegacao.Expirada] = [],
            [EstadoDaDelegacao.Cancelada] = [],
            [EstadoDaDelegacao.Recusada] = []
        };

    private Delegacao()
    {
    }

    public Guid IdExecucaoOrigem { get; private set; }
    public Guid IdEtapaOrigem { get; private set; }
    public Guid IdExecucaoSubordinada { get; private set; }
    public Guid IdExecucaoRaiz { get; private set; }
    public Guid IdAgenteSolicitante { get; private set; }
    public Guid IdVersaoSolicitante { get; private set; }
    public string HashDaVersaoSolicitante { get; private set; } = "";
    public Guid IdAgenteDestinatario { get; private set; }
    public string HandleDoDestinatario { get; private set; } = "";
    public Guid IdVersaoDestinataria { get; private set; }
    public string HashDaVersaoDestinataria { get; private set; } = "";
    // Identificador do contrato no catálogo fechado (consulta.v1, revisao.v1).
    public string Contrato { get; private set; } = "";
    // JSON canônico da entrada, validado pelo contrato.
    public string Entrada { get; private set; } = "";
    public string HashDaEntrada { get; private set; } = "";
    // O objetivo bruto nunca é gravado: o texto redigido, se a redação o alterou e o hash do objetivo normalizado.
    public string ObjetivoRedigido { get; private set; } = "";
    public bool ObjetivoAlteradoPelaRedacao { get; private set; }
    public string HashDoObjetivo { get; private set; } = "";
    public string? Criterios { get; private set; }
    public int MinutosParaIniciar { get; private set; }
    public int MinutosDeDuracao { get; private set; }
    public bool Escrita { get; private set; }
    // Agentes da raiz até o solicitante (inclusive), na ordem da hierarquia; o destinatário nunca está nela.
    public IReadOnlyList<Guid> CadeiaDeAgentes { get; private set; } = [];
    public string ChaveDeDeduplicacao { get; private set; } = "";
    public EstadoDaDelegacao Estado { get; private set; } = EstadoDaDelegacao.Solicitada;
    public string? Motivo { get; private set; }
    // Resultado: JSON canônico validado pelo contrato, com hash, autor (a versão destinatária) e sessão que o entregou.
    public string? Resultado { get; private set; }
    public string? HashDoResultado { get; private set; }
    public Guid? IdVersaoDoAutorDoResultado { get; private set; }
    public Guid? IdSessaoDoResultado { get; private set; }
    // Hash do artefato que o destinatário leu (o diff de uma worktree, por exemplo), quando houver um.
    public string? HashDoArtefatoLido { get; private set; }
    public DateTimeOffset CriadaEm { get; private set; }
    public DateTimeOffset? AceitaEm { get; private set; }
    public DateTimeOffset? IniciadaEm { get; private set; }
    public DateTimeOffset? EncerradaEm { get; private set; }

    public bool Terminal => Transicoes[Estado].Length == 0;

    public static bool PodeTransitar(EstadoDaDelegacao de, EstadoDaDelegacao para) => Transicoes[de].Contains(para);

    public static bool EstadoTerminal(EstadoDaDelegacao estado) => Transicoes[estado].Length == 0;

    // Valida o pedido inteiro e cria a delegação e a execução subordinada (Delegada, Pendente), ainda não gravadas.
    // Qualquer regra violada lança DelegacaoRecusadaException com o motivo, sem criar nada.
    public static NovaDelegacao Criar(PedidoDeDelegacao pedido, long numeroDaSubordinada, DateTimeOffset criadaEm)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        var origem = pedido.Origem;
        var etapa = pedido.EtapaDeOrigem;
        var solicitante = pedido.VersaoSolicitante.Configuracao;
        var destinataria = pedido.VersaoDestinataria.Configuracao;

        // Origem: a etapa de leitura em curso de uma Tarefa (a raiz) ou de uma Delegada; nunca o job legado nem um
        // passo de roteiro (a integração com grupos é de outra entrega), e nunca uma etapa que escreve.
        if (etapa.IdExecucao != origem.Id || origem.Estado != EstadoDaExecucao.EmExecucao ||
            origem.Tipo is not (TipoDeExecucao.Tarefa or TipoDeExecucao.Delegada) ||
            etapa.Estado != EstadoDaEtapa.EmExecucao || etapa.Tipo != TipoDeEtapa.TurnoDeAgente || etapa.Escrita)
            throw Recusa(MotivoDaRecusaDeDelegacao.OrigemInvalida,
                "Só a etapa de leitura em curso de uma tarefa pode delegar.");
        if (pedido.VersaoSolicitante.IdAgente != pedido.Solicitante.Id ||
            origem.IdVersaoDoAgente != pedido.VersaoSolicitante.Id || !pedido.VersaoSolicitante.Integra)
            throw Recusa(MotivoDaRecusaDeDelegacao.OrigemInvalida,
                "O solicitante não é o agente congelado na execução de origem.");
        if (pedido.Escrita)
            throw Recusa(MotivoDaRecusaDeDelegacao.EscritaNaoAutorizada, "Delegação com escrita não é permitida.");
        if (!ContratosDeDelegacao.TentarObter(pedido.Contrato, out var contrato))
            throw Recusa(MotivoDaRecusaDeDelegacao.ContratoInvalido, "Contrato de delegação desconhecido.");

        // Hierarquia: o destinatário não é o solicitante nem nenhum agente acima dele na cadeia.
        if (pedido.Destinatario.Id == pedido.Solicitante.Id)
            throw Recusa(MotivoDaRecusaDeDelegacao.Autodelegacao, "Um agente não delega a si mesmo.");
        var cadeia = pedido.CadeiaDeAgentes;
        if (cadeia.Count != origem.Profundidade + 1 || cadeia[^1] != pedido.Solicitante.Id)
            throw Recusa(MotivoDaRecusaDeDelegacao.OrigemInvalida, "A cadeia de agentes não corresponde à origem.");
        if (cadeia.Contains(pedido.Destinatario.Id))
            throw Recusa(MotivoDaRecusaDeDelegacao.Ciclo,
                $"O agente {pedido.Destinatario.Handle} já está na cadeia desta execução.");
        var limites = solicitante.LimitesEfetivosDeDelegacao;
        if (origem.Profundidade + 1 > limites.ProfundidadeMaxima)
            throw Recusa(MotivoDaRecusaDeDelegacao.ProfundidadeExcedida,
                $"Profundidade máxima de delegação ({limites.ProfundidadeMaxima}) atingida.");

        // Autorização por interseção: concessão do solicitante, destinatário ativo e íntegro, capacidade e repositórios.
        if (!solicitante.PodeDelegarPara(pedido.Destinatario.Handle))
            throw Recusa(MotivoDaRecusaDeDelegacao.DestinatarioNaoAutorizado,
                $"O agente {pedido.Destinatario.Handle} não está entre os que este agente pode consultar.");
        if (pedido.Destinatario.Estado != EstadoDoAgente.Ativo ||
            pedido.VersaoDestinataria.IdAgente != pedido.Destinatario.Id ||
            pedido.Destinatario.IdVersaoAtual != pedido.VersaoDestinataria.Id || !pedido.VersaoDestinataria.Integra)
            throw Recusa(MotivoDaRecusaDeDelegacao.DestinatarioIndisponivel,
                $"O agente {pedido.Destinatario.Handle} não está ativo com uma versão íntegra.");
        if (!contrato.Aceita(destinataria.Capacidades))
            throw Recusa(MotivoDaRecusaDeDelegacao.CapacidadeInsuficiente,
                $"O agente {pedido.Destinatario.Handle} não tem capacidade para {contrato.Identificador}.");
        foreach (var alias in pedido.RepositoriosDaOrigem)
            if (!solicitante.PermiteRepositorio(alias) || !destinataria.PermiteRepositorio(alias))
                throw Recusa(MotivoDaRecusaDeDelegacao.RepositorioForaDaIntersecao,
                    $"O repositório {alias} não é permitido aos dois agentes.");

        // Volume: filhas da origem (fan-out, limite do solicitante) e da árvore inteira (orçamento, teto global).
        if (pedido.DelegacoesDaOrigem < 0 || pedido.DelegacoesDaRaiz < pedido.DelegacoesDaOrigem)
            throw new ArgumentOutOfRangeException(nameof(pedido), "Contagem de delegações inválida.");
        if (pedido.DelegacoesDaOrigem >= limites.DelegacoesPorExecucao)
            throw Recusa(MotivoDaRecusaDeDelegacao.FanOutExcedido,
                $"Esta execução já delegou o máximo de {limites.DelegacoesPorExecucao} vezes.");
        if (pedido.DelegacoesDaRaiz >= LimitesDeDelegacao.Teto.DelegacoesPorExecucao)
            throw Recusa(MotivoDaRecusaDeDelegacao.OrcamentoExcedido,
                $"A execução raiz já tem o máximo de {LimitesDeDelegacao.Teto.DelegacoesPorExecucao} delegações.");
        var minutosParaIniciar = pedido.MinutosParaIniciar ?? limites.MinutosParaIniciar;
        var minutosDeDuracao = pedido.MinutosDeDuracao ?? limites.MinutosDeDuracao;
        if (minutosParaIniciar < 1 || minutosParaIniciar > limites.MinutosParaIniciar ||
            minutosDeDuracao < 1 || minutosDeDuracao > limites.MinutosDeDuracao)
            throw Recusa(MotivoDaRecusaDeDelegacao.LimitesExcedidos,
                $"Prazo até {limites.MinutosParaIniciar} min para iniciar e duração até {limites.MinutosDeDuracao} min.");

        // Conteúdo: contrato, objetivo e critérios.
        string entrada;
        try
        {
            entrada = contrato.CanonizarEntrada(pedido.Entrada);
        }
        catch (ArgumentException exception)
        {
            throw Recusa(MotivoDaRecusaDeDelegacao.ContratoInvalido, exception.Message);
        }
        var normalizado = NormalizarObjetivo(pedido.ObjetivoOriginal);
        var redigido = pedido.ObjetivoRedigido?.Trim() ?? "";
        if (normalizado.Length == 0 || redigido.Length == 0 || redigido.Length > Execucao.TamanhoMaximoDoObjetivo)
            throw Recusa(MotivoDaRecusaDeDelegacao.ContratoInvalido,
                $"Objetivo obrigatório, com até {Execucao.TamanhoMaximoDoObjetivo} caracteres.");
        var criterios = string.IsNullOrWhiteSpace(pedido.Criterios) ? null : pedido.Criterios.Trim();
        if (criterios is { Length: > TamanhoMaximoDosCriterios })
            throw Recusa(MotivoDaRecusaDeDelegacao.ContratoInvalido,
                $"Critérios de conclusão com até {TamanhoMaximoDosCriterios} caracteres.");

        var subordinada = Execucao.Delegada(origem, numeroDaSubordinada, pedido.VersaoDestinataria.Id, redigido, criadaEm);
        var hashDaEntrada = ContratoDeDelegacao.Hash(entrada);
        var hashDoObjetivo = HashDoObjetivoNormalizado(origem.IdExecucaoRaiz, normalizado);
        var delegacao = new Delegacao
        {
            IdExecucaoOrigem = origem.Id,
            IdEtapaOrigem = etapa.Id,
            IdExecucaoSubordinada = subordinada.Id,
            IdExecucaoRaiz = origem.IdExecucaoRaiz,
            IdAgenteSolicitante = pedido.Solicitante.Id,
            IdVersaoSolicitante = pedido.VersaoSolicitante.Id,
            HashDaVersaoSolicitante = pedido.VersaoSolicitante.HashDaConfiguracao,
            IdAgenteDestinatario = pedido.Destinatario.Id,
            HandleDoDestinatario = pedido.Destinatario.Handle,
            IdVersaoDestinataria = pedido.VersaoDestinataria.Id,
            HashDaVersaoDestinataria = pedido.VersaoDestinataria.HashDaConfiguracao,
            Contrato = contrato.Identificador,
            Entrada = entrada,
            HashDaEntrada = hashDaEntrada,
            ObjetivoRedigido = redigido,
            ObjetivoAlteradoPelaRedacao = pedido.ObjetivoAlteradoPelaRedacao,
            HashDoObjetivo = hashDoObjetivo,
            Criterios = criterios,
            MinutosParaIniciar = minutosParaIniciar,
            MinutosDeDuracao = minutosDeDuracao,
            Escrita = false,
            CadeiaDeAgentes = Array.AsReadOnly(cadeia.ToArray()),
            ChaveDeDeduplicacao = CalcularChave(origem.IdExecucaoRaiz, etapa.Numero, contrato.Identificador,
                hashDoObjetivo, hashDaEntrada),
            CriadaEm = criadaEm
        };
        return new NovaDelegacao(delegacao, subordinada);
    }

    // Objetivo normalizado para a identidade da unidade: forma Unicode NFC, espaços colapsados, sem pontas, minúsculas.
    public static string NormalizarObjetivo(string? objetivo) =>
        string.IsNullOrWhiteSpace(objetivo) ? ""
            : Espacos.Replace(objetivo.Normalize(NormalizationForm.FormC), " ").Trim().ToLowerInvariant();

    public static string HashDoObjetivoNormalizado(Guid idExecucaoRaiz, string objetivoNormalizado) =>
        Sha(idExecucaoRaiz.ToString("N", CultureInfo.InvariantCulture) + "\n" + objetivoNormalizado);

    // Identidade da unidade de trabalho, sem a tentativa (uma retomada da origem acha a mesma delegação) e sem o
    // destinatário (a mesma unidade nunca vai a dois agentes).
    public static string CalcularChave(Guid idExecucaoRaiz, int numeroDaEtapaDeOrigem, string contrato,
        string hashDoObjetivo, string hashDaEntrada) =>
        Sha("delegacao/1\n" + idExecucaoRaiz.ToString("N", CultureInfo.InvariantCulture) + "\n" +
            numeroDaEtapaDeOrigem.ToString(CultureInfo.InvariantCulture) + "\n" + contrato + "\n" + hashDoObjetivo + "\n" +
            hashDaEntrada);

    // Outro pedido com a mesma chave, em qualquer estado desta delegação (inclusive terminal): para o mesmo agente é a
    // mesma delegação, devolvida como está (estado e resultado originais; nada é reaberto nem alterado); para outro
    // agente, a unidade já está atribuída e o pedido é recusado. Reexecutar ou reatribuir será uma operação explícita,
    // auditada e com identidade própria, nunca efeito de a delegação ter terminado.
    public Delegacao ConferirRepeticao(Guid idAgenteDestinatario)
    {
        if (idAgenteDestinatario != IdAgenteDestinatario)
            throw Recusa(MotivoDaRecusaDeDelegacao.UnidadeJaAtribuida,
                $"Esta unidade de trabalho já foi delegada a {HandleDoDestinatario}.");
        return this;
    }

    // Enfileirada: validação e trabalho da fila gravados. Não é o início da execução.
    public void Aceitar(DateTimeOffset quando)
    {
        Transitar(EstadoDaDelegacao.Aceita);
        AceitaEm = quando;
    }

    // A execução subordinada começou.
    public void Iniciar(DateTimeOffset quando)
    {
        Transitar(EstadoDaDelegacao.EmAndamento);
        IniciadaEm = quando;
    }

    // Resultado do destinatário, validado pelo contrato. Só o autor congelado (a versão destinatária) responde, uma vez:
    // depois disso a delegação é terminal e o resultado não muda. Na revisão, o artefato lido é o da entrada.
    public void Responder(string resultado, Guid idVersaoDoAutor, Guid? idSessao, string? hashDoArtefatoLido,
        DateTimeOffset quando)
    {
        if (!PodeTransitar(Estado, EstadoDaDelegacao.Respondida)) throw Invalida(EstadoDaDelegacao.Respondida);
        if (idVersaoDoAutor != IdVersaoDestinataria)
            throw new InvalidOperationException("Só a versão destinatária responde a delegação.");
        if (idSessao == Guid.Empty) throw new ArgumentException("Sessão inválida.", nameof(idSessao));
        if (hashDoArtefatoLido is not null && !Sha256.IsMatch(hashDoArtefatoLido))
            throw new ArgumentException("Hash do artefato lido inválido.", nameof(hashDoArtefatoLido));
        ContratosDeDelegacao.TentarObter(Contrato, out var contrato);
        var canonico = contrato.CanonizarSaida(resultado);
        if (Contrato == ContratosDeDelegacao.Revisao.Identificador &&
            hashDoArtefatoLido != ContratoDeDelegacao.Texto(Entrada, "artefato"))
            throw new InvalidOperationException("A revisão vale só para o artefato pedido.");
        Transitar(EstadoDaDelegacao.Respondida);
        Resultado = canonico;
        HashDoResultado = ContratoDeDelegacao.Hash(canonico);
        IdVersaoDoAutorDoResultado = idVersaoDoAutor;
        IdSessaoDoResultado = idSessao;
        HashDoArtefatoLido = hashDoArtefatoLido;
        EncerradaEm = quando;
    }

    // Entrega repetida: verdadeiro quando o conteúdo é o mesmo já gravado (idempotente); outro conteúdo, falso.
    public bool MesmoResultado(string resultado)
    {
        if (Resultado is null) return false;
        ContratosDeDelegacao.TentarObter(Contrato, out var contrato);
        try
        {
            return ContratoDeDelegacao.Hash(contrato.CanonizarSaida(resultado)) == HashDoResultado;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Falhar(string motivo, DateTimeOffset quando) => Encerrar(EstadoDaDelegacao.Falhou, motivo, quando);

    public void Expirar(string motivo, DateTimeOffset quando) => Encerrar(EstadoDaDelegacao.Expirada, motivo, quando);

    public void Cancelar(string motivo, DateTimeOffset quando) => Encerrar(EstadoDaDelegacao.Cancelada, motivo, quando);

    public void Recusar(string motivo, DateTimeOffset quando) => Encerrar(EstadoDaDelegacao.Recusada, motivo, quando);

    private void Encerrar(EstadoDaDelegacao para, string motivo, DateTimeOffset quando)
    {
        var texto = Execucao.TextoCortado(motivo, TamanhoMaximoDoMotivo, nameof(motivo));
        Transitar(para);
        Motivo = texto;
        EncerradaEm = quando;
    }

    private void Transitar(EstadoDaDelegacao para)
    {
        if (!PodeTransitar(Estado, para)) throw Invalida(para);
        Estado = para;
    }

    private TransicaoInvalidaException Invalida(EstadoDaDelegacao para) =>
        new("delegação", Estado.ToString(), para.ToString());

    private static DelegacaoRecusadaException Recusa(MotivoDaRecusaDeDelegacao motivo, string mensagem) => new(motivo, mensagem);

    private static string Sha(string texto) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(texto)));
}
