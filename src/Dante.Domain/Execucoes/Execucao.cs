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
    public Execucao(long numero, TipoDeExecucao tipo, string canal, long idDoUsuarioNoCanal, string contexto,
        string? objetivo, DateTimeOffset criadaEm)
    {
        if (numero < 1) throw new ArgumentOutOfRangeException(nameof(numero), "Número da execução deve ser positivo.");
        if (!Enum.IsDefined(tipo)) throw new ArgumentOutOfRangeException(nameof(tipo), "Tipo de execução inválido.");
        Numero = numero;
        Tipo = tipo;
        Canal = TextoObrigatorio(canal, TamanhoMaximoDoCanal, nameof(canal));
        IdDoUsuarioNoCanal = idDoUsuarioNoCanal;
        Contexto = TextoObrigatorio(contexto, TamanhoMaximoDoContexto, nameof(contexto));
        if (objetivo is { Length: > TamanhoMaximoDoObjetivo })
            throw new ArgumentException("Objetivo da execução excede o tamanho máximo.", nameof(objetivo));
        Objetivo = string.IsNullOrWhiteSpace(objetivo) ? null : objetivo;
        CriadaEm = criadaEm;
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

    public string Codigo => Tipo == TipoDeExecucao.Avulsa ? $"J{Numero:D6}" : $"E{Numero}";
    public bool Terminal => Transicoes[Estado].Length == 0;

    public static bool PodeTransitar(EstadoDaExecucao de, EstadoDaExecucao para) => Transicoes[de].Contains(para);

    public void Iniciar(DateTimeOffset quando)
    {
        Transitar(EstadoDaExecucao.EmExecucao);
        IniciadaEm ??= quando;
    }

    public void AguardarAprovacao() => Transitar(EstadoDaExecucao.AguardandoAprovacao);

    public void AguardarEntrada() => Transitar(EstadoDaExecucao.AguardandoEntrada);

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
