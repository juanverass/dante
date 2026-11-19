using Dante.Application.Agentes;
using Dante.Application.Comum;
using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Application.Grupos;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Domain.Grupos;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Worker.Execucoes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static Dante.Tests.FilaDuravelPostgreSqlTests;

namespace Dante.Tests;

// #308 (decisões B, C, F e G): gate durável do roteiro contra PostgreSQL descartável (DANTE_TEST_POSTGRES) e Git real
// em diretório temporário. O executor do roteiro é da entrega seguinte; aqui os passos são conduzidos pelos casos de
// uso, com a posse real da fila, como o executor fará.
[Collection("FilaDuravel")]
public sealed class GateDoRoteiroPostgreSqlTests : IDisposable
{
    private const long Dono = 1001;
    private readonly RepositorioGitDeTeste temp = new();

    public void Dispose() => temp.Dispose();

    [PostgreSqlFact]
    public async Task GateLiberaOSlotPreservaAWorktreeESobreviveAoReinicio()
    {
        var dante = temp.Criar("dante");
        var outro = temp.Criar("outro");
        await using var banco = await FilaAsync();
        RoteiroNoGate gate;
        await using (var provider = ProviderDeRoteiro(banco.ConnectionString, dante, outro))
        {
            gate = await RoteiroNoGateAsync(provider, dante);
            var roteiro = (await RoteirosAsync(provider, r => r.ObterAsync(gate.IdExecucao)))!;
            Assert.Equal(EstadoDaExecucao.AguardandoAprovacao, roteiro.Estado);
            Assert.Equal([TipoDePassoDoRoteiro.Implementar, TipoDePassoDoRoteiro.Revisar, TipoDePassoDoRoteiro.AprovacaoHumana],
                roteiro.Etapas.Select(e => e.Passo));
            Assert.Equal((0, gate.HashDoArtefato), (roteiro.PedidoPendente!.Rodada, roteiro.PedidoPendente.HashDoArtefato));
            // Slot livre, worktree da execução: aguardando decisão, sem trabalho ativo.
            Assert.Equal([EstadoDaReservaDeWorktree.AguardandoDecisao],
                (await ReservasAsync(provider, gate.IdExecucao)).Select(r => r.Estado));
            Assert.Equal(0L, await Escalar(banco, "SELECT count(*) FROM orquestracao.trabalhos_da_fila WHERE estado IN (0, 1)"));
            // Outra execução de escrita reserva com limite global 1, porque o slot foi liberado.
            var versao = await ReservasDeWorktreePostgreSqlTests.VersaoAsync(provider);
            var (tarefa, _) = await ReservasDeWorktreePostgreSqlTests.TarefaDeEscritaAsync(provider, versao, "outra:1");
            var trabalho = (await ReivindicarAsync(provider, Processador("host-a")))!;
            var gerenciador = provider.GetRequiredService<IGerenciadorDeWorktrees>();
            var planejada = await gerenciador.PlanejarAsync(outro, tarefa.Numero, "dev");
            await ReservasAsync(provider, s => s.ReservarAsync(trabalho.Posse, trabalho.IdEtapa!.Value, [planejada], "host-a"));
            // A worktree do gate não é limpa nem pode ser tomada por outra reserva (índice único entre as ativas).
            var limpeza = await ReservasAsync(provider, s => s.LimparAsync("host-a"));
            Assert.Equal((0, 0, 0), (limpeza.Liberadas, limpeza.Removidas, limpeza.Preservadas));
            Assert.True(File.Exists(Path.Combine(gate.Worktree, "resultado.txt")));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, (await Assert.ThrowsAsync<PostgresException>(() => Executar(banco,
                $"""
                INSERT INTO orquestracao.reservas_de_worktree (id, id_execucao, id_etapa, alias, principal, caminho_do_repositorio,
                    caminho, branch, sha_base, maquina_do_worker, estado, criada_em)
                SELECT gen_random_uuid(), '{tarefa.Id}', '{trabalho.IdEtapa}', '@x', true, '/r/x', caminho, 'outra', sha_base,
                    'host-a', 1, now()
                FROM orquestracao.reservas_de_worktree WHERE id_execucao = '{gate.IdExecucao}'
                """))).SqlState);
        }
        // Reinício do Worker: outro provider, nada em memória. O gate continua no ledger e a decisão vale.
        await using (var reiniciado = ProviderDeRoteiro(banco.ConnectionString, dante, outro))
        {
            var roteiro = (await RoteirosAsync(reiniciado, r => r.ObterDoProprietarioAsync(Proprietario(), gate.Codigo)))!;
            Assert.Equal(gate.HashDoArtefato, roteiro.PedidoPendente!.HashDoArtefato);
            Assert.Null(await RoteirosAsync(reiniciado, r => r.ObterDoProprietarioAsync(new(LedgerChannel.Telegram, 999), gate.Codigo)));
            Assert.Null(await RoteirosAsync(reiniciado, r => r.ConferirIntegridadeAsync(gate.IdExecucao)));
            var decisao = await RoteirosAsync(reiniciado, r => r.DecidirAsync(Proprietario(), gate.Codigo,
                new DecisaoHumanaDto(TipoDeDecisaoHumana.Aprovar, gate.HashDoArtefato[..12])));
            Assert.Equal((ResultadoDaDecisao.Aprovada, 0), (decisao.Resultado, decisao.Rodada));
            // Aprovar encerra sem commit nem push: reserva liberada, e a limpeza preserva a worktree alterada.
            Assert.Equal(EstadoDaExecucao.Concluida, (await RoteirosAsync(reiniciado, r => r.ObterAsync(gate.IdExecucao)))!.Estado);
            var limpeza = await ReservasAsync(reiniciado, s => s.LimparAsync("host-a"));
            Assert.Equal(1, limpeza.Preservadas);
            Assert.Equal([EstadoDaReservaDeWorktree.Preservada], (await ReservasAsync(reiniciado, gate.IdExecucao)).Select(r => r.Estado));
            Assert.True(File.Exists(Path.Combine(gate.Worktree, "resultado.txt")));
            Assert.Equal(0L, await Escalar(banco, "SELECT count(*) FROM orquestracao.pedidos_de_aprovacao WHERE estado = 0"));
        }
    }

    [PostgreSqlFact]
    public async Task DecisaoUnicaVinculadaAoHashEDoDonoMesmoComPedidosSimultaneos()
    {
        var dante = temp.Criar("dante");
        await using var banco = await FilaAsync();
        await using var provider = ProviderDeRoteiro(banco.ConnectionString, dante);
        var gate = await RoteiroNoGateAsync(provider, dante);
        // Hash errado, curto ou de outro dono: nada muda.
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoteirosAsync(provider, r => r.DecidirAsync(Proprietario(),
            gate.Codigo, new DecisaoHumanaDto(TipoDeDecisaoHumana.Aprovar, new string('f', 12)))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoteirosAsync(provider, r => r.DecidirAsync(Proprietario(),
            gate.Codigo, new DecisaoHumanaDto(TipoDeDecisaoHumana.Aprovar, gate.HashDoArtefato[..8]))));
        Assert.Equal(ResultadoDaDecisao.NaoEncontrada, (await RoteirosAsync(provider, r => r.DecidirAsync(
            new(LedgerChannel.Telegram, 999), gate.Codigo, new DecisaoHumanaDto(TipoDeDecisaoHumana.Recusar)))).Resultado);
        Assert.Equal(EstadoDaExecucao.AguardandoAprovacao, (await RoteirosAsync(provider, r => r.ObterAsync(gate.IdExecucao)))!.Estado);

        // Duas decisões ao mesmo tempo (aprovar e recusar): exatamente uma vale; a outra encontra o pedido decidido.
        var largada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tarefas = new[]
        {
            new DecisaoHumanaDto(TipoDeDecisaoHumana.Aprovar, gate.HashDoArtefato),
            new DecisaoHumanaDto(TipoDeDecisaoHumana.Recusar, Texto: "não")
        }.Select(d => Task.Run(async () =>
        {
            await largada.Task;
            try { return (await RoteirosAsync(provider, r => r.DecidirAsync(Proprietario(), gate.Codigo, d))).Resultado; }
            catch (ConflitoDeConcorrenciaException) { return ResultadoDaDecisao.SemPedidoPendente; }
        })).ToArray();
        largada.SetResult();
        var resultados = await Task.WhenAll(tarefas);
        Assert.Single(resultados, r => r is ResultadoDaDecisao.Aprovada or ResultadoDaDecisao.Recusada);
        Assert.Single(resultados, r => r == ResultadoDaDecisao.SemPedidoPendente);
        // E uma terceira, depois, também.
        Assert.Equal(ResultadoDaDecisao.SemPedidoPendente, (await RoteirosAsync(provider, r => r.DecidirAsync(Proprietario(),
            gate.Codigo, new DecisaoHumanaDto(TipoDeDecisaoHumana.Aprovar, gate.HashDoArtefato)))).Resultado);
        Assert.Equal(1L, await Escalar(banco, "SELECT count(*) FROM orquestracao.pedidos_de_aprovacao WHERE estado <> 0"));
        // O pedido decidido é imutável no banco.
        Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() =>
            Executar(banco, "UPDATE orquestracao.pedidos_de_aprovacao SET estado = 0, decidido_em = NULL, id_do_decisor = NULL"))).SqlState);
        Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() =>
            Executar(banco, "DELETE FROM orquestracao.pedidos_de_aprovacao"))).SqlState);
    }

    [PostgreSqlFact]
    public async Task CorrecaoReadquireOSlotSobATravaEEsgotarRodadasBloqueiaSemAprovar()
    {
        var dante = temp.Criar("dante");
        var outro = temp.Criar("outro");
        await using var banco = await FilaAsync();
        await using var provider = ProviderDeRoteiro(banco.ConnectionString, dante, outro);
        var gate = await RoteiroNoGateAsync(provider, dante, rodadas: 1);
        var reservaAntes = Assert.Single(await ReservasAsync(provider, gate.IdExecucao));
        // Instrução com credencial nunca é aceita; sem instrução, também não.
        await Assert.ThrowsAsync<ArgumentException>(() => RoteirosAsync(provider, r => r.DecidirAsync(Proprietario(), gate.Codigo,
            new DecisaoHumanaDto(TipoDeDecisaoHumana.Corrigir, gate.HashDoArtefato, "use token=abcd1234efgh"))));
        await Assert.ThrowsAsync<ArgumentException>(() => RoteirosAsync(provider, r => r.DecidirAsync(Proprietario(), gate.Codigo,
            new DecisaoHumanaDto(TipoDeDecisaoHumana.Corrigir, gate.HashDoArtefato))));

        // Enquanto o gate espera, outra escrita ocupa o único slot.
        var versao = await ReservasDeWorktreePostgreSqlTests.VersaoAsync(provider);
        var (tarefa, _) = await ReservasDeWorktreePostgreSqlTests.TarefaDeEscritaAsync(provider, versao, "ocupa:1");
        var ocupante = (await ReivindicarAsync(provider, Processador("host-a")))!;
        var gerenciador = provider.GetRequiredService<IGerenciadorDeWorktrees>();
        var planejada = await gerenciador.PlanejarAsync(outro, tarefa.Numero, "dev");
        await ReservasAsync(provider, s => s.ReservarAsync(ocupante.Posse, ocupante.IdEtapa!.Value, [planejada], "host-a"));

        var correcao = await RoteirosAsync(provider, r => r.DecidirAsync(Proprietario(), gate.Codigo,
            new DecisaoHumanaDto(TipoDeDecisaoHumana.Corrigir, gate.HashDoArtefato[..16], "trate a entrada vazia")));
        Assert.Equal((ResultadoDaDecisao.CorrecaoAgendada, 1), (correcao.Resultado, correcao.Rodada));
        var roteiro = (await RoteirosAsync(provider, r => r.ObterAsync(gate.IdExecucao)))!;
        Assert.Equal(("trate a entrada vazia", EstadoDaExecucao.Pendente), (roteiro.UltimaInstrucaoDeCorrecao, roteiro.Estado));
        Assert.Equal((TipoDePassoDoRoteiro.Corrigir, 1, "dev"), (roteiro.Etapas[^1].Passo, roteiro.Etapas[^1].Rodada, roteiro.Etapas[^1].Papel));
        // A execução corrigida ainda não ocupa o slot (reserva aguardando decisão); a reaquisição espera sem gravar.
        var corrigir = (await ReivindicarAsync(provider, Processador("host-a")))!;
        Assert.Equal(roteiro.Etapas[^1].IdEtapa, corrigir.IdEtapa);
        await Assert.ThrowsAsync<CapacidadeDeWorktreesIndisponivelException>(() => ReservasAsync(provider,
            s => s.ReadquirirAsync(corrigir.Posse, corrigir.IdEtapa!.Value, "host-a")));
        Assert.Equal(EstadoDaReservaDeWorktree.AguardandoDecisao, (await ReservasAsync(provider, gate.IdExecucao))[0].Estado);
        // Outro host não readquire a worktree deste.
        await AppAsync(provider, f => f.ConcluirEtapaAsync(ocupante.Posse, ocupante.IdEtapa!.Value,
            new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou, Erro: "fim do ocupante")));
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => ReservasAsync(provider,
            s => s.ReadquirirAsync(corrigir.Posse, corrigir.IdEtapa!.Value, "host-b")));
        // Slot livre: a mesma reserva volta a ocupar capacidade, e só ela.
        var readquiridas = await ReservasAsync(provider, s => s.ReadquirirAsync(corrigir.Posse, corrigir.IdEtapa!.Value, "host-a"));
        Assert.Equal((reservaAntes.Id, EstadoDaReservaDeWorktree.Pronta), (Assert.Single(readquiridas).Id, readquiridas[0].Estado));
        Assert.Equal(1L, await Escalar(banco, "SELECT count(*) FROM orquestracao.reservas_de_worktree WHERE id_execucao = '" +
            gate.IdExecucao + "'"));
        Assert.Null(await RoteirosAsync(provider, r => r.ConferirIntegridadeAsync(gate.IdExecucao)));

        // Corrige e revisa de novo; o revisor ainda pede correção, mas o limite (1) esgotou: bloqueia, nunca aprova.
        await AppAsync(provider, f => f.IniciarEtapaAsync(corrigir.Posse, corrigir.IdEtapa!.Value));
        Assert.Equal(TipoDePassoDoRoteiro.Revisar, (await RoteirosAsync(provider, r => r.ConcluirPassoAsync(corrigir.Posse,
            corrigir.IdEtapa!.Value, new ConclusaoDoPassoDto(new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida))))).ProximoPasso);
        var revisar = (await ReivindicarAsync(provider, Processador("host-a")))!;
        await AppAsync(provider, f => f.IniciarEtapaAsync(revisar.Posse, revisar.IdEtapa!.Value));
        var bloqueio = await RoteirosAsync(provider, r => r.ConcluirPassoAsync(revisar.Posse, revisar.IdEtapa!.Value,
            new ConclusaoDoPassoDto(new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida), VereditoDeRevisao.RequerCorrecoes,
                new string('c', 64))));
        Assert.Equal(EstadoDaExecucao.Bloqueada, bloqueio.Estado);
        Assert.Contains("Limite de 1", bloqueio.Motivo);
        Assert.Equal(0L, await Escalar(banco, "SELECT count(*) FROM orquestracao.pedidos_de_aprovacao WHERE estado = 0"));
        Assert.Equal(0L, await Escalar(banco, "SELECT count(*) FROM orquestracao.trabalhos_da_fila WHERE estado IN (0, 1)"));
    }

    [PostgreSqlFact]
    public async Task ObjetivoComCredencialNuncaEGravadoEUmNovoESanitizadoEPedidoAPessoa()
    {
        var dante = temp.Criar("dante");
        await using var banco = await FilaAsync();
        await using var provider = ProviderDeRoteiro(banco.ConnectionString, dante);
        const string segredo = "token=supersecreto12345";
        var criado = await CriarRoteiroAsync(provider, dante, $"implemente o login usando {segredo}");
        Assert.True(criado.ObjetivoAlteradoPelaRedacao);
        // O bruto não está em nenhuma coluna do ledger.
        foreach (var tabela in new[] { "execucoes", "roteiros_da_execucao", "eventos", "etapas_da_execucao", "trabalhos_da_fila" })
            Assert.Equal(0L, await Escalar(banco, $"SELECT count(*) FROM orquestracao.{tabela} t WHERE t::text LIKE '%supersecreto%'"));
        var roteiro = (await RoteirosAsync(provider, r => r.ObterAsync(criado.Execucao.IdExecucao)))!;
        Assert.Equal(RoteirosAppService.ObjetivoProtegido, roteiro.ObjetivoRedigido);
        Assert.Equal(RoteiroDaExecucao.HashDoObjetivo(roteiro.IdExecucao, $"implemente o login usando {segredo}"),
            roteiro.HashDoObjetivoOriginal);

        // Depois de um reinício, o executor não tem o original: a execução pede um objetivo sanitizado, sem iniciar.
        var trabalho = (await ReivindicarAsync(provider, Processador("host-a")))!;
        await RoteirosAsync(provider, r => r.SolicitarObjetivoAsync(trabalho.Posse, trabalho.IdEtapa!.Value));
        roteiro = (await RoteirosAsync(provider, r => r.ObterAsync(criado.Execucao.IdExecucao)))!;
        Assert.Equal((EstadoDaExecucao.AguardandoEntrada, EstadoDaEtapa.Pendente), (roteiro.Estado, roteiro.Etapas[0].Estado));
        await Assert.ThrowsAsync<ArgumentException>(() => RoteirosAsync(provider, r => r.SubstituirObjetivoAsync(Proprietario(),
            criado.Execucao.Codigo, $"de novo {segredo}")));
        Assert.False(await RoteirosAsync(provider, r => r.SubstituirObjetivoAsync(new(LedgerChannel.Telegram, 999),
            criado.Execucao.Codigo, "implemente o login")));
        Assert.True(await RoteirosAsync(provider, r => r.SubstituirObjetivoAsync(Proprietario(), criado.Execucao.Codigo,
            "implemente o login lendo a credencial do ambiente")));
        roteiro = (await RoteirosAsync(provider, r => r.ObterAsync(criado.Execucao.IdExecucao)))!;
        Assert.Equal((false, EstadoDaExecucao.Pendente), (roteiro.ObjetivoAlteradoPelaRedacao, roteiro.Estado));
        Assert.Equal(roteiro.Etapas[0].IdEtapa, (await ReivindicarAsync(provider, Processador("host-a")))!.IdEtapa);
        // Substituir de novo não vale (o objetivo guardado já é utilizável), nem pelo banco.
        Assert.False(await RoteirosAsync(provider, r => r.SubstituirObjetivoAsync(Proprietario(), criado.Execucao.Codigo, "outro")));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() =>
            Executar(banco, "UPDATE orquestracao.roteiros_da_execucao SET objetivo_redigido = 'outro'"))).SqlState);
    }

    [PostgreSqlFact]
    public async Task SnapshotImutavelHashDivergenteRecusaDecisaoEAgenteEditadoNaoAlteraAExecucao()
    {
        var dante = temp.Criar("dante");
        await using var banco = await FilaAsync();
        await using var provider = ProviderDeRoteiro(banco.ConnectionString, dante);
        var gate = await RoteiroNoGateAsync(provider, dante);
        // Editar o agente e o grupo depois: o snapshot da execução não muda e continua íntegro.
        await using (var escopo = provider.CreateAsyncScope())
        {
            await escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>().EditarAsync("codex-dev",
                new AlteracaoDoAgenteDto { Instrucoes = "Testes primeiro." }, Dono);
            await escopo.ServiceProvider.GetRequiredService<ICadastroDeGruposAppService>().EditarAsync("eng",
                new AlteracaoDoGrupoDto { RodadasDeRevisaoMaximas = 3 }, Dono);
        }
        var roteiro = (await RoteirosAsync(provider, r => r.ObterAsync(gate.IdExecucao)))!;
        Assert.Equal([1, 1], roteiro.Participantes.Select(p => p.NumeroDaVersao));
        Assert.Equal((1, 2), (roteiro.NumeroDaVersaoDoGrupo, roteiro.RodadasDeRevisaoMaximas));
        Assert.Null(await RoteirosAsync(provider, r => r.ConferirIntegridadeAsync(gate.IdExecucao)));
        // Roteiro em andamento impede arquivar o agente e o grupo.
        await using (var escopo = provider.CreateAsyncScope())
        {
            var agentes = escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>();
            await agentes.DesativarAsync("codex-rev");
            Assert.Contains("ainda tem 1", (await Assert.ThrowsAsync<InvalidOperationException>(() => agentes.ArquivarAsync("codex-rev"))).Message);
            var grupos = escopo.ServiceProvider.GetRequiredService<ICadastroDeGruposAppService>();
            await grupos.DesativarAsync("eng");
            Assert.Contains("ainda tem 1", (await Assert.ThrowsAsync<InvalidOperationException>(() => grupos.ArquivarAsync("eng"))).Message);
        }
        // O banco recusa alterar snapshot, participantes e versões.
        foreach (var sql in new[]
                 {
                     "UPDATE orquestracao.participantes_da_execucao SET pode_escrever = NOT pode_escrever",
                     "DELETE FROM orquestracao.participantes_da_execucao",
                     "UPDATE orquestracao.roteiros_da_execucao SET rodadas_de_revisao_maximas = 9",
                     "UPDATE orquestracao.roteiros_da_execucao SET hash_do_snapshot = repeat('0', 64)",
                     "DELETE FROM orquestracao.roteiros_da_execucao"
                 })
            Assert.Equal(PostgresErrorCodes.RestrictViolation,
                (await Assert.ThrowsAsync<PostgresException>(() => Executar(banco, sql))).SqlState);
        // Defesa em profundidade: mesmo com o trigger desligado por fora, um hash divergente impede a decisão.
        await Executar(banco, """
            ALTER TABLE orquestracao.participantes_da_execucao DISABLE TRIGGER trg_participantes_da_execucao_imutaveis;
            UPDATE orquestracao.participantes_da_execucao SET hash_da_configuracao_do_agente = repeat('9', 64) WHERE papel = 'rev';
            ALTER TABLE orquestracao.participantes_da_execucao ENABLE TRIGGER trg_participantes_da_execucao_imutaveis;
            """);
        Assert.Contains("snapshot", (await RoteirosAsync(provider, r => r.ConferirIntegridadeAsync(gate.IdExecucao)))!);
        Assert.Contains("snapshot", (await Assert.ThrowsAsync<InvalidOperationException>(() => RoteirosAsync(provider,
            r => r.DecidirAsync(Proprietario(), gate.Codigo, new DecisaoHumanaDto(TipoDeDecisaoHumana.Aprovar, gate.HashDoArtefato))))).Message);
        Assert.Equal(1L, await Escalar(banco, "SELECT count(*) FROM orquestracao.pedidos_de_aprovacao WHERE estado = 0"));
    }

    [PostgreSqlFact]
    public async Task CancelarNoGateCancelaOPedidoELiberaAReserva()
    {
        var dante = temp.Criar("dante");
        await using var banco = await FilaAsync();
        await using var provider = ProviderDeRoteiro(banco.ConnectionString, dante);
        var gate = await RoteiroNoGateAsync(provider, dante);
        await using (var escopo = provider.CreateAsyncScope())
        {
            var resultado = await escopo.ServiceProvider.GetRequiredService<IControleDeExecucoesAppService>()
                .SolicitarCancelamentoAsync(Proprietario(), gate.Codigo, "mudou o plano");
            Assert.Equal(ResultadoDoCancelamento.Cancelada, resultado.Resultado);
        }
        var roteiro = (await RoteirosAsync(provider, r => r.ObterAsync(gate.IdExecucao)))!;
        Assert.Equal(EstadoDaExecucao.Cancelada, roteiro.Estado);
        Assert.Null(roteiro.PedidoPendente);
        Assert.Equal(EstadoDoPedidoDeAprovacao.Cancelado, Assert.Single(roteiro.Pedidos).Estado);
        Assert.Equal([EstadoDaReservaDeWorktree.Liberada], (await ReservasAsync(provider, gate.IdExecucao)).Select(r => r.Estado));
        Assert.Equal(ResultadoDaDecisao.SemPedidoPendente, (await RoteirosAsync(provider, r => r.DecidirAsync(Proprietario(),
            gate.Codigo, new DecisaoHumanaDto(TipoDeDecisaoHumana.Aprovar, gate.HashDoArtefato)))).Resultado);
    }

    // O Telegram só interpreta e formata: o detalhe mostra o gate com o hash a confirmar, e as decisões chegam ao caso
    // de uso do dono (outro usuário não vê nem decide).
    [PostgreSqlFact]
    public async Task TelegramMostraOGateEDecideSoPeloDono()
    {
        var dante = temp.Criar("dante");
        await using var banco = await FilaAsync();
        await using var provider = ProviderDeRoteiro(banco.ConnectionString, dante);
        var gate = await RoteiroNoGateAsync(provider, dante);
        var telegram = new Dante.Worker.Telegram.TelegramExecucoes(provider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Dante.Worker.Telegram.TelegramExecucoes>.Instance);
        var detalhe = await telegram.DetalharAsync(Dono, gate.Codigo, CancellationToken.None);
        Assert.Contains("(roteiro de grupo)", detalhe);
        Assert.Contains("Grupo: eng v1", detalhe);
        Assert.Contains($"/execucao aprovar {gate.Codigo} {gate.HashDoArtefato[..12]}", detalhe);
        Assert.Contains("não faz commit, push, PR nem merge", detalhe);
        Assert.Contains("não encontrada", await telegram.DetalharAsync(999, gate.Codigo, CancellationToken.None));
        Assert.Contains("não encontrada", await telegram.DetalharAsync(999, $"aprovar {gate.Codigo} {gate.HashDoArtefato}",
            CancellationToken.None));
        Assert.StartsWith("Uso:", await telegram.DetalharAsync(Dono, $"aprovar {gate.Codigo}", CancellationToken.None));
        Assert.Contains("hash confirmado", await telegram.DetalharAsync(Dono, $"aprovar {gate.Codigo} 000000000000",
            CancellationToken.None));
        // Conteúdo alterado depois da revisão: nada é decidido e o caminho é pedir nova revisão.
        File.AppendAllText(Path.Combine(gate.Worktree, "resultado.txt"), "mudou\n");
        var divergencia = await telegram.DetalharAsync(Dono, $"aprovar {gate.Codigo} {gate.HashDoArtefato[..12]}", CancellationToken.None);
        Assert.Contains("mudou depois da revisão", divergencia);
        Assert.Contains($"/execucao revisar {gate.Codigo}", divergencia);
        File.WriteAllText(Path.Combine(gate.Worktree, "resultado.txt"), "feito pelo implementador\n");
        Assert.Contains("aprovada (rodada 0) e encerrada", await telegram.DetalharAsync(Dono,
            $"aprovar {gate.Codigo} {gate.HashDoArtefato[..12]}", CancellationToken.None));
        Assert.Contains("não está aguardando aprovação", await telegram.DetalharAsync(Dono,
            $"recusar {gate.Codigo}", CancellationToken.None));
    }

    // Review do PR #316: a decisão vale só para o artefato revisado. Gate com H; a worktree passa a H2; aprovar ou
    // corrigir com H é recusado sem gravar nada (gate pendente, nenhuma correção agendada); a nova revisão do conteúdo
    // atual substitui o pedido, e só então a decisão vale, com H2. Um gate também não abre com um hash velho.
    [PostgreSqlFact]
    public async Task ArtefatoAlteradoDepoisDaRevisaoNaoEDecididoEExigeNovaRevisao()
    {
        var dante = temp.Criar("dante");
        await using var banco = await FilaAsync();
        await using var provider = ProviderDeRoteiro(banco.ConnectionString, dante);
        var gate = await RoteiroNoGateAsync(provider, dante);
        // Com o conteúdo ainda revisado, não há o que revisar de novo.
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoteirosAsync(provider,
            r => r.RevisarNovamenteAsync(Proprietario(), gate.Codigo)));
        File.AppendAllText(Path.Combine(gate.Worktree, "resultado.txt"), "alterado depois da revisão\n");
        var gerenciador = provider.GetRequiredService<IGerenciadorDeWorktrees>();
        var reserva = Assert.Single(await ReservasAsync(provider, gate.IdExecucao));
        var h2 = (await gerenciador.ResumirAlteracoesAsync(reserva.Planejada, 0)).Hash;
        Assert.NotEqual(gate.HashDoArtefato, h2);
        foreach (var decisao in new[]
                 {
                     new DecisaoHumanaDto(TipoDeDecisaoHumana.Aprovar, gate.HashDoArtefato),
                     new DecisaoHumanaDto(TipoDeDecisaoHumana.Corrigir, gate.HashDoArtefato, "trate o caso vazio"),
                     // O hash novo também não vale: o pedido é do artefato revisado.
                     new DecisaoHumanaDto(TipoDeDecisaoHumana.Aprovar, h2)
                 })
        {
            var resultado = await RoteirosAsync(provider, r => r.DecidirAsync(Proprietario(), gate.Codigo, decisao));
            if (decisao.HashConfirmado == h2) Assert.Equal(ResultadoDaDecisao.ArtefatoDivergente, resultado.Resultado);
            else Assert.Equal((ResultadoDaDecisao.ArtefatoDivergente, h2), (resultado.Resultado, resultado.HashAtual));
        }
        var roteiro = (await RoteirosAsync(provider, r => r.ObterAsync(gate.IdExecucao)))!;
        Assert.Equal((EstadoDaExecucao.AguardandoAprovacao, gate.HashDoArtefato), (roteiro.Estado, roteiro.PedidoPendente!.HashDoArtefato));
        Assert.Equal(EstadoDaReservaDeWorktree.AguardandoDecisao, (await ReservasAsync(provider, gate.IdExecucao))[0].Estado);
        Assert.Equal(0L, await Escalar(banco, "SELECT count(*) FROM orquestracao.trabalhos_da_fila WHERE estado IN (0, 1)"));
        Assert.Equal(0L, await Escalar(banco, "SELECT count(*) FROM orquestracao.pedidos_de_aprovacao WHERE estado <> 0"));

        // Nova revisão do conteúdo atual: o pedido é substituído, a aprovação cancelada, e uma revisão da mesma rodada vai à fila.
        var revisao = await RoteirosAsync(provider, r => r.RevisarNovamenteAsync(Proprietario(), gate.Codigo));
        Assert.Equal((ResultadoDaDecisao.RevisaoAgendada, 0, h2), (revisao.Resultado, revisao.Rodada, revisao.HashAtual));
        roteiro = (await RoteirosAsync(provider, r => r.ObterAsync(gate.IdExecucao)))!;
        Assert.Equal(EstadoDaExecucao.Pendente, roteiro.Estado);
        Assert.Equal(EstadoDoPedidoDeAprovacao.Substituido, Assert.Single(roteiro.Pedidos).Estado);
        Assert.Equal((TipoDePassoDoRoteiro.Revisar, 0), (roteiro.Etapas[^1].Passo, roteiro.Etapas[^1].Rodada));
        Assert.Equal(EstadoDaEtapa.Cancelada, roteiro.Etapas.Single(e => e.Passo == TipoDePassoDoRoteiro.AprovacaoHumana).Estado);
        // O gate não abre com o hash velho: o diff revisado tem de ser o conteúdo atual.
        var revisar = (await ReivindicarAsync(provider, Processador("host-a")))!;
        await AppAsync(provider, f => f.IniciarEtapaAsync(revisar.Posse, revisar.IdEtapa!.Value));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RoteirosAsync(provider, r => r.ConcluirPassoAsync(
            revisar.Posse, revisar.IdEtapa!.Value, new ConclusaoDoPassoDto(new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida),
                VereditoDeRevisao.Aprovado, gate.HashDoArtefato))));
        var novoGate = await RoteirosAsync(provider, r => r.ConcluirPassoAsync(revisar.Posse, revisar.IdEtapa!.Value,
            new ConclusaoDoPassoDto(new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida), VereditoDeRevisao.Aprovado, h2)));
        Assert.Equal((true, h2), (novoGate.GateAberto, novoGate.HashDoArtefato));
        // No gate novo, o hash antigo digitado é recusado (o artefato confere; a confirmação, não).
        Assert.Contains("hash confirmado", (await Assert.ThrowsAsync<InvalidOperationException>(() => RoteirosAsync(provider,
            r => r.DecidirAsync(Proprietario(), gate.Codigo, new DecisaoHumanaDto(TipoDeDecisaoHumana.Aprovar, gate.HashDoArtefato))))).Message);
        Assert.Equal(ResultadoDaDecisao.Aprovada, (await RoteirosAsync(provider, r => r.DecidirAsync(Proprietario(),
            gate.Codigo, new DecisaoHumanaDto(TipoDeDecisaoHumana.Aprovar, h2[..12])))).Resultado);
    }

    // A corrida: o conteúdo muda entre a primeira conferência e o commit da decisão (aqui, logo depois da primeira
    // leitura do hash). A conferência feita depois de gravar a decisão, ainda na transação, a desfaz inteira: nada é
    // aprovado, a reserva não é liberada e o gate continua pendente, pedindo nova revisão.
    [PostgreSqlFact]
    public async Task AlteracaoConcorrenteDuranteADecisaoDesfazTudo()
    {
        var dante = temp.Criar("dante");
        await using var banco = await FilaAsync();
        var alterador = new AlteraDepoisDaLeitura();
        await using var provider = ProviderDeRoteiro(banco.ConnectionString, [dante], alterador);
        var gate = await RoteiroNoGateAsync(provider, dante);
        foreach (var tipo in new[] { TipoDeDecisaoHumana.Aprovar, TipoDeDecisaoHumana.Corrigir })
        {
            // Volta ao artefato revisado e arma a alteração para a próxima leitura.
            File.WriteAllText(Path.Combine(gate.Worktree, "resultado.txt"), "feito pelo implementador\n");
            alterador.Armar(Path.Combine(gate.Worktree, "resultado.txt"));
            var resultado = await RoteirosAsync(provider, r => r.DecidirAsync(Proprietario(), gate.Codigo,
                new DecisaoHumanaDto(tipo, gate.HashDoArtefato, "corrija")));
            Assert.Equal(ResultadoDaDecisao.ArtefatoDivergente, resultado.Resultado);
            Assert.True(alterador.Disparou);
            Assert.Equal(1L, await Escalar(banco, "SELECT count(*) FROM orquestracao.pedidos_de_aprovacao WHERE estado = 0"));
            Assert.Equal(0L, await Escalar(banco, "SELECT count(*) FROM orquestracao.trabalhos_da_fila WHERE estado IN (0, 1)"));
            Assert.Equal(0L, await Escalar(banco, "SELECT count(*) FROM orquestracao.eventos WHERE tipo = " +
                (int)TipoDeEventoDeOrquestracao.SolicitacaoResolvida));
            Assert.Equal(EstadoDaExecucao.AguardandoAprovacao, (await RoteirosAsync(provider, r => r.ObterAsync(gate.IdExecucao)))!.Estado);
            Assert.Equal(EstadoDaReservaDeWorktree.AguardandoDecisao, (await ReservasAsync(provider, gate.IdExecucao))[0].Estado);
        }
        // Sem alteração em curso, a mesma decisão vale.
        File.WriteAllText(Path.Combine(gate.Worktree, "resultado.txt"), "feito pelo implementador\n");
        Assert.Equal(ResultadoDaDecisao.Aprovada, (await RoteirosAsync(provider, r => r.DecidirAsync(Proprietario(),
            gate.Codigo, new DecisaoHumanaDto(TipoDeDecisaoHumana.Aprovar, gate.HashDoArtefato)))).Resultado);
    }

    [PostgreSqlFact]
    public async Task MigrationDoGateEhReversivelSemDeixarFuncoes()
    {
        await using var banco = await FilaAsync();
        await using var contexto = new OrquestracaoDbContext(OrquestracaoDbContextFactory.Opcoes(banco.ConnectionString));
        var funcoes = "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'orquestracao'";
        var antes = await Escalar(banco, funcoes);
        await contexto.GetService<IMigrator>().MigrateAsync("20261117200148_GruposDeAgentes");
        // As 3 funções do gate, a dos vereditos, as 3 das delegações (#309) e as 8 da coordenação (#310): migrations
        // seguintes, desfeitas junto.
        Assert.Equal(antes - 15, await Escalar(banco, funcoes));
        Assert.Equal(0L, await Escalar(banco, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'orquestracao' " +
            "AND table_name IN ('roteiros_da_execucao', 'participantes_da_execucao', 'pedidos_de_aprovacao')"));
        await contexto.Database.MigrateAsync();
        Assert.Empty(await contexto.Database.GetPendingMigrationsAsync());
        Assert.Equal(antes, await Escalar(banco, funcoes));
    }

    // ---- apoio ----

    // Altera o arquivo armado logo depois de devolver o hash de uma leitura: a próxima leitura (a conferência feita
    // depois de gravar a decisão) vê outro conteúdo.
    private sealed class AlteraDepoisDaLeitura : IGerenciadorDeWorktrees
    {
        private IGerenciadorDeWorktrees inner = null!;
        private string? arquivo;

        public bool Disparou { get; private set; }

        public IGerenciadorDeWorktrees Envolver(IGerenciadorDeWorktrees real)
        {
            inner = real;
            return this;
        }

        public void Armar(string caminho) => (arquivo, Disparou) = (caminho, false);

        public async Task<AlteracoesDaWorktreeDto> ResumirAlteracoesAsync(WorktreePlanejada worktree, int limiteDoDiff,
            CancellationToken cancellationToken = default)
        {
            var resumo = await inner.ResumirAlteracoesAsync(worktree, limiteDoDiff, cancellationToken);
            if (arquivo is { } alvo)
            {
                arquivo = null;
                File.AppendAllText(alvo, "alteração concorrente\n");
                Disparou = true;
            }
            return resumo;
        }

        public Task<WorktreePlanejada> PlanejarAsync(RepositorioCadastrado repositorio, long numeroDaExecucao, string handle,
            CancellationToken cancellationToken = default) => inner.PlanejarAsync(repositorio, numeroDaExecucao, handle, cancellationToken);
        public Task CriarOuAdotarAsync(WorktreePlanejada worktree, CancellationToken cancellationToken = default) =>
            inner.CriarOuAdotarAsync(worktree, cancellationToken);
        public Task ValidarAsync(WorktreePlanejada worktree, CancellationToken cancellationToken = default) =>
            inner.ValidarAsync(worktree, cancellationToken);
        public Task<DesfechoDaLimpeza> RemoverSePossivelAsync(WorktreePlanejada worktree, CancellationToken cancellationToken = default) =>
            inner.RemoverSePossivelAsync(worktree, cancellationToken);
        public IReadOnlyList<string> ListarDiretoriosDesconhecidos(IReadOnlyCollection<string> conhecidos) =>
            inner.ListarDiretoriosDesconhecidos(conhecidos);
    }

    private sealed record RoteiroNoGate(Guid IdExecucao, string Codigo, string Worktree, string HashDoArtefato);

    private static ProprietarioDaExecucao Proprietario() => new(LedgerChannel.Telegram, Dono);

    private ServiceProvider ProviderDeRoteiro(string connectionString, params RepositorioCadastrado[] repositorios) =>
        ProviderDeRoteiro(connectionString, repositorios, null);

    // alterador: envolve o gerenciador Git real para simular uma alteração concorrente da worktree.
    private ServiceProvider ProviderDeRoteiro(string connectionString, RepositorioCadastrado[] repositorios,
        AlteraDepoisDaLeitura? alterador) =>
        Provider(connectionString, configurar: services =>
        {
            if (alterador is not null)
                services.Replace(ServiceDescriptor.Singleton<IGerenciadorDeWorktrees>(provider => alterador.Envolver(
                    new Dante.Infrastructure.Contextos.GerenciadorDeWorktreesGit(provider.GetRequiredService<OpcoesDeWorktrees>(),
                        provider.GetRequiredService<ICatalogoDeRepositorios>()))));
            services.AddSingleton(new OpcoesDeWorktrees { Raiz = temp.RaizDasWorktrees });
            services.Replace(ServiceDescriptor.Singleton<ICatalogoDeRepositorios>(new CatalogoFixo(repositorios)));
            services.Replace(ServiceDescriptor.Singleton<IAgentModelCatalog>(new CatalogoDeModelosFixo()));
        });

    // Agentes Codex, grupo ativado e o roteiro enfileirado no @dante.
    private static async Task<RoteiroCriadoDto> CriarRoteiroAsync(IServiceProvider provider, RepositorioCadastrado dante,
        string objetivo, int rodadas = 2)
    {
        await using var escopo = provider.CreateAsyncScope();
        var s = escopo.ServiceProvider;
        var agentes = s.GetRequiredService<ICadastroDeAgentesAppService>();
        await agentes.CriarAsync(new NovoAgenteDto("codex-dev", new ConfiguracaoDoAgenteDto
        {
            Driver = AgentKind.Codex, PodeEscrever = true, PerfilPadrao = AgentPermissionProfile.Auto,
            PerfilMaximo = AgentPermissionProfile.Auto, Capacidades = [CapacidadeDoAgente.Implementar], Repositorios = ["*"]
        }, Dono));
        await agentes.CriarAsync(new NovoAgenteDto("codex-rev", new ConfiguracaoDoAgenteDto
        {
            Driver = AgentKind.Codex, Capacidades = [CapacidadeDoAgente.Revisar], Repositorios = ["*"]
        }, Dono));
        var grupos = s.GetRequiredService<ICadastroDeGruposAppService>();
        var v1 = (await grupos.CriarAsync(new NovoGrupoDto("eng",
            CadastroDeGruposAppServiceTests.Configuracao(rodadas: rodadas) with { Repositorios = [] }, Dono))).VersaoAtual!;
        await grupos.AtivarAsync("eng", 1, v1.HashDaConfiguracao, Dono);
        var resolucao = await grupos.ResolverParaExecucaoAsync("eng");
        Assert.True(resolucao.Resolvido, resolucao.Erro);
        var numero = await s.GetRequiredService<IExecucaoRepository>().ReservarNumeroAsync();
        return await s.GetRequiredService<IRoteirosAppService>().EnfileirarAsync(new NovoRoteiroDto(resolucao, objetivo,
            JobExecutionContext.Repository("@dante", dante.Caminho, []), numero, LedgerChannel.Telegram, Dono,
            $"telegram:1:{numero}"));
    }

    // Implementar (com worktree real alterada) e revisar aprovado: o roteiro para no gate.
    private static async Task<RoteiroNoGate> RoteiroNoGateAsync(IServiceProvider provider, RepositorioCadastrado dante,
        int rodadas = 2)
    {
        var criado = await CriarRoteiroAsync(provider, dante, "implemente o endpoint de saúde", rodadas);
        var gerenciador = provider.GetRequiredService<IGerenciadorDeWorktrees>();
        var implementar = (await ReivindicarAsync(provider, Processador("host-a")))!;
        var planejada = await gerenciador.PlanejarAsync(dante, criado.Execucao.Numero, "codex-dev");
        var reservada = Assert.Single(await ReservasAsync(provider, s => s.ReservarAsync(implementar.Posse,
            implementar.IdEtapa!.Value, [planejada], "host-a")));
        await gerenciador.CriarOuAdotarAsync(reservada.Planejada);
        var pronta = Assert.Single(await ReservasAsync(provider, s => s.MarcarProntasAsync(implementar.Posse, criado.Execucao.IdExecucao)));
        await AppAsync(provider, f => f.IniciarEtapaAsync(implementar.Posse, implementar.IdEtapa!.Value));
        File.WriteAllText(Path.Combine(pronta.Caminho, "resultado.txt"), "feito pelo implementador\n");
        var avanco = await RoteirosAsync(provider, r => r.ConcluirPassoAsync(implementar.Posse, implementar.IdEtapa!.Value,
            new ConclusaoDoPassoDto(new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida))));
        Assert.Equal((TipoDePassoDoRoteiro?)TipoDePassoDoRoteiro.Revisar, avanco.ProximoPasso);
        var revisar = (await ReivindicarAsync(provider, Processador("host-a")))!;
        await AppAsync(provider, f => f.IniciarEtapaAsync(revisar.Posse, revisar.IdEtapa!.Value));
        // O hash do artefato é o do conteúdo real da worktree: o gate só abre para ele.
        var hash = (await gerenciador.ResumirAlteracoesAsync(pronta.Planejada, 0)).Hash;
        var gate = await RoteirosAsync(provider, r => r.ConcluirPassoAsync(revisar.Posse, revisar.IdEtapa!.Value,
            new ConclusaoDoPassoDto(new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida), VereditoDeRevisao.Aprovado, hash)));
        Assert.True(gate.GateAberto);
        Assert.Equal(hash, gate.HashDoArtefato);
        return new RoteiroNoGate(criado.Execucao.IdExecucao, criado.Execucao.Codigo, pronta.Caminho, hash);
    }

    private static async Task<T> RoteirosAsync<T>(IServiceProvider provider, Func<IRoteirosAppService, Task<T>> acao)
    {
        await using var escopo = provider.CreateAsyncScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<IRoteirosAppService>());
    }

    private static async Task RoteirosAsync(IServiceProvider provider, Func<IRoteirosAppService, Task> acao)
    {
        await using var escopo = provider.CreateAsyncScope();
        await acao(escopo.ServiceProvider.GetRequiredService<IRoteirosAppService>());
    }

    private static async Task<T> ReservasAsync<T>(IServiceProvider provider, Func<IReservasDeWorktreeAppService, Task<T>> acao)
    {
        await using var escopo = provider.CreateAsyncScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<IReservasDeWorktreeAppService>());
    }

    private static Task<IReadOnlyList<WorktreeDaExecucaoDto>> ReservasAsync(IServiceProvider provider, Guid idExecucao) =>
        ReservasAsync(provider, s => s.ListarDaExecucaoAsync(idExecucao));
}
