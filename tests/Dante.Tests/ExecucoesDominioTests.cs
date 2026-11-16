using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;

namespace Dante.Tests;

// #299: máquinas de estado do ledger (AD-66). Toda combinação de origem e destino é exercitada pelos métodos do
// domínio: a permitida muda o estado, a proibida lança TransicaoInvalidaException e não altera nada.
public sealed class ExecucoesDominioTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly (EstadoDaExecucao De, EstadoDaExecucao Para)[] TransicoesDaExecucao =
    [
        (EstadoDaExecucao.Pendente, EstadoDaExecucao.EmExecucao), (EstadoDaExecucao.Pendente, EstadoDaExecucao.Cancelada),
        (EstadoDaExecucao.EmExecucao, EstadoDaExecucao.AguardandoAprovacao),
        (EstadoDaExecucao.EmExecucao, EstadoDaExecucao.AguardandoEntrada),
        (EstadoDaExecucao.EmExecucao, EstadoDaExecucao.AguardandoDelegacoes),
        (EstadoDaExecucao.EmExecucao, EstadoDaExecucao.Bloqueada), (EstadoDaExecucao.EmExecucao, EstadoDaExecucao.Interrompida),
        (EstadoDaExecucao.EmExecucao, EstadoDaExecucao.Pendente), (EstadoDaExecucao.EmExecucao, EstadoDaExecucao.Concluida),
        (EstadoDaExecucao.EmExecucao, EstadoDaExecucao.Falhou), (EstadoDaExecucao.EmExecucao, EstadoDaExecucao.Cancelada),
        (EstadoDaExecucao.AguardandoAprovacao, EstadoDaExecucao.Pendente), (EstadoDaExecucao.AguardandoAprovacao, EstadoDaExecucao.Cancelada),
        (EstadoDaExecucao.AguardandoEntrada, EstadoDaExecucao.Pendente), (EstadoDaExecucao.AguardandoEntrada, EstadoDaExecucao.Cancelada),
        (EstadoDaExecucao.AguardandoDelegacoes, EstadoDaExecucao.Pendente), (EstadoDaExecucao.AguardandoDelegacoes, EstadoDaExecucao.Cancelada),
        (EstadoDaExecucao.Bloqueada, EstadoDaExecucao.Pendente), (EstadoDaExecucao.Bloqueada, EstadoDaExecucao.Cancelada),
        (EstadoDaExecucao.Interrompida, EstadoDaExecucao.Pendente), (EstadoDaExecucao.Interrompida, EstadoDaExecucao.Cancelada)
    ];

    public static TheoryData<EstadoDaExecucao, EstadoDaExecucao> ParesDaExecucao()
    {
        var dados = new TheoryData<EstadoDaExecucao, EstadoDaExecucao>();
        foreach (var de in Enum.GetValues<EstadoDaExecucao>())
            foreach (var para in Enum.GetValues<EstadoDaExecucao>())
                dados.Add(de, para);
        return dados;
    }

    [Theory]
    [MemberData(nameof(ParesDaExecucao))]
    public void ExecucaoSoAceitaAsTransicoesDoDiagrama(EstadoDaExecucao de, EstadoDaExecucao para)
    {
        var permitida = TransicoesDaExecucao.Contains((de, para));
        Assert.Equal(permitida, Execucao.PodeTransitar(de, para));
        var execucao = ExecucaoEm(de);
        Assert.Equal(de, execucao.Estado);
        if (permitida)
        {
            Transitar(execucao, para);
            Assert.Equal(para, execucao.Estado);
            return;
        }
        var excecao = Assert.Throws<TransicaoInvalidaException>(() => Transitar(execucao, para));
        Assert.Equal((de.ToString(), para.ToString()), (excecao.De, excecao.Para));
        Assert.Equal(de, execucao.Estado);
    }

    [Fact]
    public void TerminaisNaoSaemENaoRecebemEtapas()
    {
        foreach (var terminal in new[] { EstadoDaExecucao.Concluida, EstadoDaExecucao.Falhou, EstadoDaExecucao.Cancelada })
        {
            var execucao = ExecucaoEm(terminal);
            Assert.True(execucao.Terminal);
            Assert.NotNull(execucao.EncerradaEm);
            Assert.Throws<InvalidOperationException>(() => new EtapaDaExecucao(execucao, 1, TipoDeEtapa.ProcessoAvulso,
                false, PoliticaDeRetomada.Automatica, Agora));
        }
    }

    [Fact]
    public void ExecucaoGuardaCodigoAutoridadeEDadosDoFim()
    {
        var execucao = new Execucao(42, TipoDeExecucao.Avulsa, "telegram", 1001, "{}", null, Agora);
        Assert.Equal(("J000042", "telegram", 1001L, EstadoDaExecucao.Pendente), (execucao.Codigo, execucao.Canal,
            execucao.IdDoUsuarioNoCanal, execucao.Estado));
        execucao.Iniciar(Agora.AddSeconds(1));
        execucao.Falhar(new string('x', 5000), Agora.AddSeconds(2));
        Assert.Equal(Execucao.TamanhoMaximoDoTexto, execucao.Erro!.Length);
        Assert.Equal((Agora.AddSeconds(1), Agora.AddSeconds(2)), (execucao.IniciadaEm, execucao.EncerradaEm));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Execucao(0, TipoDeExecucao.Avulsa, "telegram", 1, "{}", null, Agora));
        Assert.Throws<ArgumentException>(() => new Execucao(1, TipoDeExecucao.Avulsa, " ", 1, "{}", null, Agora));
        Assert.Throws<ArgumentException>(() => new Execucao(1, TipoDeExecucao.Avulsa, "telegram", 1, "", null, Agora));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Execucao(1, (TipoDeExecucao)99, "telegram", 1, "{}", null, Agora));
    }

    private static readonly (EstadoDaEtapa De, EstadoDaEtapa Para)[] TransicoesDaEtapa =
    [
        (EstadoDaEtapa.Pendente, EstadoDaEtapa.Pronta), (EstadoDaEtapa.Pendente, EstadoDaEtapa.Ignorada),
        (EstadoDaEtapa.Pendente, EstadoDaEtapa.Cancelada), (EstadoDaEtapa.Pronta, EstadoDaEtapa.EmExecucao),
        (EstadoDaEtapa.Pronta, EstadoDaEtapa.Ignorada), (EstadoDaEtapa.Pronta, EstadoDaEtapa.Cancelada),
        (EstadoDaEtapa.EmExecucao, EstadoDaEtapa.AguardandoAprovacao), (EstadoDaEtapa.EmExecucao, EstadoDaEtapa.AguardandoEntrada),
        (EstadoDaEtapa.EmExecucao, EstadoDaEtapa.Interrompida), (EstadoDaEtapa.EmExecucao, EstadoDaEtapa.Concluida),
        (EstadoDaEtapa.EmExecucao, EstadoDaEtapa.Falhou), (EstadoDaEtapa.EmExecucao, EstadoDaEtapa.Cancelada),
        (EstadoDaEtapa.AguardandoAprovacao, EstadoDaEtapa.EmExecucao), (EstadoDaEtapa.AguardandoAprovacao, EstadoDaEtapa.Interrompida),
        (EstadoDaEtapa.AguardandoAprovacao, EstadoDaEtapa.Cancelada), (EstadoDaEtapa.AguardandoEntrada, EstadoDaEtapa.EmExecucao),
        (EstadoDaEtapa.AguardandoEntrada, EstadoDaEtapa.Interrompida), (EstadoDaEtapa.AguardandoEntrada, EstadoDaEtapa.Cancelada),
        (EstadoDaEtapa.Interrompida, EstadoDaEtapa.Pronta), (EstadoDaEtapa.Interrompida, EstadoDaEtapa.Cancelada)
    ];

    public static TheoryData<EstadoDaEtapa, EstadoDaEtapa> ParesDaEtapa()
    {
        var dados = new TheoryData<EstadoDaEtapa, EstadoDaEtapa>();
        foreach (var de in Enum.GetValues<EstadoDaEtapa>())
            foreach (var para in Enum.GetValues<EstadoDaEtapa>())
                dados.Add(de, para);
        return dados;
    }

    [Theory]
    [MemberData(nameof(ParesDaEtapa))]
    public void EtapaSoAceitaAsTransicoesDoDiagrama(EstadoDaEtapa de, EstadoDaEtapa para)
    {
        var permitida = TransicoesDaEtapa.Contains((de, para));
        Assert.Equal(permitida, EtapaDaExecucao.PodeTransitar(de, para));
        var etapa = EtapaEm(de);
        if (permitida)
        {
            TransitarEtapa(etapa, para);
            Assert.Equal(para, etapa.Estado);
            return;
        }
        Assert.ThrowsAny<InvalidOperationException>(() => TransitarEtapa(etapa, para));
        Assert.Equal(de, etapa.Estado);
    }

    [Fact]
    public void RetomadaDeEtapaGeraNovaTentativaENovaChave()
    {
        var etapa = EtapaEm(EstadoDaEtapa.Interrompida);
        var anterior = etapa.ChaveDeIdempotencia;
        Assert.EndsWith(":1:1", anterior);
        etapa.Retomar();
        Assert.Equal((EstadoDaEtapa.Pronta, 2), (etapa.Estado, etapa.Tentativa));
        Assert.EndsWith(":1:2", etapa.ChaveDeIdempotencia);
        Assert.NotEqual(anterior, etapa.ChaveDeIdempotencia);
    }

    [Fact]
    public void DependenciasExigemMesmaExecucaoOrdemEConclusao()
    {
        var execucao = Nova();
        var primeira = Etapa(execucao, 1);
        var segunda = Etapa(execucao, 2);
        var outra = Etapa(Nova(), 1);
        var dependencia = new DependenciaDeEtapa(segunda, primeira);
        Assert.Equal((execucao.Id, segunda.Id, primeira.Id), (dependencia.IdExecucao, dependencia.IdEtapa, dependencia.IdEtapaRequerida));
        Assert.Throws<InvalidOperationException>(() => new DependenciaDeEtapa(primeira, segunda));
        Assert.Throws<InvalidOperationException>(() => new DependenciaDeEtapa(primeira, primeira));
        Assert.Throws<InvalidOperationException>(() => new DependenciaDeEtapa(segunda, outra));

        Assert.Throws<InvalidOperationException>(() => segunda.TornarPronta([primeira]));
        Assert.Throws<InvalidOperationException>(() => segunda.TornarPronta([outra]));
        Assert.Equal(EstadoDaEtapa.Pendente, segunda.Estado);
        primeira.TornarPronta([]);
        primeira.Iniciar(Agora);
        primeira.Concluir(0, Agora);
        segunda.TornarPronta([primeira]);
        Assert.Equal(EstadoDaEtapa.Pronta, segunda.Estado);
    }

    [Fact]
    public void EtapaDeTurnoSeVinculaASessaoEAoTurno()
    {
        var execucao = Nova();
        var turno = new EtapaDaExecucao(execucao, 1, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica,
            Agora, AgentKind.Claude);
        var sessao = Sessao(7);
        turno.VincularSessao(sessao, "T000003");
        Assert.Equal((sessao.Id, "T000003"), (turno.IdSessaoDeAgente, turno.IdTurno));
        var evento = EventoDeOrquestracao.DaExecucao(execucao, TipoDeEventoDeOrquestracao.EtapaTransitou, null, Agora, turno);
        Assert.Equal((execucao.Id, turno.Id, sessao.Id, "T000003"), (evento.IdExecucao, evento.IdEtapa, evento.IdSessaoDeAgente, evento.IdTurno));

        Assert.Throws<InvalidOperationException>(() => Etapa(execucao, 2).VincularSessao(sessao, "T1"));
        Assert.Throws<ArgumentException>(() => turno.VincularSessao(sessao, " "));
        var encerrada = Sessao(8);
        encerrada.Encerrar(null, Agora);
        Assert.Throws<InvalidOperationException>(() => turno.VincularSessao(encerrada, null));
        Assert.Throws<InvalidOperationException>(() =>
            EventoDeOrquestracao.DaExecucao(Nova(), TipoDeEventoDeOrquestracao.EtapaTransitou, null, Agora, turno));
    }

    public static TheoryData<EstadoDaSessaoDeAgente, EstadoDaSessaoDeAgente> ParesDaSessao()
    {
        var dados = new TheoryData<EstadoDaSessaoDeAgente, EstadoDaSessaoDeAgente>();
        foreach (var de in Enum.GetValues<EstadoDaSessaoDeAgente>())
            foreach (var para in Enum.GetValues<EstadoDaSessaoDeAgente>())
                dados.Add(de, para);
        return dados;
    }

    [Theory]
    [MemberData(nameof(ParesDaSessao))]
    public void SessaoSoAceitaAsTransicoesDoDiagrama(EstadoDaSessaoDeAgente de, EstadoDaSessaoDeAgente para)
    {
        var permitida = de switch
        {
            EstadoDaSessaoDeAgente.Iniciando => para != EstadoDaSessaoDeAgente.Iniciando,
            EstadoDaSessaoDeAgente.Ativa => para is EstadoDaSessaoDeAgente.Encerrada or EstadoDaSessaoDeAgente.Falhou or
                EstadoDaSessaoDeAgente.Interrompida,
            _ => false
        };
        Assert.Equal(permitida, SessaoDeAgente.PodeTransitar(de, para));
        var sessao = SessaoEm(de);
        Action transicao = para switch
        {
            EstadoDaSessaoDeAgente.Ativa => () => sessao.MarcarIniciada("up", null, Agora),
            EstadoDaSessaoDeAgente.Encerrada => () => sessao.Encerrar(null, Agora),
            EstadoDaSessaoDeAgente.Falhou => () => sessao.Falhar("erro", null, Agora),
            EstadoDaSessaoDeAgente.Interrompida => () => sessao.Interromper("reinício do Worker", null, Agora),
            _ => () => throw new TransicaoInvalidaException("sessão", de.ToString(), para.ToString())
        };
        if (permitida)
        {
            transicao();
            Assert.Equal(para, sessao.Estado);
            return;
        }
        Assert.Throws<TransicaoInvalidaException>(transicao);
        Assert.Equal(de, sessao.Estado);
    }

    [Fact]
    public void SessaoGuardaCodigoUpstreamEMotivoDoFim()
    {
        var sessao = Sessao(12);
        Assert.Equal("S000012", sessao.Codigo);
        sessao.MarcarIniciada("thread-1", "opus", Agora);
        sessao.Interromper("O Worker foi encerrado.", "thread-2", Agora.AddMinutes(1));
        Assert.Equal(("thread-2", "opus", "O Worker foi encerrado.", Agora.AddMinutes(1)),
            (sessao.IdUpstream, sessao.ModeloInformado, sessao.MotivoDoFim, sessao.EncerradaEm));
        Assert.Throws<ArgumentException>(() => new SessaoDeAgente(1, AgentKind.Codex, "telegram", 1, "{}",
            AgentPermissionProfile.Plan, Agora, idExecucao: Guid.Empty));
    }

    [Fact]
    public void EventoLimitaDadosETurno()
    {
        var sessao = Sessao(1);
        Assert.Throws<ArgumentException>(() => EventoDeOrquestracao.DaSessao(sessao, TipoDeEventoDeOrquestracao.TurnoIniciado,
            null, new string('x', EventoDeOrquestracao.TamanhoMaximoDosDados + 1), Agora));
        Assert.Throws<ArgumentException>(() => EventoDeOrquestracao.DaSessao(sessao, TipoDeEventoDeOrquestracao.TurnoIniciado,
            new string('T', 40), null, Agora));
        var evento = EventoDeOrquestracao.DaSessao(sessao, TipoDeEventoDeOrquestracao.TurnoIniciado, "T000001", null, Agora);
        Assert.Equal((null, sessao.Id, 0L), (evento.IdExecucao, evento.IdSessaoDeAgente, evento.Sequencia));
    }

    internal static Execucao Nova(long numero = 1) =>
        new(numero, TipoDeExecucao.Avulsa, "telegram", 1001, "{\"modo\":\"General\"}", null, Agora);

    internal static EtapaDaExecucao Etapa(Execucao execucao, int numero) => new(execucao, numero,
        TipoDeEtapa.ProcessoAvulso, false, PoliticaDeRetomada.ComConfirmacao, Agora, AgentKind.Codex, "gpt", "high");

    internal static SessaoDeAgente Sessao(long numero) => new(numero, AgentKind.Claude, "telegram", 1001, "{}",
        AgentPermissionProfile.Plan, Agora, "opus", "high");

    private static Execucao ExecucaoEm(EstadoDaExecucao estado)
    {
        var execucao = Nova();
        if (estado == EstadoDaExecucao.Pendente) return execucao;
        if (estado == EstadoDaExecucao.Cancelada)
        {
            execucao.Cancelar("pedido", Agora);
            return execucao;
        }
        execucao.Iniciar(Agora);
        if (estado != EstadoDaExecucao.EmExecucao) Transitar(execucao, estado);
        return execucao;
    }

    private static void Transitar(Execucao execucao, EstadoDaExecucao para)
    {
        switch (para)
        {
            case EstadoDaExecucao.EmExecucao: execucao.Iniciar(Agora); break;
            case EstadoDaExecucao.AguardandoAprovacao: execucao.AguardarAprovacao(); break;
            case EstadoDaExecucao.AguardandoEntrada: execucao.AguardarEntrada(); break;
            case EstadoDaExecucao.AguardandoDelegacoes: execucao.AguardarDelegacoes(); break;
            case EstadoDaExecucao.Bloqueada: execucao.Bloquear("impedimento"); break;
            case EstadoDaExecucao.Interrompida: execucao.Interromper("reinício"); break;
            case EstadoDaExecucao.Concluida: execucao.Concluir(Agora); break;
            case EstadoDaExecucao.Falhou: execucao.Falhar("erro", Agora); break;
            case EstadoDaExecucao.Cancelada: execucao.Cancelar(null, Agora); break;
            // Pendente: reprogramação a partir de EmExecucao; retomada a partir de espera, bloqueio ou interrupção.
            default:
                if (execucao.Estado == EstadoDaExecucao.EmExecucao) execucao.Reprogramar();
                else execucao.Retomar();
                break;
        }
    }

    private static EtapaDaExecucao EtapaEm(EstadoDaEtapa estado)
    {
        var etapa = Etapa(Nova(), 1);
        if (estado == EstadoDaEtapa.Pendente) return etapa;
        if (estado is EstadoDaEtapa.Ignorada or EstadoDaEtapa.Cancelada)
        {
            TransitarEtapa(etapa, estado);
            return etapa;
        }
        etapa.TornarPronta([]);
        if (estado == EstadoDaEtapa.Pronta) return etapa;
        etapa.Iniciar(Agora);
        if (estado != EstadoDaEtapa.EmExecucao) TransitarEtapa(etapa, estado);
        return etapa;
    }

    private static void TransitarEtapa(EtapaDaExecucao etapa, EstadoDaEtapa para)
    {
        switch (para)
        {
            case EstadoDaEtapa.Pronta:
                if (etapa.Estado == EstadoDaEtapa.Interrompida) etapa.Retomar();
                else etapa.TornarPronta([]);
                break;
            case EstadoDaEtapa.EmExecucao:
                if (etapa.Estado is EstadoDaEtapa.AguardandoAprovacao or EstadoDaEtapa.AguardandoEntrada) etapa.Continuar();
                else etapa.Iniciar(Agora);
                break;
            case EstadoDaEtapa.AguardandoAprovacao: etapa.AguardarAprovacao(); break;
            case EstadoDaEtapa.AguardandoEntrada: etapa.AguardarEntrada(); break;
            case EstadoDaEtapa.Interrompida: etapa.Interromper("reinício"); break;
            case EstadoDaEtapa.Concluida: etapa.Concluir(0, Agora); break;
            case EstadoDaEtapa.Falhou: etapa.Falhar("erro", 1, Agora); break;
            case EstadoDaEtapa.Cancelada: etapa.Cancelar(null, null, Agora); break;
            case EstadoDaEtapa.Ignorada: etapa.Ignorar(); break;
            default: throw new TransicaoInvalidaException("etapa", etapa.Estado.ToString(), para.ToString());
        }
    }

    private static SessaoDeAgente SessaoEm(EstadoDaSessaoDeAgente estado)
    {
        var sessao = Sessao(1);
        switch (estado)
        {
            case EstadoDaSessaoDeAgente.Ativa: sessao.MarcarIniciada(null, null, Agora); break;
            case EstadoDaSessaoDeAgente.Encerrada: sessao.Encerrar(null, Agora); break;
            case EstadoDaSessaoDeAgente.Falhou: sessao.Falhar("erro", null, Agora); break;
            case EstadoDaSessaoDeAgente.Interrompida: sessao.Interromper("reinício", null, Agora); break;
        }
        return sessao;
    }
}
