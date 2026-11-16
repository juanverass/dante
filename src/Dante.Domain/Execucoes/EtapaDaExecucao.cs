using Dante.Domain.Agentes;
using Dante.Domain.Comum;

namespace Dante.Domain.Execucoes;

// Parte de uma execução: um processo avulso ou turnos numa sessão de agente. Uma etapa pode levar vários turnos;
// o vínculo com a sessão e o turno que a executam fica aqui. A chave de idempotência (execução:número:tentativa) é
// única no banco e impede dupla conclusão de uma mesma tentativa.
public sealed class EtapaDaExecucao : EntidadeBase
{
    public const int TamanhoMaximoDoModelo = 200;
    public const int TamanhoMaximoDoTurno = 32;

    private static readonly IReadOnlyDictionary<EstadoDaEtapa, EstadoDaEtapa[]> Transicoes =
        new Dictionary<EstadoDaEtapa, EstadoDaEtapa[]>
        {
            [EstadoDaEtapa.Pendente] = [EstadoDaEtapa.Pronta, EstadoDaEtapa.Ignorada, EstadoDaEtapa.Cancelada],
            [EstadoDaEtapa.Pronta] = [EstadoDaEtapa.EmExecucao, EstadoDaEtapa.Ignorada, EstadoDaEtapa.Cancelada],
            [EstadoDaEtapa.EmExecucao] =
            [
                EstadoDaEtapa.AguardandoAprovacao, EstadoDaEtapa.AguardandoEntrada, EstadoDaEtapa.Interrompida,
                EstadoDaEtapa.Concluida, EstadoDaEtapa.Falhou, EstadoDaEtapa.Cancelada
            ],
            [EstadoDaEtapa.AguardandoAprovacao] =
                [EstadoDaEtapa.EmExecucao, EstadoDaEtapa.Interrompida, EstadoDaEtapa.Cancelada],
            [EstadoDaEtapa.AguardandoEntrada] =
                [EstadoDaEtapa.EmExecucao, EstadoDaEtapa.Interrompida, EstadoDaEtapa.Cancelada],
            [EstadoDaEtapa.Interrompida] = [EstadoDaEtapa.Pronta, EstadoDaEtapa.Cancelada],
            [EstadoDaEtapa.Concluida] = [],
            [EstadoDaEtapa.Falhou] = [],
            [EstadoDaEtapa.Cancelada] = [],
            [EstadoDaEtapa.Ignorada] = []
        };

    private EtapaDaExecucao()
    {
    }

    public EtapaDaExecucao(Execucao execucao, int numero, TipoDeEtapa tipo, bool escrita,
        PoliticaDeRetomada politicaDeRetomada, DateTimeOffset criadaEm, AgentKind? agente = null,
        string? modelo = null, string? esforco = null)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        if (execucao.Terminal) throw new InvalidOperationException("Execução encerrada não recebe etapas.");
        if (numero < 1) throw new ArgumentOutOfRangeException(nameof(numero), "Número da etapa deve ser positivo.");
        if (!Enum.IsDefined(tipo)) throw new ArgumentOutOfRangeException(nameof(tipo), "Tipo de etapa inválido.");
        if (!Enum.IsDefined(politicaDeRetomada))
            throw new ArgumentOutOfRangeException(nameof(politicaDeRetomada), "Política de retomada inválida.");
        if (agente is { } valor && !Enum.IsDefined(valor))
            throw new ArgumentOutOfRangeException(nameof(agente), "Agente inválido.");
        // Fail-closed (AD-68): etapa de escrita de execução nova só existiria com worktree reservada, que esta entrega
        // não cria. Ela nem nasce; o checkout principal nunca recebe escrita de execução nova.
        if (escrita && execucao.Nova) throw new EscritaSemWorktreeException();
        IdExecucao = execucao.Id;
        Numero = numero;
        Tipo = tipo;
        Escrita = escrita;
        PoliticaDeRetomada = politicaDeRetomada;
        Agente = agente;
        Modelo = Opcional(modelo, nameof(modelo));
        Esforco = Opcional(esforco, nameof(esforco));
        CriadaEm = criadaEm;
        AtualizarChave();
    }

    public Guid IdExecucao { get; private set; }
    public int Numero { get; private set; }
    public TipoDeEtapa Tipo { get; private set; }
    public EstadoDaEtapa Estado { get; private set; } = EstadoDaEtapa.Pendente;
    // Se altera repositório. Execuções novas só poderão escrever numa worktree reservada (etapa 5 do roadmap).
    public bool Escrita { get; private set; }
    public PoliticaDeRetomada PoliticaDeRetomada { get; private set; }
    public int Tentativa { get; private set; } = 1;
    public string ChaveDeIdempotencia { get; private set; } = "";
    public AgentKind? Agente { get; private set; }
    public string? Modelo { get; private set; }
    public string? Esforco { get; private set; }
    public Guid? IdSessaoDeAgente { get; private set; }
    // Código T… do turno que executa a etapa na sessão vinculada.
    public string? IdTurno { get; private set; }
    public int? CodigoDeSaida { get; private set; }
    public string? Erro { get; private set; }
    public string? Motivo { get; private set; }
    public DateTimeOffset CriadaEm { get; private set; }
    public DateTimeOffset? IniciadaEm { get; private set; }
    public DateTimeOffset? EncerradaEm { get; private set; }

    public bool Terminal => Transicoes[Estado].Length == 0;

    public static bool PodeTransitar(EstadoDaEtapa de, EstadoDaEtapa para) => Transicoes[de].Contains(para);

    // requeridas: as etapas apontadas pelas dependências desta; todas precisam estar concluídas.
    public void TornarPronta(IEnumerable<EtapaDaExecucao> requeridas)
    {
        ArgumentNullException.ThrowIfNull(requeridas);
        foreach (var requerida in requeridas)
        {
            if (requerida.IdExecucao != IdExecucao)
                throw new InvalidOperationException("Dependência de outra execução.");
            if (requerida.Estado != EstadoDaEtapa.Concluida)
                throw new InvalidOperationException($"A etapa {requerida.Numero} ainda não foi concluída.");
        }
        Transitar(EstadoDaEtapa.Pronta);
    }

    public void Iniciar(DateTimeOffset quando)
    {
        Transitar(EstadoDaEtapa.EmExecucao);
        IniciadaEm ??= quando;
    }

    public void AguardarAprovacao() => Transitar(EstadoDaEtapa.AguardandoAprovacao);

    public void AguardarEntrada() => Transitar(EstadoDaEtapa.AguardandoEntrada);

    // Fim de uma espera de aprovação ou de entrada: a etapa volta a executar.
    public void Continuar()
    {
        if (Estado is not (EstadoDaEtapa.AguardandoAprovacao or EstadoDaEtapa.AguardandoEntrada))
            throw Invalida(EstadoDaEtapa.EmExecucao);
        Transitar(EstadoDaEtapa.EmExecucao);
    }

    // Uma etapa de turno é executada numa sessão do mesmo ledger; o turno é o que está em curso nela.
    public void VincularSessao(SessaoDeAgente sessao, string? idTurno)
    {
        ArgumentNullException.ThrowIfNull(sessao);
        if (Tipo != TipoDeEtapa.TurnoDeAgente)
            throw new InvalidOperationException("Só etapas de turno de agente são executadas numa sessão.");
        if (Terminal) throw new InvalidOperationException("Etapa encerrada não recebe sessão.");
        if (sessao.Terminal) throw new InvalidOperationException("Sessão encerrada não executa etapas.");
        if (idTurno is not null && (string.IsNullOrWhiteSpace(idTurno) || idTurno.Length > TamanhoMaximoDoTurno))
            throw new ArgumentException("Turno inválido.", nameof(idTurno));
        IdSessaoDeAgente = sessao.Id;
        IdTurno = idTurno;
    }

    public void Concluir(int? codigoDeSaida, DateTimeOffset quando)
    {
        Transitar(EstadoDaEtapa.Concluida);
        CodigoDeSaida = codigoDeSaida;
        EncerradaEm = quando;
    }

    public void Falhar(string erro, int? codigoDeSaida, DateTimeOffset quando)
    {
        var texto = Execucao.TextoCortado(erro, Execucao.TamanhoMaximoDoTexto, nameof(erro));
        Transitar(EstadoDaEtapa.Falhou);
        Erro = texto;
        CodigoDeSaida = codigoDeSaida;
        EncerradaEm = quando;
    }

    public void Cancelar(string? motivo, int? codigoDeSaida, DateTimeOffset quando)
    {
        Transitar(EstadoDaEtapa.Cancelada, Execucao.TextoOpcional(motivo));
        CodigoDeSaida = codigoDeSaida;
        EncerradaEm = quando;
    }

    public void Interromper(string motivo) =>
        Transitar(EstadoDaEtapa.Interrompida, Execucao.TextoCortado(motivo, Execucao.TamanhoMaximoDoTexto, nameof(motivo)));

    public void Ignorar() => Transitar(EstadoDaEtapa.Ignorada);

    // Retomada de uma etapa interrompida: nova tentativa, nova chave; o resultado da anterior não é reaproveitado.
    public void Retomar()
    {
        Transitar(EstadoDaEtapa.Pronta);
        Tentativa++;
        IdTurno = null;
        AtualizarChave();
    }

    private void Transitar(EstadoDaEtapa para, string? motivo = null)
    {
        if (!PodeTransitar(Estado, para)) throw Invalida(para);
        Estado = para;
        Motivo = motivo;
    }

    private TransicaoInvalidaException Invalida(EstadoDaEtapa para) => new("etapa", Estado.ToString(), para.ToString());

    private void AtualizarChave() => ChaveDeIdempotencia = $"{IdExecucao:N}:{Numero}:{Tentativa}";

    private static string? Opcional(string? valor, string parametro)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;
        if (valor.Length > TamanhoMaximoDoModelo) throw new ArgumentException("Texto excede o tamanho máximo.", parametro);
        return valor;
    }
}
