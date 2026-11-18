using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Domain.Grupos;

namespace Dante.Tests;

// #308 (AD-69, decisões B, C, F e G): progressão fixa do roteiro, gate humano vinculado a hash, snapshot e objetivo
// protegidos e a reserva que aguarda a decisão, sem banco.
public sealed class GateDoRoteiroDominioTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    [Theory]
    // concluído, rodada, veredito, tem correção, máximo → próximo passo, rodada (ou bloqueio)
    [InlineData(TipoDePassoDoRoteiro.Implementar, 0, null, true, 2, TipoDePassoDoRoteiro.Revisar, 0)]
    [InlineData(TipoDePassoDoRoteiro.Corrigir, 1, null, true, 2, TipoDePassoDoRoteiro.Revisar, 1)]
    [InlineData(TipoDePassoDoRoteiro.Revisar, 0, VereditoDeRevisao.Aprovado, true, 2, TipoDePassoDoRoteiro.AprovacaoHumana, 0)]
    [InlineData(TipoDePassoDoRoteiro.Revisar, 0, VereditoDeRevisao.RequerCorrecoes, true, 2, TipoDePassoDoRoteiro.Corrigir, 1)]
    [InlineData(TipoDePassoDoRoteiro.Revisar, 1, VereditoDeRevisao.RequerCorrecoes, true, 2, TipoDePassoDoRoteiro.Corrigir, 2)]
    [InlineData(TipoDePassoDoRoteiro.Revisar, 2, VereditoDeRevisao.RequerCorrecoes, true, 2, null, 0)]
    [InlineData(TipoDePassoDoRoteiro.Revisar, 0, VereditoDeRevisao.RequerCorrecoes, false, 0, null, 0)]
    [InlineData(TipoDePassoDoRoteiro.Revisar, 0, VereditoDeRevisao.Bloqueado, true, 2, null, 0)]
    [InlineData(TipoDePassoDoRoteiro.Revisar, 2, VereditoDeRevisao.Aprovado, true, 2, TipoDePassoDoRoteiro.AprovacaoHumana, 2)]
    public void ProgressaoFixaPorTabela(TipoDePassoDoRoteiro concluido, int rodada, VereditoDeRevisao? veredito,
        bool temCorrecao, int maximo, TipoDePassoDoRoteiro? esperado, int rodadaEsperada)
    {
        var proximo = ProgressaoDoRoteiro.AposPasso(concluido, rodada, veredito, temCorrecao, maximo);
        Assert.Equal(esperado, proximo.Passo);
        if (esperado is null) Assert.False(string.IsNullOrWhiteSpace(proximo.MotivoDoBloqueio));
        else Assert.Equal(rodadaEsperada, proximo.Rodada);
    }

    [Fact]
    public void ProgressaoNuncaAprovaImplicitamenteNemAceitaVereditoForaDaRevisao()
    {
        Assert.Equal((TipoDePassoDoRoteiro?)TipoDePassoDoRoteiro.Implementar, ProgressaoDoRoteiro.Inicio().Passo);
        Assert.Contains("Limite de 2", ProgressaoDoRoteiro.AposPasso(TipoDePassoDoRoteiro.Revisar, 2,
            VereditoDeRevisao.RequerCorrecoes, true, 2).MotivoDoBloqueio);
        Assert.Throws<ArgumentException>(() => ProgressaoDoRoteiro.AposPasso(TipoDePassoDoRoteiro.Revisar, 0, null, true, 2));
        Assert.Throws<ArgumentException>(() => ProgressaoDoRoteiro.AposPasso(TipoDePassoDoRoteiro.Implementar, 0,
            VereditoDeRevisao.Aprovado, true, 2));
        Assert.Throws<ArgumentException>(() => ProgressaoDoRoteiro.AposPasso(TipoDePassoDoRoteiro.AprovacaoHumana, 0, null, true, 2));
        // Correção pedida no gate: mesma regra de rodadas.
        Assert.Equal((TipoDePassoDoRoteiro?)TipoDePassoDoRoteiro.Corrigir, ProgressaoDoRoteiro.CorrecaoPedidaNoGate(0, true, 1).Passo);
        Assert.True(ProgressaoDoRoteiro.CorrecaoPedidaNoGate(1, true, 1).Bloqueia);
        Assert.True(ProgressaoDoRoteiro.CorrecaoPedidaNoGate(0, false, 0).Bloqueia);
    }

    [Fact]
    public void ExecucaoRoteiroApontaParaAVersaoDoGrupoESoParaEla()
    {
        var grupo = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => Execucao(TipoDeExecucao.Roteiro, grupo: null));
        Assert.Throws<ArgumentException>(() => Execucao(TipoDeExecucao.Tarefa, agente: Guid.NewGuid(), grupo: grupo));
        Assert.Throws<ArgumentException>(() => Execucao(TipoDeExecucao.Roteiro, agente: Guid.NewGuid(), grupo: grupo));
        Assert.Equal(grupo, Execucao(TipoDeExecucao.Roteiro, grupo: grupo).IdVersaoDoGrupo);
        Assert.True(Execucao(TipoDeExecucao.Roteiro, grupo: grupo).Nova);
    }

    [Fact]
    public void EtapasDoRoteiroTemPapelVersaoEPoliticaFixasPorPasso()
    {
        var execucao = Execucao(TipoDeExecucao.Roteiro, grupo: Guid.NewGuid());
        var agente = Guid.NewGuid();
        var implementar = EtapaDaExecucao.DoRoteiro(execucao, 1, TipoDePassoDoRoteiro.Implementar, 0, Agora, "dev", agente,
            AgentKind.Codex);
        Assert.Equal((true, PoliticaDeRetomada.ComConfirmacao, TipoDeEtapa.TurnoDeAgente, "dev", agente),
            (implementar.Escrita, implementar.PoliticaDeRetomada, implementar.Tipo, implementar.Papel, implementar.IdVersaoDoAgente));
        var revisar = EtapaDaExecucao.DoRoteiro(execucao, 2, TipoDePassoDoRoteiro.Revisar, 0, Agora, "rev", agente);
        Assert.Equal((false, PoliticaDeRetomada.Automatica), (revisar.Escrita, revisar.PoliticaDeRetomada));
        var aprovacao = EtapaDaExecucao.DoRoteiro(execucao, 3, TipoDePassoDoRoteiro.AprovacaoHumana, 0, Agora);
        Assert.Equal((TipoDeEtapa.AprovacaoHumana, false, (string?)null), (aprovacao.Tipo, aprovacao.Escrita, aprovacao.Papel));
        // Aprovação humana sem papel; passo de agente com papel e versão; nunca fora de um Roteiro.
        Assert.Throws<ArgumentException>(() => EtapaDaExecucao.DoRoteiro(execucao, 4, TipoDePassoDoRoteiro.AprovacaoHumana, 0,
            Agora, "dev", agente));
        Assert.Throws<ArgumentException>(() => EtapaDaExecucao.DoRoteiro(execucao, 4, TipoDePassoDoRoteiro.Corrigir, 1, Agora));
        Assert.Throws<InvalidOperationException>(() => EtapaDaExecucao.DoRoteiro(
            Execucao(TipoDeExecucao.Tarefa, agente: agente), 1, TipoDePassoDoRoteiro.Implementar, 0, Agora, "dev", agente));
    }

    [Fact]
    public void PedidoDeAprovacaoDecideUmaVezSoEVinculadoAoHashDoArtefato()
    {
        var (pedido, _) = Pedido();
        Assert.Contains("hash confirmado", Assert.Throws<InvalidOperationException>(() =>
            pedido.Aprovar("telegram", 7, HashA[..11], Agora)).Message);
        Assert.Throws<InvalidOperationException>(() => pedido.Aprovar("telegram", 7, HashB, Agora));
        Assert.Throws<ArgumentException>(() => pedido.SolicitarCorrecao("telegram", 7, HashA, " ", Agora));
        Assert.True(pedido.Pendente);
        pedido.Aprovar("telegram", 7, HashA[..12].ToUpperInvariant(), Agora);
        Assert.Equal((EstadoDoPedidoDeAprovacao.Aprovado, "telegram", (long?)7, (DateTimeOffset?)Agora),
            (pedido.Estado, pedido.CanalDoDecisor, pedido.IdDoDecisor, pedido.DecididoEm));
        // Decisão duplicada (mesma ou outra): recusada.
        Assert.Contains("já foi decidido", Assert.Throws<InvalidOperationException>(() =>
            pedido.Aprovar("telegram", 7, HashA, Agora)).Message);
        Assert.Throws<InvalidOperationException>(() => pedido.Recusar("telegram", 7, null, Agora));
        Assert.Throws<InvalidOperationException>(() => pedido.Cancelar("x", Agora));

        var (outro, _) = Pedido();
        outro.SolicitarCorrecao("telegram", 7, HashA, "trate o caso vazio", Agora);
        Assert.Equal((EstadoDoPedidoDeAprovacao.CorrecaoSolicitada, "trate o caso vazio"), (outro.Estado, outro.Motivo));
        var (recusado, _) = Pedido();
        recusado.Recusar("telegram", 7, null, Agora);
        Assert.Equal(EstadoDoPedidoDeAprovacao.Recusado, recusado.Estado);
        // Hash inválido nunca entra; pedido só na etapa de aprovação humana de um Roteiro.
        var (_, etapa) = Pedido();
        var execucao = Execucao(TipoDeExecucao.Roteiro, grupo: Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => new PedidoDeAprovacao(execucao, etapa, 0, HashA, HashA, Agora));
        var aprovacao = EtapaDaExecucao.DoRoteiro(execucao, 1, TipoDePassoDoRoteiro.AprovacaoHumana, 0, Agora);
        Assert.Throws<ArgumentException>(() => new PedidoDeAprovacao(execucao, aprovacao, 0, HashA, "ABC", Agora));
        Assert.Throws<ArgumentException>(() => new PedidoDeAprovacao(execucao, aprovacao, 0, HashA, HashA.ToUpperInvariant(), Agora));
    }

    [Fact]
    public void SnapshotTemHashIndependenteDaOrdemEObjetivoProtegidoSoESubstituidoPorUmUtilizavel()
    {
        var execucao = Execucao(TipoDeExecucao.Roteiro, grupo: Guid.NewGuid());
        var dev = new ParticipanteDaExecucao(execucao, "dev", "codex-dev", Guid.NewGuid(), 1, HashA, true, null);
        var rev = new ParticipanteDaExecucao(execucao, "rev", "codex-rev", Guid.NewGuid(), 3, HashB, false, null);
        Assert.Equal(RoteiroDaExecucao.CalcularHashDoSnapshot(HashA, [dev, rev]),
            RoteiroDaExecucao.CalcularHashDoSnapshot(HashA, [rev, dev]));
        Assert.NotEqual(RoteiroDaExecucao.CalcularHashDoSnapshot(HashA, [dev, rev]),
            RoteiroDaExecucao.CalcularHashDoSnapshot(HashB, [dev, rev]));
        var bruto = "faça o deploy com token=supersecreto123";
        var hash = RoteiroDaExecucao.HashDoObjetivo(execucao.Id, bruto);
        // O sal é a execução: o mesmo texto em outra execução tem outro hash.
        Assert.NotEqual(hash, RoteiroDaExecucao.HashDoObjetivo(Guid.NewGuid(), bruto));
        var roteiro = new RoteiroDaExecucao(execucao, Guid.NewGuid(), "eng", execucao.IdVersaoDoGrupo!.Value, 1, HashA,
            [new(TipoDePassoDoRoteiro.Implementar, "dev"), new(TipoDePassoDoRoteiro.Revisar, "rev"),
                new(TipoDePassoDoRoteiro.AprovacaoHumana)], 0, [dev, rev], "[conteúdo protegido]", true, hash);
        Assert.False(roteiro.ObjetivoUtilizavel);
        Assert.False(roteiro.TemCorrecao);
        roteiro.SubstituirObjetivo("faça o deploy", RoteiroDaExecucao.HashDoObjetivo(execucao.Id, "faça o deploy"));
        Assert.True(roteiro.ObjetivoUtilizavel);
        Assert.Throws<InvalidOperationException>(() => roteiro.SubstituirObjetivo("outro", hash));
        // Snapshot de outra versão do grupo ou com participante de outra execução: recusado.
        Assert.Throws<InvalidOperationException>(() => new RoteiroDaExecucao(execucao, Guid.NewGuid(), "eng", Guid.NewGuid(), 1,
            HashA, roteiro.Passos, 0, [dev], "x", false, hash));
        var outra = Execucao(TipoDeExecucao.Roteiro, grupo: Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => new RoteiroDaExecucao(outra, Guid.NewGuid(), "eng", outra.IdVersaoDoGrupo!.Value, 1,
            HashA, roteiro.Passos, 0, [dev], "x", false, hash));
    }

    [Fact]
    public void ReservaAguardandoDecisaoContinuaDaExecucaoSemOcuparCapacidade()
    {
        var execucao = Execucao(TipoDeExecucao.Roteiro, grupo: Guid.NewGuid());
        var etapa = EtapaDaExecucao.DoRoteiro(execucao, 1, TipoDePassoDoRoteiro.Implementar, 0, Agora, "dev", Guid.NewGuid());
        var reserva = new ReservaDeWorktree(execucao, etapa, "@dante", true, "/repos/dante", "/wt/dante/E1", "dante/dev/e1",
            new string('0', 40), "host-a", Agora);
        Assert.Throws<TransicaoInvalidaException>(() => reserva.AguardarDecisao());
        reserva.MarcarPronta(Agora);
        Assert.Throws<TransicaoInvalidaException>(() => reserva.Readquirir());
        reserva.AguardarDecisao();
        Assert.Equal((true, false), (reserva.Ativa, reserva.OcupaCapacidade));
        reserva.Readquirir();
        Assert.Equal((EstadoDaReservaDeWorktree.Pronta, true), (reserva.Estado, reserva.OcupaCapacidade));
        reserva.AguardarDecisao();
        reserva.Liberar(Agora);
        Assert.False(reserva.Ativa);
    }

    private static Execucao Execucao(TipoDeExecucao tipo, Guid? agente = null, Guid? grupo = null) =>
        new(1, tipo, "telegram", 7, "{}", "x", Agora, agente, grupo);

    private static (PedidoDeAprovacao Pedido, EtapaDaExecucao Etapa) Pedido()
    {
        var execucao = Execucao(TipoDeExecucao.Roteiro, grupo: Guid.NewGuid());
        var etapa = EtapaDaExecucao.DoRoteiro(execucao, 3, TipoDePassoDoRoteiro.AprovacaoHumana, 0, Agora);
        return (new PedidoDeAprovacao(execucao, etapa, 0, HashB, HashA, Agora), etapa);
    }
}
