using Dante.Domain.Comum;

namespace Dante.Domain.Execucoes;

// Unidade de trabalho persistente do ledger (AD-66): distinta de sessão e de turno. O número vem de sequência do
// banco e não se repete entre reinícios; o código exibido é J… para o job legado (Avulsa) e E… para os demais.
// Toda mudança de estado passa por um método que recusa a origem inválida; a gravação usa xmin (AD-43).
public sealed class Execucao : EntidadeBase
{
    public const int TamanhoMaximoDoCanal = 32;
    public const int TamanhoMaximoDoContexto = 8000;
    public const int TamanhoMaximoDoObjetivo = 2000;
    public const int TamanhoMaximoDoTexto = 1000;

    private static readonly IReadOnlyDictionary<EstadoDaExecucao, EstadoDaExecucao[]> Transicoes =
        new Dictionary<EstadoDaExecucao, EstadoDaExecucao[]>
        {
            [EstadoDaExecucao.Pendente] = [EstadoDaExecucao.EmExecucao, EstadoDaExecucao.Cancelada],
            [EstadoDaExecucao.EmExecucao] =
            [
                EstadoDaExecucao.AguardandoAprovacao, EstadoDaExecucao.AguardandoEntrada,
                EstadoDaExecucao.AguardandoDelegacoes, EstadoDaExecucao.Bloqueada, EstadoDaExecucao.Interrompida,
                EstadoDaExecucao.Pendente, EstadoDaExecucao.Concluida, EstadoDaExecucao.Falhou, EstadoDaExecucao.Cancelada
            ],
            [EstadoDaExecucao.AguardandoAprovacao] = [EstadoDaExecucao.Pendente, EstadoDaExecucao.Cancelada],
            [EstadoDaExecucao.AguardandoEntrada] = [EstadoDaExecucao.Pendente, EstadoDaExecucao.Cancelada],
            [EstadoDaExecucao.AguardandoDelegacoes] = [EstadoDaExecucao.Pendente, EstadoDaExecucao.Cancelada],
            [EstadoDaExecucao.Bloqueada] = [EstadoDaExecucao.Pendente, EstadoDaExecucao.Cancelada],
            [EstadoDaExecucao.Interrompida] = [EstadoDaExecucao.Pendente, EstadoDaExecucao.Cancelada],
            [EstadoDaExecucao.Concluida] = [],
            [EstadoDaExecucao.Falhou] = [],
            [EstadoDaExecucao.Cancelada] = []
        };

    // Materialização pelo provider.
    private Execucao()
    {
    }

    // contexto: o contexto de execução congelado (AD-11) já serializado pela Application.
    // idVersaoDoAgente: versão congelada do agente responsável (AD-68); obrigatória numa Tarefa.
    // idVersaoDoGrupo: versão ativa do grupo, congelada (AD-69); obrigatória num Roteiro, que não tem agente único.
    // Coordenada (AD-75): versão do grupo e versão congelada do coordenador, as duas obrigatórias.
    public Execucao(long numero, TipoDeExecucao tipo, string canal, long idDoUsuarioNoCanal, string contexto,
        string? objetivo, DateTimeOffset criadaEm, Guid? idVersaoDoAgente = null, Guid? idVersaoDoGrupo = null)
    {
        if (numero < 1) throw new ArgumentOutOfRangeException(nameof(numero), "Número da execução deve ser positivo.");
        if (!Enum.IsDefined(tipo)) throw new ArgumentOutOfRangeException(nameof(tipo), "Tipo de execução inválido.");
        if (idVersaoDoAgente == Guid.Empty) throw new ArgumentException("Versão do agente inválida.", nameof(idVersaoDoAgente));
        if (tipo == TipoDeExecucao.Tarefa && idVersaoDoAgente is null)
            throw new ArgumentException("Tarefa exige a versão do agente responsável.", nameof(idVersaoDoAgente));
        if (idVersaoDoGrupo == Guid.Empty) throw new ArgumentException("Versão do grupo inválida.", nameof(idVersaoDoGrupo));
        if ((tipo is TipoDeExecucao.Roteiro or TipoDeExecucao.Coordenada) != (idVersaoDoGrupo is not null))
            throw new ArgumentException("Só o roteiro e a coordenação apontam para a versão de um grupo, e sempre apontam.",
                nameof(idVersaoDoGrupo));
        if (tipo == TipoDeExecucao.Coordenada && idVersaoDoAgente is null)
            throw new ArgumentException("Coordenação exige a versão congelada do coordenador.", nameof(idVersaoDoAgente));
        if (tipo == TipoDeExecucao.Roteiro && idVersaoDoAgente is not null)
            throw new ArgumentException("Roteiro congela a versão de cada participante, não de um agente único.",
                nameof(idVersaoDoAgente));
        // A execução delegada nasce só de uma delegação (Delegada), filha de outra; por aqui só nascem raízes.
        if (tipo == TipoDeExecucao.Delegada)
            throw new ArgumentException("Execução delegada nasce só de uma delegação.", nameof(tipo));
        IdVersaoDoAgente = idVersaoDoAgente;
        IdVersaoDoGrupo = idVersaoDoGrupo;
        Numero = numero;
        Tipo = tipo;
        Canal = TextoObrigatorio(canal, TamanhoMaximoDoCanal, nameof(canal));
        IdDoUsuarioNoCanal = idDoUsuarioNoCanal;
        Contexto = TextoObrigatorio(contexto, TamanhoMaximoDoContexto, nameof(contexto));
        if (objetivo is { Length: > TamanhoMaximoDoObjetivo })
            throw new ArgumentException("Objetivo da execução excede o tamanho máximo.", nameof(objetivo));
        Objetivo = string.IsNullOrWhiteSpace(objetivo) ? null : objetivo;
        CriadaEm = criadaEm;
        IdExecucaoRaiz = Id;
    }

    // Execução subordinada de uma delegação (AD-70, #309), criada pela própria Delegacao junto com a linha dela. Herda
    // da origem a autoridade (canal e pessoa) e o contexto congelado: quem delega nunca escolhe repositório, diretório
    // nem dono. A raiz é a da origem e a profundidade, a dela mais um, dentro do teto global; a hierarquia é fixada aqui e
    // não muda depois (o banco também recusa alterá-la), então um ciclo na árvore é impossível por construção.
    internal static Execucao Delegada(Execucao origem, long numero, Guid idVersaoDoAgente, string? objetivo,
        DateTimeOffset criadaEm)
    {
        ArgumentNullException.ThrowIfNull(origem);
        if (origem.Terminal) throw new InvalidOperationException("Execução encerrada não origina delegação.");
        if (origem.Profundidade + 1 > Dante.Domain.Agentes.LimitesDeDelegacao.Teto.ProfundidadeMaxima)
            throw new InvalidOperationException("Profundidade de delegação acima do teto global.");
        var filha = new Execucao(numero, TipoDeExecucao.Tarefa, origem.Canal, origem.IdDoUsuarioNoCanal, origem.Contexto,
            objetivo, criadaEm, idVersaoDoAgente)
        {
            Tipo = TipoDeExecucao.Delegada,
            IdExecucaoPai = origem.Id,
            Profundidade = origem.Profundidade + 1
        };
        filha.IdExecucaoRaiz = origem.IdExecucaoRaiz;
        return filha;
    }

    public long Numero { get; private set; }
    public TipoDeExecucao Tipo { get; private set; }
    public EstadoDaExecucao Estado { get; private set; } = EstadoDaExecucao.Pendente;
    // Autoridade de origem: o canal que recebeu o pedido e a pessoa nele.
    public string Canal { get; private set; } = "";
    public long IdDoUsuarioNoCanal { get; private set; }
    public string Contexto { get; private set; } = "";
    // Nulo quando o pedido não é registrado (o job legado não persiste o texto do usuário).
    public string? Objetivo { get; private set; }
    public string? Erro { get; private set; }
    // Motivo do estado atual quando ele exige um (bloqueio, interrupção, cancelamento).
    public string? Motivo { get; private set; }
    public DateTimeOffset CriadaEm { get; private set; }
    public DateTimeOffset? IniciadaEm { get; private set; }
    public DateTimeOffset? EncerradaEm { get; private set; }

    // Versão do agente responsável, congelada na criação; editar o agente depois não a altera.
    public Guid? IdVersaoDoAgente { get; private set; }

    // Versão do grupo congelada na criação de um Roteiro (AD-69) ou de uma Coordenada (AD-75); ativar outra versão depois
    // não a altera.
    public Guid? IdVersaoDoGrupo { get; private set; }

    // Hierarquia (AD-70, #309): a raiz aponta para si mesma, não tem pai e tem profundidade 0; uma delegada aponta para
    // a execução de origem (pai) e para a raiz dela. Imutáveis depois da criação.
    public Guid? IdExecucaoPai { get; private set; }
    public Guid IdExecucaoRaiz { get; private set; }
    public int Profundidade { get; private set; }

    public bool Raiz => IdExecucaoPai is null;

    public string Codigo => Tipo == TipoDeExecucao.Avulsa ? $"J{Numero:D6}" : $"E{Numero}";
    public bool Terminal => Transicoes[Estado].Length == 0;
    // Execuções novas (tudo que não é o job legado) não escrevem fora de uma worktree reservada (AD-68, AD-71 proposta).
    public bool Nova => Tipo != TipoDeExecucao.Avulsa;

    public static bool PodeTransitar(EstadoDaExecucao de, EstadoDaExecucao para) => Transicoes[de].Contains(para);

    // Código exibido → número e tipo: J… é sempre o job legado (Avulsa) e E… uma execução nova. Zeros à esquerda são
    // aceitos (J000123), maiúsculas ou minúsculas. Falso para qualquer outra forma.
    public static bool TentarInterpretarCodigo(string? codigo, out long numero, out bool avulsa)
    {
        numero = 0;
        avulsa = false;
        if (string.IsNullOrWhiteSpace(codigo) || codigo.Length is < 2 or > 20) return false;
        var prefixo = char.ToUpperInvariant(codigo[0]);
        if (prefixo is not ('J' or 'E')) return false;
        var digitos = codigo.AsSpan(1);
        foreach (var c in digitos)
            if (c is < '0' or > '9') return false;
        if (!long.TryParse(digitos, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out numero) || numero < 1) return false;
        avulsa = prefixo == 'J';
        return true;
    }

    public void Iniciar(DateTimeOffset quando)
    {
        Transitar(EstadoDaExecucao.EmExecucao);
        IniciadaEm ??= quando;
    }

    // motivo: o que se espera da pessoa (exibido no acompanhamento).
    public void AguardarAprovacao(string? motivo = null) =>
        Transitar(EstadoDaExecucao.AguardandoAprovacao, TextoOpcional(motivo));

    public void AguardarEntrada(string? motivo = null) =>
        Transitar(EstadoDaExecucao.AguardandoEntrada, TextoOpcional(motivo));

    public void AguardarDelegacoes() => Transitar(EstadoDaExecucao.AguardandoDelegacoes);

    public void Bloquear(string motivo) => Transitar(EstadoDaExecucao.Bloqueada, TextoCortado(motivo, nameof(motivo)));

    public void Interromper(string motivo) =>
        Transitar(EstadoDaExecucao.Interrompida, TextoCortado(motivo, nameof(motivo)));

    // Etapa retentável reprogramada: a execução volta à fila sem falhar.
    public void Reprogramar() => Transitar(EstadoDaExecucao.Pendente);

    // Saída de uma espera, de um bloqueio ou de uma interrupção.
    public void Retomar()
    {
        if (Estado == EstadoDaExecucao.EmExecucao) throw Invalida(EstadoDaExecucao.Pendente);
        Transitar(EstadoDaExecucao.Pendente);
    }

    public void Concluir(DateTimeOffset quando)
    {
        Transitar(EstadoDaExecucao.Concluida);
        EncerradaEm = quando;
    }

    public void Falhar(string erro, DateTimeOffset quando)
    {
        var texto = TextoCortado(erro, nameof(erro));
        Transitar(EstadoDaExecucao.Falhou);
        Erro = texto;
        EncerradaEm = quando;
    }

    public void Cancelar(string? motivo, DateTimeOffset quando)
    {
        Transitar(EstadoDaExecucao.Cancelada, TextoOpcional(motivo));
        EncerradaEm = quando;
    }

    private void Transitar(EstadoDaExecucao para, string? motivo = null)
    {
        if (!PodeTransitar(Estado, para)) throw Invalida(para);
        Estado = para;
        Motivo = motivo;
    }

    private TransicaoInvalidaException Invalida(EstadoDaExecucao para) =>
        new("execução", Estado.ToString(), para.ToString());

    internal static string TextoObrigatorio(string valor, int tamanhoMaximo, string parametro)
    {
        if (string.IsNullOrWhiteSpace(valor)) throw new ArgumentException("Texto obrigatório.", parametro);
        if (valor.Length > tamanhoMaximo) throw new ArgumentException("Texto excede o tamanho máximo.", parametro);
        return valor;
    }

    internal static string TextoCortado(string valor, int tamanhoMaximo, string parametro) =>
        string.IsNullOrWhiteSpace(valor) ? throw new ArgumentException("Texto obrigatório.", parametro)
            : valor.Length <= tamanhoMaximo ? valor : valor[..tamanhoMaximo];

    private static string TextoCortado(string valor, string parametro) =>
        TextoCortado(valor, TamanhoMaximoDoTexto, parametro);

    // Textos vindos de fora (erros de processo, motivos) são cortados em vez de recusar o registro.
    internal static string? TextoOpcional(string? valor, int tamanhoMaximo = TamanhoMaximoDoTexto) =>
        string.IsNullOrWhiteSpace(valor) ? null : valor.Length <= tamanhoMaximo ? valor : valor[..tamanhoMaximo];
}
