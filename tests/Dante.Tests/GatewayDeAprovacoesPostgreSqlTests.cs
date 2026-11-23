using Dante.Application.Agentes;
using Dante.Application.Execucoes;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Worker.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Dante.Tests.AprovacaoDeAcaoDominioTests;
using static Dante.Tests.FilaDuravelPostgreSqlTests;

namespace Dante.Tests;

// #311 (AD-72), PR 2: o caso de uso das aprovações de ação protegida e o adaptador do Telegram contra PostgreSQL real e
// descartável (DANTE_TEST_POSTGRES). Pedido único por payload, decisão só do dono com o hash exibido e uma vez,
// execução única por CAS mesmo concorrente, adulteração do payload, expiração, revogação, falha no meio (Incerta, sem
// repetição) e reinício (pedidos e decisões vêm do ledger; o que estava em execução vira Incerto).
public sealed class GatewayDeAprovacoesPostgreSqlTests
{
    private static readonly ProprietarioDaExecucao DonoDaRaiz = new(Canal, Dono);

    [PostgreSqlFact]
    public async Task PedidoEUnicoPorPayloadSobreviveAoReinicioEADecisaoVemDoLedger()
    {
        await using var banco = await FilaAsync();
        PedidoDeAcaoDto pedido;
        Execucao raiz;
        await using (var provider = ProviderComRelogio(banco))
        {
            raiz = await RaizAsync(provider);
            pedido = await App(provider, a => a.SolicitarAsync(Solicitacao(raiz)));
            Assert.True(pedido.Novo);
            Assert.Equal((EstadoDaAprovacaoDeAcao.Pendente, Agora.AddHours(24)), (pedido.Aprovacao.Estado, pedido.Aprovacao.ExpiraEm));
            Assert.Equal(raiz.Codigo, pedido.Aprovacao.CodigoDaExecucao);
            // O mesmo payload de novo: o pedido vigente, nunca um segundo.
            var repetido = await App(provider, a => a.SolicitarAsync(Solicitacao(raiz)));
            Assert.Equal((false, pedido.Aprovacao.Id), (repetido.Novo, repetido.Aprovacao.Id));
        }
        // Reinício: nada em memória; o pedido e a decisão vêm do ledger.
        await using (var reiniciado = ProviderComRelogio(banco))
        {
            var emAberto = await App(reiniciado, a => a.ListarEmAbertoAsync(DonoDaRaiz));
            Assert.Equal([pedido.Aprovacao.Id], emAberto.Select(a => a.Id));
            Assert.Empty(await App(reiniciado, a => a.ListarEmAbertoAsync(new ProprietarioDaExecucao(Canal, 999))));
            var decisao = await App(reiniciado, a => a.AprovarAsync(DonoDaRaiz, pedido.Aprovacao.Id, pedido.Aprovacao.HashCurto));
            Assert.Equal(DesfechoDaDecisaoDeAcao.Aprovada, decisao.Desfecho);
        }
        await using (var deNovo = ProviderComRelogio(banco))
            Assert.Equal(EstadoDaAprovacaoDeAcao.Aprovada,
                (await App(deNovo, a => a.ObterAsync(DonoDaRaiz, pedido.Aprovacao.Id)))!.Estado);
    }

    [PostgreSqlFact]
    public async Task ForaDoCatalogoNaoProtegidaDeOutroDonoOuForaDaFormaERecusada()
    {
        await using var banco = await FilaAsync();
        await using var provider = ProviderComRelogio(banco);
        var raiz = await RaizAsync(provider);
        var recusas = new List<Exception>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                App(provider, a => a.SolicitarAsync(Solicitacao(raiz) with { Acao = "dante.push" }))),
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                App(provider, a => a.SolicitarAsync(Solicitacao(raiz) with { Acao = CatalogoDeAcoes.EntregarResultado.Nome }))),
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                App(provider, a => a.SolicitarAsync(Solicitacao(raiz) with { Dono = new ProprietarioDaExecucao(Canal, 999) }))),
            await Assert.ThrowsAsync<ArgumentException>(() => App(provider, a => a.SolicitarAsync(Solicitacao(raiz) with
            {
                ParametrosJson = Parametros().Replace("{", "{\"push\":\"sim\",", StringComparison.Ordinal)
            }))),
            // Agente sem etapa: a identidade do agente vem da etapa da sessão, nunca de argumento.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                App(provider, a => a.SolicitarAsync(Solicitacao(raiz) with { Dono = null })))
        };
        Assert.Equal(["Ação fora do catálogo.", "Esta ação não exige aprovação; não há pedido a criar.", "Execução não encontrada."],
            recusas.Take(3).Select(r => r.Message));
        Assert.Empty(await App(provider, a => a.ListarEmAbertoAsync(DonoDaRaiz)));
    }

    [PostgreSqlFact]
    public async Task DecisaoSoDoDonoComOHashExibidoEUmaVez()
    {
        await using var banco = await FilaAsync();
        await using var provider = ProviderComRelogio(banco);
        var raiz = await RaizAsync(provider);
        var pedido = (await App(provider, a => a.SolicitarAsync(Solicitacao(raiz)))).Aprovacao;
        var outro = new ProprietarioDaExecucao(Canal, 999);
        // Outro usuário: indistinguível de inexistente, para aprovar e para recusar.
        Assert.Equal(DesfechoDaDecisaoDeAcao.NaoEncontrada, (await App(provider, a => a.AprovarAsync(outro, pedido.Id, pedido.Hash))).Desfecho);
        Assert.Equal(DesfechoDaDecisaoDeAcao.NaoEncontrada, (await App(provider, a => a.RecusarAsync(outro, pedido.Id, null))).Desfecho);
        // Hash de outro payload, curto demais ou ausente: recusado com a mensagem, e nada muda.
        foreach (var hash in new[] { new string('f', 64), pedido.Hash[..11], null })
            Assert.Contains("hash", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                App(provider, a => a.AprovarAsync(DonoDaRaiz, pedido.Id, hash)))).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EstadoDaAprovacaoDeAcao.Pendente, (await App(provider, a => a.ObterAsync(DonoDaRaiz, pedido.Id)))!.Estado);

        var recusa = await App(provider, a => a.RecusarAsync(DonoDaRaiz, pedido.Id, "senha=abcdef123 não"));
        Assert.Equal(DesfechoDaDecisaoDeAcao.Recusada, recusa.Desfecho);
        Assert.Equal("[conteúdo protegido]", recusa.Aprovacao!.Motivo);
        Assert.Equal(DesfechoDaDecisaoDeAcao.JaDecidida, (await App(provider, a => a.AprovarAsync(DonoDaRaiz, pedido.Id, pedido.Hash))).Desfecho);
        // Recusada não executa.
        var acao = new AcaoFalsa(raiz);
        Assert.Equal(ResultadoDaExecucaoDeAcao.NaoAutorizada, (await App(provider, a => a.ExecutarAsync(pedido.Id, acao.Execucao))).Resultado);
        Assert.Equal(0, acao.Execucoes);
    }

    [PostgreSqlFact]
    public async Task DecisoesSimultaneasSoUmaValeEExecucoesSimultaneasSoUmaRoda()
    {
        await using var banco = await FilaAsync();
        await using var provider = ProviderComRelogio(banco);
        var raiz = await RaizAsync(provider);
        var pedido = (await App(provider, a => a.SolicitarAsync(Solicitacao(raiz)))).Aprovacao;
        var decisoes = await Task.WhenAll(
            Task.Run(() => App(provider, a => a.AprovarAsync(DonoDaRaiz, pedido.Id, pedido.Hash))),
            Task.Run(() => App(provider, a => a.RecusarAsync(DonoDaRaiz, pedido.Id, null))));
        Assert.Equal(1, decisoes.Count(d => d.Desfecho is DesfechoDaDecisaoDeAcao.Aprovada or DesfechoDaDecisaoDeAcao.Recusada));
        Assert.Equal(1, decisoes.Count(d => d.Desfecho == DesfechoDaDecisaoDeAcao.JaDecidida));

        // Outro payload aprovado e três tentativas de execução ao mesmo tempo: o CAS deixa uma só executar.
        var segundo = (await App(provider, a => a.SolicitarAsync(Solicitacao(raiz, artefato: new string('c', 64))))).Aprovacao;
        await App(provider, a => a.AprovarAsync(DonoDaRaiz, segundo.Id, segundo.HashCurto));
        var acao = new AcaoFalsa(raiz, artefato: new string('c', 64)) { Espera = new TaskCompletionSource() };
        var tentativas = Enumerable.Range(0, 3)
            .Select(_ => Task.Run(() => App(provider, a => a.ExecutarAsync(segundo.Id, acao.Execucao)))).ToArray();
        await Esperar(() => acao.Execucoes == 1);
        // As que perderam já voltaram sem executar, com a primeira ainda em andamento.
        await Esperar(() => tentativas.Count(t => t.IsCompleted) == 2);
        acao.Espera.SetResult();
        var resultados = await Task.WhenAll(tentativas);
        Assert.Equal(1, acao.Execucoes);
        Assert.Equal(1, resultados.Count(r => r.Resultado == ResultadoDaExecucaoDeAcao.Executada));
        Assert.All(resultados.Where(r => r.Resultado != ResultadoDaExecucaoDeAcao.Executada), r =>
            Assert.Contains(r.Resultado, new[] { ResultadoDaExecucaoDeAcao.JaIniciada, ResultadoDaExecucaoDeAcao.NaoAutorizada }));

        // Replay: executar de novo e pedir de novo o mesmo payload não executam outra vez.
        Assert.Equal(ResultadoDaExecucaoDeAcao.NaoAutorizada, (await App(provider, a => a.ExecutarAsync(segundo.Id, acao.Execucao))).Resultado);
        var replay = await App(provider, a => a.SolicitarAsync(Solicitacao(raiz, artefato: new string('c', 64))));
        Assert.Equal((false, segundo.Id, EstadoDaAprovacaoDeAcao.Executada), (replay.Novo, replay.Aprovacao.Id, replay.Aprovacao.Estado));
        Assert.Equal("sha abc", replay.Aprovacao.Resultado);
        Assert.Equal(1, acao.Execucoes);
    }

    [PostgreSqlFact]
    public async Task PayloadAdulteradoExpiradoOuIrreproduzivelNaoExecuta()
    {
        await using var banco = await FilaAsync();
        var relogio = new RelogioManual(Agora);
        await using var provider = Provider(banco.ConnectionString, relogio);
        var raiz = await RaizAsync(provider);
        async Task<AprovacaoDeAcaoDto> AprovadaAsync(string artefato, TimeSpan? validade = null)
        {
            var p = (await App(provider, a => a.SolicitarAsync(Solicitacao(raiz, artefato) with { Validade = validade }))).Aprovacao;
            Assert.Equal(DesfechoDaDecisaoDeAcao.Aprovada, (await App(provider, a => a.AprovarAsync(DonoDaRaiz, p.Id, p.Hash))).Desfecho);
            return p;
        }

        // O conteúdo atual (o artefato) mudou depois da aprovação: cancelada, sem executar.
        var adulterada = await AprovadaAsync(new string('1', 64));
        var acao = new AcaoFalsa(raiz, artefato: new string('2', 64));
        var execucao = await App(provider, a => a.ExecutarAsync(adulterada.Id, acao.Execucao));
        Assert.Equal((ResultadoDaExecucaoDeAcao.PayloadAlterado, EstadoDaAprovacaoDeAcao.Cancelada),
            (execucao.Resultado, execucao.Aprovacao!.Estado));
        // O payload atual não pôde ser remontado: cancelada (fail-closed).
        var irreproduzivel = await AprovadaAsync(new string('3', 64));
        var semPayload = new AcaoFalsa(raiz, artefato: new string('3', 64)) { FalhaAoRemontar = true };
        execucao = await App(provider, a => a.ExecutarAsync(irreproduzivel.Id, semPayload.Execucao));
        Assert.Equal((ResultadoDaExecucaoDeAcao.Revogada, EstadoDaAprovacaoDeAcao.Cancelada), (execucao.Resultado, execucao.Aprovacao!.Estado));
        // Aprovada dentro do prazo, executada depois dele: expira.
        var vencida = await AprovadaAsync(new string('4', 64), TimeSpan.FromHours(1));
        relogio.Avancar(TimeSpan.FromHours(2));
        var noPrazo = new AcaoFalsa(raiz, artefato: new string('4', 64));
        execucao = await App(provider, a => a.ExecutarAsync(vencida.Id, noPrazo.Execucao));
        Assert.Equal((ResultadoDaExecucaoDeAcao.Expirada, EstadoDaAprovacaoDeAcao.Expirada), (execucao.Resultado, execucao.Aprovacao!.Estado));
        // Pendente vencida: aprovar não autoriza nada.
        var pendente = (await App(provider, a => a.SolicitarAsync(Solicitacao(raiz, new string('5', 64)) with
        {
            Validade = TimeSpan.FromMinutes(1)
        }))).Aprovacao;
        relogio.Avancar(TimeSpan.FromMinutes(2));
        Assert.Equal(DesfechoDaDecisaoDeAcao.Expirada, (await App(provider, a => a.AprovarAsync(DonoDaRaiz, pendente.Id, pendente.Hash))).Desfecho);
        Assert.Equal(0, acao.Execucoes + semPayload.Execucoes + noPrazo.Execucoes);
        // Executor de outra ação não executa esta.
        var outra = await AprovadaAsync(new string('6', 64));
        var errada = new AcaoFalsa(raiz, artefato: new string('6', 64)) { Acao = CatalogoDeAcoes.EntregarResultado };
        Assert.Equal(ResultadoDaExecucaoDeAcao.NaoAutorizada, (await App(provider, a => a.ExecutarAsync(outra.Id, errada.Execucao))).Resultado);
        Assert.Equal(0, errada.Execucoes);
    }

    [PostgreSqlFact]
    public async Task FalhaNoMeioViraIncertaComEstadoRedigidoESemEfeitoViraFalhaSemRepeticao()
    {
        await using var banco = await FilaAsync();
        await using var provider = ProviderComRelogio(banco);
        var raiz = await RaizAsync(provider);
        async Task<(AprovacaoDeAcaoDto, AcaoFalsa)> AprovadaAsync(string artefato, Func<DesfechoDaAcaoProtegida> desfecho)
        {
            var p = (await App(provider, a => a.SolicitarAsync(Solicitacao(raiz, artefato)))).Aprovacao;
            await App(provider, a => a.AprovarAsync(DonoDaRaiz, p.Id, p.Hash));
            return (p, new AcaoFalsa(raiz, artefato) { Desfecho = desfecho });
        }

        var (quebrou, excecao) = await AprovadaAsync(new string('1', 64), () => throw new IOException("git caiu no meio"));
        var (incerta, comEstado) = await AprovadaAsync(new string('2', 64),
            () => new DesfechoDaAcaoProtegida.Incerta("commit interrompido", "token=ghp_abcdefghijklmnopqrstuvwxyz0123"));
        var (semEfeito, recusada) = await AprovadaAsync(new string('3', 64),
            () => new DesfechoDaAcaoProtegida.SemEfeito("worktree divergente; nada gravado"));
        var r1 = await App(provider, a => a.ExecutarAsync(quebrou.Id, excecao.Execucao));
        Assert.Equal((ResultadoDaExecucaoDeAcao.Incerta, AprovacoesDeAcaoAppService.MotivoDaFalhaInesperada),
            (r1.Resultado, r1.Aprovacao!.Motivo));
        Assert.DoesNotContain("git caiu", r1.Aprovacao.Motivo);
        var r2 = await App(provider, a => a.ExecutarAsync(incerta.Id, comEstado.Execucao));
        Assert.Equal((ResultadoDaExecucaoDeAcao.Incerta, "[conteúdo protegido]"), (r2.Resultado, r2.Aprovacao!.Resultado));
        var r3 = await App(provider, a => a.ExecutarAsync(semEfeito.Id, recusada.Execucao));
        Assert.Equal((ResultadoDaExecucaoDeAcao.Falhou, EstadoDaAprovacaoDeAcao.Falhou), (r3.Resultado, r3.Aprovacao!.Estado));
        // Nenhuma é repetida: nem por execução nova, nem pela recuperação.
        foreach (var (id, acao) in new[] { (quebrou.Id, excecao), (incerta.Id, comEstado), (semEfeito.Id, recusada) })
            Assert.Equal(ResultadoDaExecucaoDeAcao.NaoAutorizada, (await App(provider, a => a.ExecutarAsync(id, acao.Execucao))).Resultado);
        Assert.Equal((1, 1, 1), (excecao.Execucoes, comEstado.Execucoes, recusada.Execucoes));
        var manutencao = await App(provider, a => a.RecuperarAposReinicioAsync());
        Assert.Empty(manutencao.Incertas);
    }

    [PostgreSqlFact]
    public async Task ReinicioDeixaIncertoOQueEstavaEmExecucaoExpiraVencidosEPreservaAprovadas()
    {
        await using var banco = await FilaAsync();
        var relogio = new RelogioManual(Agora);
        Guid emExecucao, vencido, aprovada;
        Execucao raiz;
        await using (var provider = Provider(banco.ConnectionString, relogio))
        {
            raiz = await RaizAsync(provider);
            // Uma execução que gravou o início (CAS) e "caiu" antes do desfecho: o processo some com ela EmExecucao.
            var a = (await App(provider, s => s.SolicitarAsync(Solicitacao(raiz, new string('1', 64))))).Aprovacao;
            await App(provider, s => s.AprovarAsync(DonoDaRaiz, a.Id, a.Hash));
            await using (var escopo = provider.CreateAsyncScope())
            {
                var entidade = (await escopo.ServiceProvider.GetRequiredService<IAprovacaoDeAcaoRepository>().ObterPorIdAsync(a.Id))!;
                Assert.Equal(InicioDaExecucaoDaAcao.Iniciada, entidade.IniciarExecucao(Payload(raiz, new string('1', 64)), relogio.GetUtcNow()));
                await escopo.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
            }
            emExecucao = a.Id;
            vencido = (await App(provider, s => s.SolicitarAsync(Solicitacao(raiz, new string('2', 64)) with
            {
                Validade = TimeSpan.FromMinutes(30)
            }))).Aprovacao.Id;
            var c = (await App(provider, s => s.SolicitarAsync(Solicitacao(raiz, new string('3', 64))))).Aprovacao;
            await App(provider, s => s.AprovarAsync(DonoDaRaiz, c.Id, c.Hash));
            aprovada = c.Id;
        }
        relogio.Avancar(TimeSpan.FromHours(1));

        // Outro processo, nada em memória.
        await using var reiniciado = Provider(banco.ConnectionString, relogio);
        var manutencao = await App(reiniciado, s => s.RecuperarAposReinicioAsync());
        Assert.Equal([emExecucao], manutencao.Incertas.Select(a => a.Id));
        Assert.Equal(AprovacoesDeAcaoAppService.MotivoDoReinicio, manutencao.Incertas[0].Motivo);
        Assert.Equal([vencido], manutencao.Expiradas.Select(a => a.Id));
        // A incerta nunca roda de novo; a aprovada e não iniciada continua valendo, uma vez.
        var acao = new AcaoFalsa(raiz, new string('1', 64));
        Assert.Equal(ResultadoDaExecucaoDeAcao.NaoAutorizada, (await App(reiniciado, s => s.ExecutarAsync(emExecucao, acao.Execucao))).Resultado);
        Assert.Equal(0, acao.Execucoes);
        var pendente = new AcaoFalsa(raiz, new string('3', 64));
        Assert.Equal(ResultadoDaExecucaoDeAcao.Executada, (await App(reiniciado, s => s.ExecutarAsync(aprovada, pendente.Execucao))).Resultado);
        Assert.Equal(ResultadoDaExecucaoDeAcao.NaoAutorizada, (await App(reiniciado, s => s.ExecutarAsync(aprovada, pendente.Execucao))).Resultado);
        Assert.Equal(1, pendente.Execucoes);
        // Uma segunda recuperação não encontra mais nada.
        var segunda = await App(reiniciado, s => s.RecuperarAposReinicioAsync());
        Assert.Empty(segunda.Incertas.Concat(segunda.Expiradas));
    }

    [PostgreSqlFact]
    public async Task AgenteDesativadoOuComOutraVersaoEntreOPedidoEADecisaoOuAExecucaoImpedeAExecucao()
    {
        await using var banco = await FilaAsync();
        await using var provider = ProviderComRelogio(banco);
        var (idAgente, idVersao) = await AgenteAsync(provider);
        var (tarefa, etapa) = await ReservasDeWorktreePostgreSqlTests.TarefaDeEscritaAsync(provider, idVersao, "acao:agente");
        string hashDaVersao;
        await using (var escopo = provider.CreateAsyncScope())
            hashDaVersao = (await escopo.ServiceProvider.GetRequiredService<IVersaoDoAgenteRepository>().ObterPorIdAsync(idVersao))!
                .HashDaConfiguracao;
        SolicitacaoDeAcaoDto DoAgente(string artefato) => Solicitacao(tarefa, artefato) with { Dono = null, IdEtapa = etapa.Id };

        // Pedido do agente: a versão vem da sessão (etapa → execução), e o payload a registra.
        var pedido = (await App(provider, a => a.SolicitarAsync(DoAgente(new string('1', 64))))).Aprovacao;
        Assert.StartsWith("agente", pedido.Solicitante);
        // Desativado antes da decisão: a aprovação é cancelada, não concedida.
        await AlterarAgenteAsync(provider, idAgente, (agente, _) =>
        {
            agente.Desativar(DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        });
        var decisao = await App(provider, a => a.AprovarAsync(DonoDaRaiz, pedido.Id, pedido.Hash));
        Assert.Equal((DesfechoDaDecisaoDeAcao.Cancelada, EstadoDaAprovacaoDeAcao.Cancelada), (decisao.Desfecho, decisao.Aprovacao!.Estado));
        // Desativado, ele nem pede.
        await Assert.ThrowsAsync<InvalidOperationException>(() => App(provider, a => a.SolicitarAsync(DoAgente(new string('2', 64)))));

        // Reativado, pede e é aprovado; uma versão nova (concessões alteradas) antes da execução revoga.
        await AlterarAgenteAsync(provider, idAgente, (agente, _) =>
        {
            agente.Reativar(DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        });
        var segundo = (await App(provider, a => a.SolicitarAsync(DoAgente(new string('3', 64))))).Aprovacao;
        Assert.Equal(DesfechoDaDecisaoDeAcao.Aprovada, (await App(provider, a => a.AprovarAsync(DonoDaRaiz, segundo.Id, segundo.Hash))).Desfecho);
        await AlterarAgenteAsync(provider, idAgente, (agente, servicos) => servicos.GetRequiredService<IVersaoDoAgenteRepository>()
            .AdicionarAsync(agente.NovaVersao(Configuracao(AgentPermissionProfile.Plan), Dono, DateTimeOffset.UtcNow)));
        var acao = new AcaoFalsa(tarefa, new string('3', 64))
        {
            Solicitante = SolicitanteDaAcao.Agente(idVersao, hashDaVersao), IdEtapa = etapa.Id
        };
        var execucao = await App(provider, a => a.ExecutarAsync(segundo.Id, acao.Execucao));
        Assert.Equal((ResultadoDaExecucaoDeAcao.Revogada, EstadoDaAprovacaoDeAcao.Cancelada), (execucao.Resultado, execucao.Aprovacao!.Estado));
        Assert.Contains("outra versão", execucao.Aprovacao.Motivo);
        Assert.Equal(0, acao.Execucoes);
    }

    [PostgreSqlFact]
    public async Task AdaptadorDoTelegramMostraOPedidoEEncaminhaADecisaoQueAApplicationConfere()
    {
        await using var banco = await FilaAsync();
        await using var provider = ProviderComRelogio(banco);
        var raiz = await RaizAsync(provider);
        var pedido = (await App(provider, a => a.SolicitarAsync(Solicitacao(raiz) with
        {
            ParametrosJson = Parametros(mensagem: "feat: entrega\n\nsenha=abcdef123")
        }))).Aprovacao;
        var bot = new BotDeAprovacoes();
        var adaptador = new TelegramAprovacoesDeAcao(provider.GetRequiredService<IServiceScopeFactory>(), bot,
            NullLogger<TelegramAprovacoesDeAcao>.Instance);

        // /acoes: uma mensagem por pedido, com resumo redigido, hash curto e botões.
        Assert.Null(await adaptador.AtenderAsync(10, Dono, "", CancellationToken.None));
        var (texto, teclado) = Assert.Single(bot.Enviadas);
        Assert.Contains($"Hash: {pedido.HashCurto}", texto);
        Assert.Contains("mensagem: [conteúdo protegido]", texto);
        Assert.DoesNotContain("abcdef123", texto);
        Assert.DoesNotContain(pedido.Hash, texto);
        Assert.NotNull(teclado);
        Assert.True(TelegramAcaoCallback.TryParse(teclado!.Rows[0][0].CallbackData, out var aprovar));
        // Outro usuário com o mesmo botão: a Application não encontra o pedido dele.
        Assert.Equal("Pedido não encontrado.", await adaptador.DecidirAsync(999, aprovar, CancellationToken.None));
        Assert.Equal("Nenhum pedido de ação aguardando você.", await adaptador.AtenderAsync(10, 999, "", CancellationToken.None));
        // Botão adulterado com outro hash: a Application recusa com a mensagem do domínio.
        Assert.Contains("hash", await adaptador.DecidirAsync(Dono, aprovar with { Hash = "ffffffffffff" }, CancellationToken.None),
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("✅", await adaptador.DecidirAsync(Dono, aprovar, CancellationToken.None));
        Assert.Contains("já foi decidido", await adaptador.DecidirAsync(Dono, aprovar, CancellationToken.None));
        // Comandos textuais equivalentes: id curto e o mesmo caso de uso.
        var outro = (await App(provider, a => a.SolicitarAsync(Solicitacao(raiz, new string('9', 64))))).Aprovacao;
        var idCurto = TelegramAprovacoesDeAcao.IdCurto(outro.Id);
        Assert.Contains("não encontrado", await adaptador.AtenderAsync(10, 999, $"recusar {idCurto}", CancellationToken.None));
        Assert.StartsWith("❌", await adaptador.AtenderAsync(10, Dono, $"recusar {idCurto} agora não", CancellationToken.None));
        Assert.Equal(TelegramAprovacoesDeAcao.Uso, await adaptador.AtenderAsync(10, Dono, "aprovar", CancellationToken.None));
    }

    // A manutenção do Worker, na partida: recupera pelo ledger antes do polling e avisa o dono de cada encerramento.
    [PostgreSqlFact]
    public async Task ManutencaoNaPartidaRecuperaPeloLedgerEAvisaODono()
    {
        await using var banco = await FilaAsync();
        var relogio = new RelogioManual(Agora);
        await using var provider = Provider(banco.ConnectionString, relogio);
        var raiz = await RaizAsync(provider);
        var interrompida = (await App(provider, s => s.SolicitarAsync(Solicitacao(raiz, new string('1', 64))))).Aprovacao;
        await App(provider, s => s.AprovarAsync(DonoDaRaiz, interrompida.Id, interrompida.Hash));
        await using (var escopo = provider.CreateAsyncScope())
        {
            var entidade = (await escopo.ServiceProvider.GetRequiredService<IAprovacaoDeAcaoRepository>().ObterPorIdAsync(interrompida.Id))!;
            entidade.IniciarExecucao(Payload(raiz, new string('1', 64)), relogio.GetUtcNow());
            await escopo.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        }
        await App(provider, s => s.SolicitarAsync(Solicitacao(raiz, new string('2', 64)) with { Validade = TimeSpan.FromMinutes(5) }));
        relogio.Avancar(TimeSpan.FromMinutes(10));
        var bot = new BotDeAprovacoes();
        var escopos = provider.GetRequiredService<IServiceScopeFactory>();
        var manutencao = new Dante.Worker.Execucoes.ManutencaoDasAprovacoesDeAcao(escopos,
            new TelegramAprovacoesDeAcao(escopos, bot, NullLogger<TelegramAprovacoesDeAcao>.Instance),
            NullLogger<Dante.Worker.Execucoes.ManutencaoDasAprovacoesDeAcao>.Instance, TimeSpan.FromHours(1));
        await manutencao.StartAsync(CancellationToken.None);
        await manutencao.StopAsync(CancellationToken.None);
        Assert.Equal(2, bot.Enviadas.Count);
        Assert.Contains(bot.Enviadas, m => m.Texto.Contains("interrompida por um reinício", StringComparison.Ordinal));
        Assert.Contains(bot.Enviadas, m => m.Texto.Contains("expirou", StringComparison.Ordinal));
        Assert.Empty(await App(provider, s => s.ListarEmAbertoAsync(DonoDaRaiz)));
    }

    // ---- apoio ----

    private static ServiceProvider ProviderComRelogio(Banco banco) => Provider(banco.ConnectionString, new RelogioManual(Agora));

    private static async Task<Execucao> RaizAsync(IServiceProvider provider) =>
        (await CriarAsync(provider, "raiz:" + Guid.NewGuid())).Execucao;

    private static SolicitacaoDeAcaoDto Solicitacao(Execucao execucao, string? artefato = null) =>
        new(CatalogoDeAcoes.EntregarArtefatoDoRoteiro.Nome, execucao.Id, null, "@dante", [], Parametros(artefato), DonoDaRaiz);

    private static async Task<T> App<T>(IServiceProvider provider, Func<IAprovacoesDeAcaoAppService, Task<T>> acao)
    {
        await using var escopo = provider.CreateAsyncScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<IAprovacoesDeAcaoAppService>());
    }

    private static async Task Esperar(Func<bool> condicao)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (!condicao())
        {
            Assert.True(DateTime.UtcNow < limite, "A condição esperada não ocorreu a tempo.");
            await Task.Delay(20);
        }
    }

    private static ConfiguracaoDoAgente Configuracao(AgentPermissionProfile perfil = AgentPermissionProfile.Auto) =>
        new(AgentKind.Codex, perfil, perfil, true, capacidades: [CapacidadeDoAgente.Implementar], repositorios: ["*"]);

    private static async Task<(Guid Agente, Guid Versao)> AgenteAsync(IServiceProvider provider)
    {
        await using var escopo = provider.CreateAsyncScope();
        var s = escopo.ServiceProvider;
        var agente = new Agente("acao" + Random.Shared.Next(1000, 9999), null, null, Dono, DateTimeOffset.UtcNow);
        var versao = agente.NovaVersao(Configuracao(), Dono, DateTimeOffset.UtcNow);
        await s.GetRequiredService<IAgenteRepository>().AdicionarAsync(agente);
        await s.GetRequiredService<IVersaoDoAgenteRepository>().AdicionarAsync(versao);
        await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        return (agente.Id, versao.Id);
    }

    private static async Task AlterarAgenteAsync(IServiceProvider provider, Guid id,
        Func<Agente, IServiceProvider, Task> alteracao)
    {
        await using var escopo = provider.CreateAsyncScope();
        var agente = (await escopo.ServiceProvider.GetRequiredService<IAgenteRepository>().ObterPorIdAsync(id))!;
        await alteracao(agente, escopo.ServiceProvider);
        await escopo.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
    }

    // Consumidor falso de uma ação protegida: remonta o payload com o artefato "atual" e conta as execuções.
    private sealed class AcaoFalsa(Execucao execucao, string? artefato = null)
    {
        private int execucoes;
        public int Execucoes => Volatile.Read(ref execucoes);
        public DefinicaoDeAcao Acao { get; init; } = CatalogoDeAcoes.EntregarArtefatoDoRoteiro;
        public SolicitanteDaAcao? Solicitante { get; init; }
        public Guid? IdEtapa { get; init; }
        public bool FalhaAoRemontar { get; init; }
        public TaskCompletionSource? Espera { get; init; }
        public Func<DesfechoDaAcaoProtegida> Desfecho { get; init; } = () => new DesfechoDaAcaoProtegida.Executada("sha abc");

        public ExecucaoDaAcaoProtegida Execucao => new(Acao,
            (_, _) => FalhaAoRemontar ? throw new IOException("worktree sumiu")
                : Task.FromResult(Payload(execucao, artefato, solicitante: Solicitante, idEtapa: IdEtapa)),
            async (_, _) =>
            {
                Interlocked.Increment(ref execucoes);
                if (Espera is not null) await Espera.Task;
                return Desfecho();
            });
    }

    private sealed class BotDeAprovacoes : ITelegramBotApi
    {
        public List<(string Texto, TelegramInlineKeyboard? Teclado)> Enviadas { get; } = [];
        public bool SupportsInlineKeyboards => true;

        public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TelegramUpdate>>([]);

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            Enviadas.Add((text, null));
            return Task.CompletedTask;
        }

        public Task<long?> SendApprovalAsync(long chatId, string text, TelegramInlineKeyboard? keyboard,
            CancellationToken cancellationToken)
        {
            Enviadas.Add((text, keyboard));
            return Task.FromResult<long?>(1);
        }
    }
}
