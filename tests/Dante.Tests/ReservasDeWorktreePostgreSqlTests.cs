using System.Text.Json;
using Dante.Application.Agentes;
using Dante.Domain.Agentes;
using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static Dante.Tests.FilaDuravelPostgreSqlTests;

namespace Dante.Tests;

// #307 (AD-71): reservas de worktree contra PostgreSQL descartável (DANTE_TEST_POSTGRES) e Git real em diretório
// temporário. Reserva atômica sob posse (todas ou nenhuma), limites global por host e por repositório sob trava
// transacional entre processos, fencing da geração antiga, liberação na transição terminal e limpeza que só remove
// worktree limpa.
[Collection("FilaDuravel")]
public sealed class ReservasDeWorktreePostgreSqlTests : IDisposable
{
    private const long Dono = 1001;
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly RepositorioGitDeTeste temp = new();

    public void Dispose() => temp.Dispose();

    [PostgreSqlFact]
    public async Task ReservaAtomicaDeTodosOsRepositoriosSemDuplicarNaNovaTentativaENemNaNovaGeracao()
    {
        await using var banco = await FilaAsync();
        await using var provider = ProviderDeWorktrees(banco.ConnectionString);
        var versao = await VersaoAsync(provider);
        var (execucao, _) = await TarefaDeEscritaAsync(provider, versao, "w:1");
        var antiga = await ReivindicarAsync(provider, Processador("host-a"), TimeSpan.FromSeconds(3));
        var planejadas = Planejadas(execucao.Numero, "@dante", "@back", "@front");

        var reservadas = await ReservarAsync(provider, antiga!, planejadas);
        Assert.Equal(["@dante", "@back", "@front"], reservadas.Select(r => r.Alias));
        Assert.Equal([true, false, false], reservadas.Select(r => r.Principal));
        Assert.All(reservadas, r => Assert.Equal(EstadoDaReservaDeWorktree.Reservada, r.Estado));
        // Mesma posse, nova chamada: as mesmas reservas.
        Assert.Equal(reservadas.Select(r => r.Id), (await ReservarAsync(provider, antiga!, planejadas)).Select(r => r.Id));
        // Planejamento divergente para a mesma execução: recusado, nada gravado.
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => ReservarAsync(provider, antiga!, planejadas.Take(2).ToArray()));

        // Lease vencido (Worker morto): a nova geração recebe as mesmas reservas, e a antiga não grava mais nada.
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        var nova = await ReivindicarAsync(provider, Processador("host-a"));
        Assert.Equal(antiga!.Geracao + 1, nova!.Geracao);
        Assert.Equal(reservadas.Select(r => r.Id), (await ReservarAsync(provider, nova, planejadas)).Select(r => r.Id));
        await Assert.ThrowsAsync<PosseDoTrabalhoPerdidaException>(() => ServicoAsync(provider, s => s.MarcarProntasAsync(antiga.Posse, execucao.Id)));
        await Assert.ThrowsAsync<PosseDoTrabalhoPerdidaException>(() => ServicoAsync(provider, s => s.FalharAsync(antiga.Posse, execucao.Id, "@dante", "x")));
        await Assert.ThrowsAsync<PosseDoTrabalhoPerdidaException>(() => ReservarAsync(provider, antiga, planejadas));
        Assert.All(await ListarAsync(provider, execucao.Id), r => Assert.Equal(EstadoDaReservaDeWorktree.Reservada, r.Estado));

        var prontas = await ServicoAsync(provider, s => s.MarcarProntasAsync(nova.Posse, execucao.Id));
        Assert.All(prontas, r => Assert.Equal(EstadoDaReservaDeWorktree.Pronta, r.Estado));
        Assert.Equal(3L, await Escalar(banco, "SELECT count(*) FROM orquestracao.reservas_de_worktree"));
        // Eventos só com alias, branch e estado; nunca caminho.
        var eventos = (await ExecucaoAsync(provider, execucao.Numero)).Eventos
            .Where(e => e.Tipo is TipoDeEventoDeOrquestracao.WorktreeReservada or TipoDeEventoDeOrquestracao.WorktreeCriada).ToArray();
        Assert.Equal(6, eventos.Length);
        Assert.All(eventos, e => Assert.DoesNotContain(temp.Raiz, e.Dados!));
        Assert.Equal("@dante", JsonDocument.Parse(eventos[0].Dados!).RootElement.GetProperty("alias").GetString());
    }

    // Vários Workers (identidades e hosts diferentes) reservando ao mesmo tempo: a trava transacional e os índices
    // únicos nunca deixam passar do limite, em nenhuma rodada.
    [PostgreSqlFact]
    public async Task ReservasConcorrentesEntreWorkersRespeitamOsLimitesGlobalEPorRepositorio()
    {
        await using var banco = await FilaAsync();
        await using var operacional = ProviderDeWorktrees(banco.ConnectionString);
        var versao = await VersaoAsync(operacional);

        // Limite operacional (1): oito execuções em repositórios diferentes, oito Workers do mesmo host; um vencedor.
        var resultados = await DisputarAsync(operacional, versao, 8, i => $"@r{i}", _ => "host-a", "op");
        Assert.Equal(1, resultados.Count(r => r is null));
        Assert.All(resultados.Where(r => r is not null), r => Assert.IsType<CapacidadeDeWorktreesIndisponivelException>(r));
        Assert.Equal(1L, await Escalar(banco, "SELECT count(DISTINCT id_execucao) FROM orquestracao.reservas_de_worktree WHERE estado IN (0, 1)"));
        await Executar(banco, "UPDATE orquestracao.reservas_de_worktree SET estado = 2");

        // Capacidade maior que 1 (só em teste; o host recusa): global 3 por host, 2 por repositório.
        await using var amplo = ProviderDeWorktrees(banco.ConnectionString, new OpcoesDeWorktrees
        {
            Raiz = temp.RaizDasWorktrees, LimiteGlobal = 3, LimitePorRepositorio = 2
        });
        resultados = await DisputarAsync(amplo, versao, 12, i => i % 2 == 0 ? "@alfa" : "@beta", _ => "host-a", "amplo");
        Assert.Equal(3, resultados.Count(r => r is null));
        Assert.Equal(3L, await Escalar(banco, "SELECT count(DISTINCT id_execucao) FROM orquestracao.reservas_de_worktree WHERE estado IN (0, 1)"));
        Assert.True(await Escalar(banco, "SELECT max(c) FROM (SELECT count(DISTINCT id_execucao) c FROM orquestracao.reservas_de_worktree " +
            "WHERE estado IN (0, 1) GROUP BY caminho_do_repositorio) x") <= 2);
        await Executar(banco, "UPDATE orquestracao.reservas_de_worktree SET estado = 2");

        // O limite global é por host; o do repositório vale entre hosts. Mesmo repositório em dois hosts: um só.
        resultados = await DisputarAsync(operacional, versao, 6, i => $"@h{i}", i => $"host-{i % 2}", "hosts");
        Assert.Equal(2, resultados.Count(r => r is null));
        Assert.Equal(1L, await Escalar(banco, "SELECT max(c) FROM (SELECT count(*) c FROM orquestracao.reservas_de_worktree " +
            "WHERE estado IN (0, 1) GROUP BY maquina_do_worker) x"));
        await Executar(banco, "UPDATE orquestracao.reservas_de_worktree SET estado = 2");
        resultados = await DisputarAsync(operacional, versao, 6, _ => "@mesmo", i => $"host-{i % 2}", "mesmo");
        Assert.Equal(1, resultados.Count(r => r is null));
    }

    // Falha na criação de uma das worktrees: a dela fica Falhou (diretório nunca reutilizado), as outras liberadas, e a
    // capacidade volta. Transições terminais liberam as reservas na mesma transação; interrompida não ocupa capacidade.
    [PostgreSqlFact]
    public async Task FalhaParcialTransicaoTerminalECancelamentoLiberamACapacidade()
    {
        await using var banco = await FilaAsync();
        await using var provider = ProviderDeWorktrees(banco.ConnectionString);
        var versao = await VersaoAsync(provider);

        var (primeira, _) = await TarefaDeEscritaAsync(provider, versao, "w:p1");
        var t1 = (await ReivindicarAsync(provider, Processador("host-a")))!;
        await ReservarAsync(provider, t1, Planejadas(primeira.Numero, "@dante", "@back"));
        await ServicoAsync(provider, s => s.FalharAsync(t1.Posse, primeira.Id, "@back", "diretório ocupado"));
        var depois = await ListarAsync(provider, primeira.Id);
        Assert.Equal([EstadoDaReservaDeWorktree.Liberada, EstadoDaReservaDeWorktree.Falhou], depois.Select(r => r.Estado));
        Assert.Equal("diretório ocupado", depois[1].Motivo);
        // A mesma execução não reaproveita o caminho que falhou.
        await Assert.ThrowsAsync<WorktreeInvalidaException>(() => ReservarAsync(provider, t1, Planejadas(primeira.Numero, "@dante", "@back")));
        await AppAsync(provider, f => f.ConcluirEtapaAsync(t1.Posse, t1.IdEtapa!.Value, new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou, Erro: "worktree")));

        // Conclusão terminal libera na mesma transação.
        var (segunda, _) = await TarefaDeEscritaAsync(provider, versao, "w:p2");
        var t2 = (await ReivindicarAsync(provider, Processador("host-a")))!;
        await ReservarAsync(provider, t2, Planejadas(segunda.Numero, "@dante"));
        await ServicoAsync(provider, s => s.MarcarProntasAsync(t2.Posse, segunda.Id));
        var (bloqueada, _) = await TarefaDeEscritaAsync(provider, versao, "w:p3");
        var t3 = (await ReivindicarAsync(provider, Processador("host-b")))!;
        await Assert.ThrowsAsync<CapacidadeDeWorktreesIndisponivelException>(() =>
            ReservarAsync(provider, t3, Planejadas(bloqueada.Numero, "@outro")));
        await AppAsync(provider, f => f.IniciarEtapaAsync(t2.Posse, t2.IdEtapa!.Value));
        await AppAsync(provider, f => f.ConcluirEtapaAsync(t2.Posse, t2.IdEtapa!.Value, new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida)));
        Assert.All(await ListarAsync(provider, segunda.Id), r => Assert.Equal(EstadoDaReservaDeWorktree.Liberada, r.Estado));
        Assert.Contains((await ExecucaoAsync(provider, segunda.Numero)).Eventos, e => e.Tipo == TipoDeEventoDeOrquestracao.WorktreeLiberada);

        // Interrompida (Worker morto durante a escrita): mantém a reserva, mas não ocupa capacidade.
        await ReservarAsync(provider, t3, Planejadas(bloqueada.Numero, "@outro"));
        await AppAsync(provider, f => f.IniciarEtapaAsync(t3.Posse, t3.IdEtapa!.Value));
        var avaliacao = await AppAsync(provider, f => f.AvaliarAsync(t3));
        var interrompida = await AppAsync(provider, f => f.InterromperAsync(t3.Posse, avaliacao.Alvo!, "Worker reiniciou"));
        Assert.Equal(EstadoDaExecucao.Interrompida, interrompida.NovoEstado);
        Assert.All(await ListarAsync(provider, bloqueada.Id), r => Assert.Equal(EstadoDaReservaDeWorktree.Reservada, r.Estado));
        var (quarta, _) = await TarefaDeEscritaAsync(provider, versao, "w:p4");
        var t4 = (await ReivindicarAsync(provider, Processador("host-b")))!;
        await ReservarAsync(provider, t4, Planejadas(quarta.Numero, "@quarta"));

        // O dono cancela a interrompida (sem posse): a reserva é liberada na mesma transação.
        await using (var escopo = provider.CreateAsyncScope())
        {
            var resultado = await escopo.ServiceProvider.GetRequiredService<IControleDeExecucoesAppService>()
                .SolicitarCancelamentoAsync(new ProprietarioDaExecucao("telegram", Dono), bloqueada.Codigo, null);
            Assert.Equal(ResultadoDoCancelamento.Cancelada, resultado.Resultado);
        }
        Assert.All(await ListarAsync(provider, bloqueada.Id), r => Assert.Equal(EstadoDaReservaDeWorktree.Liberada, r.Estado));
    }

    [PostgreSqlFact]
    public async Task BancoRecusaDuasReservasAtivasNoMesmoCaminhoEReservaNoCheckoutPrincipal()
    {
        await using var banco = await FilaAsync();
        await using var provider = ProviderDeWorktrees(banco.ConnectionString);
        var versao = await VersaoAsync(provider);
        var (execucao, etapa) = await TarefaDeEscritaAsync(provider, versao, "w:db");
        var trabalho = (await ReivindicarAsync(provider, Processador("host-a")))!;
        var reservada = Assert.Single(await ReservarAsync(provider, trabalho, Planejadas(execucao.Numero, "@dante")));
        string Insert(string id, string caminho, string branch, string alias) =>
            "INSERT INTO orquestracao.reservas_de_worktree (id, id_execucao, id_etapa, alias, principal, caminho_do_repositorio, " +
            "caminho, branch, sha_base, estado, maquina_do_worker, criada_em) VALUES " +
            $"('{id}', '{execucao.Id}', '{etapa.Id}', '{alias}', false, '/repos/x', '{caminho}', '{branch}', '{Sha}', 0, 'h', now())";
        var duplicada = await Assert.ThrowsAsync<PostgresException>(() =>
            Executar(banco, Insert(Guid.NewGuid().ToString(), reservada.Caminho, "dante/dev/e999", "@outro")));
        Assert.Equal("23505", duplicada.SqlState);
        var noCheckout = await Assert.ThrowsAsync<PostgresException>(() =>
            Executar(banco, Insert(Guid.NewGuid().ToString(), "/repos/x", "dante/dev/e998", "@x")));
        Assert.Equal("23514", noCheckout.SqlState);
    }

    // Limpeza com Git real: removida só a worktree limpa; a com alterações fica preservada com motivo; diretório
    // desconhecido sob a raiz é relatado e nunca removido; o checkout principal não muda.
    [PostgreSqlFact]
    public async Task LimpezaRemoveSoALimpaPreservaAlteradaERelataDesconhecidas()
    {
        var dante = temp.Criar("dante");
        var back = temp.Criar("back");
        var impressoes = (RepositorioGitDeTeste.Impressao(dante.Caminho), RepositorioGitDeTeste.Impressao(back.Caminho));
        await using var banco = await FilaAsync();
        await using var provider = ProviderDeWorktrees(banco.ConnectionString, catalogo: new CatalogoFixo(dante, back));
        var gerenciador = provider.GetRequiredService<IGerenciadorDeWorktrees>();
        var versao = await VersaoAsync(provider);
        var (execucao, _) = await TarefaDeEscritaAsync(provider, versao, "w:limpa");
        var trabalho = (await ReivindicarAsync(provider, Processador("host-a")))!;
        WorktreePlanejada[] planejadas = [await gerenciador.PlanejarAsync(dante, execucao.Numero, "dev"),
            await gerenciador.PlanejarAsync(back, execucao.Numero, "dev")];
        foreach (var reserva in await ReservarAsync(provider, trabalho, planejadas))
            await gerenciador.CriarOuAdotarAsync(reserva.Planejada);
        var prontas = await ServicoAsync(provider, s => s.MarcarProntasAsync(trabalho.Posse, execucao.Id));
        File.WriteAllText(Path.Combine(prontas[0].Caminho, "codigo.txt"), "alterado pelo agente\n");
        var orfa = Path.Combine(temp.RaizDasWorktrees, "dante", "E99999");
        Directory.CreateDirectory(orfa);

        // Execução em andamento: nada é tocado.
        var emAndamento = await ServicoAsync(provider, s => s.LimparAsync("host-a"));
        Assert.Equal((0, 0, 0), (emAndamento.Liberadas, emAndamento.Removidas, emAndamento.Preservadas));
        Assert.Equal([orfa], emAndamento.Desconhecidos);

        await AppAsync(provider, f => f.IniciarEtapaAsync(trabalho.Posse, trabalho.IdEtapa!.Value));
        await AppAsync(provider, f => f.ConcluirEtapaAsync(trabalho.Posse, trabalho.IdEtapa!.Value, new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida)));
        // Outro host não limpa o que não é dele.
        Assert.Equal(0, (await ServicoAsync(provider, s => s.LimparAsync("host-b"))).Removidas);
        var limpeza = await ServicoAsync(provider, s => s.LimparAsync("host-a"));
        Assert.Equal((1, 1, 0), (limpeza.Removidas, limpeza.Preservadas, limpeza.FalhasAoRemover));
        var finais = await ListarAsync(provider, execucao.Id);
        Assert.Equal((EstadoDaReservaDeWorktree.Preservada, "Preservada: há alterações não commitadas."), (finais[0].Estado, finais[0].Motivo));
        Assert.Equal(EstadoDaReservaDeWorktree.Removida, finais[1].Estado);
        Assert.True(File.Exists(Path.Combine(prontas[0].Caminho, "codigo.txt")));
        Assert.False(Directory.Exists(prontas[1].Caminho));
        Assert.True(Directory.Exists(orfa));
        Assert.Contains("dante/dev/e" + execucao.Numero, RepositorioGitDeTeste.Git(back.Caminho, "branch", "--list", "dante/*"));
        // Idempotente: uma segunda passada não muda nada.
        var segunda = await ServicoAsync(provider, s => s.LimparAsync("host-a"));
        Assert.Equal((0, 0, 0), (segunda.Liberadas, segunda.Removidas, segunda.Preservadas));
        Assert.Equal(impressoes, (RepositorioGitDeTeste.Impressao(dante.Caminho), RepositorioGitDeTeste.Impressao(back.Caminho)));
    }

    // ---- apoio ----

    private ServiceProvider ProviderDeWorktrees(string connectionString, OpcoesDeWorktrees? opcoes = null,
        ICatalogoDeRepositorios? catalogo = null) =>
        Provider(connectionString, configurar: services =>
        {
            services.AddSingleton(opcoes ?? new OpcoesDeWorktrees { Raiz = temp.RaizDasWorktrees });
            services.Replace(ServiceDescriptor.Singleton(catalogo ?? new CatalogoFixo()));
        });

    private WorktreePlanejada[] Planejadas(long numero, params string[] aliases) => aliases.Select(alias =>
        new WorktreePlanejada(alias, Path.Combine(temp.Raiz, "repos", alias[1..]),
            Path.Combine(temp.RaizDasWorktrees, alias[1..], $"E{numero}"), $"dante/dev/e{numero}", Sha)).ToArray();

    // N execuções reivindicadas por N Workers e reservando juntas; null = reservou, senão a exceção.
    private async Task<Exception?[]> DisputarAsync(IServiceProvider provider, Guid versao, int quantidade,
        Func<int, string> alias, Func<int, string> host, string prefixo)
    {
        var participantes = new List<(TrabalhoReivindicado Trabalho, WorktreePlanejada[] Planejadas, string Host)>();
        for (var i = 0; i < quantidade; i++)
        {
            var (execucao, _) = await TarefaDeEscritaAsync(provider, versao, $"{prefixo}:{i}");
            var trabalho = (await ReivindicarAsync(provider, Processador(host(i))))!;
            Assert.Equal(execucao.Id, trabalho.IdExecucao);
            participantes.Add((trabalho, Planejadas(execucao.Numero, alias(i)), host(i)));
        }
        var largada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tarefas = participantes.Select(p => Task.Run(async () =>
        {
            await largada.Task;
            try
            {
                await ReservarAsync(provider, p.Trabalho, p.Planejadas, p.Host);
                return (Exception?)null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        })).ToArray();
        largada.SetResult();
        return await Task.WhenAll(tarefas);
    }

    private static async Task<IReadOnlyList<WorktreeDaExecucaoDto>> ReservarAsync(IServiceProvider provider,
        TrabalhoReivindicado trabalho, IReadOnlyList<WorktreePlanejada> planejadas, string host = "host-a") =>
        await ServicoAsync(provider, s => s.ReservarAsync(trabalho.Posse, trabalho.IdEtapa!.Value, planejadas, host));

    private static async Task<IReadOnlyList<WorktreeDaExecucaoDto>> ListarAsync(IServiceProvider provider, Guid idExecucao) =>
        await ServicoAsync(provider, s => s.ListarDaExecucaoAsync(idExecucao));

    private static async Task<T> ServicoAsync<T>(IServiceProvider provider, Func<IReservasDeWorktreeAppService, Task<T>> acao)
    {
        await using var escopo = provider.CreateAsyncScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<IReservasDeWorktreeAppService>());
    }

    private static async Task ServicoAsync(IServiceProvider provider, Func<IReservasDeWorktreeAppService, Task> acao)
    {
        await using var escopo = provider.CreateAsyncScope();
        await acao(escopo.ServiceProvider.GetRequiredService<IReservasDeWorktreeAppService>());
    }

    internal static async Task<Guid> VersaoAsync(IServiceProvider provider)
    {
        await using var escopo = provider.CreateAsyncScope();
        var s = escopo.ServiceProvider;
        var agente = new Agente("dev", null, null, Dono, DateTimeOffset.UtcNow);
        var versao = agente.NovaVersao(new ConfiguracaoDoAgente(AgentKind.Codex, AgentPermissionProfile.Auto,
            AgentPermissionProfile.Auto, true, capacidades: [CapacidadeDoAgente.Implementar], repositorios: ["*"]), Dono,
            DateTimeOffset.UtcNow);
        await s.GetRequiredService<IAgenteRepository>().AdicionarAsync(agente);
        await s.GetRequiredService<IVersaoDoAgenteRepository>().AdicionarAsync(versao);
        await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        return versao.Id;
    }

    // Tarefa de escrita pelo caminho de domínio, com o seu trabalho (o contrato da /tarefa … escrever).
    internal static async Task<(Execucao Execucao, EtapaDaExecucao Etapa)> TarefaDeEscritaAsync(IServiceProvider provider,
        Guid versao, string chave)
    {
        await using var escopo = provider.CreateAsyncScope();
        var s = escopo.ServiceProvider;
        var fila = s.GetRequiredService<IFilaDeTrabalho>();
        var numero = await s.GetRequiredService<IExecucaoRepository>().ReservarNumeroAsync();
        var execucao = new Execucao(numero, TipoDeExecucao.Tarefa, "telegram", Dono,
            """{"modo":"Repository","alias":"@dante","diretorio":"/repos/dante","vinculados":[]}""", "corrija", fila.Agora, versao);
        var etapa = new EtapaDaExecucao(execucao, 1, TipoDeEtapa.TurnoDeAgente, true, PoliticaDeRetomada.ComConfirmacao,
            fila.Agora, AgentKind.Codex);
        await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(execucao);
        await s.GetRequiredService<IEtapaDaExecucaoRepository>().AdicionarAsync(etapa);
        await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(execucao, etapa, chave, fila.Agora));
        await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        return (execucao, etapa);
    }
}
