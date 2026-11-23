using System.Text.Json;
using System.Text.RegularExpressions;
using Dante.Application.Comum;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Contextos;
using Dante.Worker.Execucoes;
using Dante.Worker.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using static Dante.Tests.FilaDuravelPostgreSqlTests;

namespace Dante.Tests;

// #311, PR 3 de ponta a ponta: depois do gate do roteiro, o dono pede a entrega (/execucao entregar), aprova a ação no
// gateway (/acoes) e o D.A.N.T.E. faz exatamente um commit local na branch da worktree, pelo Git fechado, com o autor
// configurado; nunca no checkout principal e nunca com push. PostgreSQL descartável, Git real e drivers falsos.
public sealed partial class RoteiroPelaFilaEndToEndTests
{
    internal static readonly AutorDoCommit AutorDeTeste = new("Dante Bot", "dante@example.com");

    [PostgreSqlFact]
    public async Task EntregaAprovadaNoGatewayFazUmCommitNaBranchDaWorktreeSemPushNemCheckoutPrincipal()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco);
        var remoto = Remoto(a.Primeiro);
        var impressao = RepositorioGitDeTeste.Impressao(a.Primeiro);
        var refsDoRemoto = RepositorioGitDeTeste.Git(remoto, "for-each-ref");
        await a.IniciarAsync();
        var (codigo, worktree) = await GateAprovadoAsync(a);
        var baseDoCommit = RepositorioGitDeTeste.Git(worktree, "rev-parse", "HEAD");

        var pedido = await a.Send($"/execucao entregar {codigo} feat: endpoint de saúde", 20);
        Assert.Contains($"Entrega de {codigo} pedida", pedido);
        Assert.Contains("dante.entregar_artefato_do_roteiro (escrita local) em @first", pedido);
        Assert.Contains("• autor_email: dante@example.com", pedido);
        Assert.Contains($"• base: {baseDoCommit}", pedido);
        Assert.Contains("• mensagem: feat: endpoint de saúde", pedido);
        var (id, hash) = Comando(pedido);
        // Pedir de novo devolve o mesmo pedido; outra mensagem com um pedido em aberto é recusada.
        Assert.Contains($"Entrega de {codigo} já pedida", await a.Send($"/execucao entregar {codigo}", 21));
        Assert.Contains("Recuse-o em /acoes", await a.Send($"/execucao entregar {codigo} outra mensagem", 22));
        // O gate não basta: até a aprovação da entrega, nada é commitado.
        Assert.Equal(baseDoCommit, RepositorioGitDeTeste.Git(worktree, "rev-parse", "HEAD"));
        Assert.Contains("O hash confirmado não é o da ação pedida", await a.Send($"/acoes aprovar {id} 000000000000", 23));

        var execucao = await a.Send($"/acoes aprovar {id} {hash}", 24);

        Assert.Contains($"Ação {id} aprovada", execucao);
        var sha = Regex.Match(execucao, @"executada: Commit ([0-9a-f]{40}) na branch (dante/codex-dev/e\d+)");
        Assert.True(sha.Success, execucao);
        Assert.Equal(sha.Groups[1].Value, RepositorioGitDeTeste.Git(worktree, "rev-parse", "HEAD"));
        Assert.Equal(sha.Groups[2].Value, RepositorioGitDeTeste.Git(worktree, "branch", "--show-current"));
        Assert.Equal(baseDoCommit, RepositorioGitDeTeste.Git(worktree, "rev-parse", "HEAD^1"));
        Assert.Equal("1", RepositorioGitDeTeste.Git(worktree, "rev-list", "--count", $"{baseDoCommit}..HEAD"));
        Assert.Equal("Dante Bot <dante@example.com>|Dante Bot <dante@example.com>|feat: endpoint de saúde",
            RepositorioGitDeTeste.Git(worktree, "log", "-1", "--format=%an <%ae>|%cn <%ce>|%B"));
        Assert.Equal("GET /saude", RepositorioGitDeTeste.Git(worktree, "show", "HEAD:saude.txt"));
        Assert.Equal("", RepositorioGitDeTeste.Git(worktree, "status", "--porcelain", "--untracked-files=all"));
        // Checkout principal byte a byte igual e nada no remoto.
        Assert.Equal(impressao, RepositorioGitDeTeste.Impressao(a.Primeiro));
        Assert.Equal(refsDoRemoto, RepositorioGitDeTeste.Git(remoto, "for-each-ref"));
        Assert.Equal("", RepositorioGitDeTeste.Git(a.Primeiro, "for-each-ref", "refs/remotes"));

        // Auditável no ledger: decisor, hash, prazos e o resultado com o SHA, sem conteúdo bruto do diff.
        var linha = await LinhaAsync(banco, $"SELECT estado, id_do_decisor, decidida_em IS NOT NULL AND iniciada_em IS NOT NULL " +
            "AND encerrada_em IS NOT NULL AND expira_em > criada_em, resultado, payload FROM orquestracao.aprovacoes_de_acao");
        Assert.Equal((int)EstadoDaAprovacaoDeAcao.Executada, linha[0]);
        Assert.Equal(123L, linha[1]);
        Assert.Equal(true, linha[2]);
        Assert.Contains(sha.Groups[1].Value, (string)linha[3]!);
        Assert.DoesNotContain("GET /saude", (string)linha[4]!);

        // Executada uma vez: nem o pedido nem a aprovação repetem.
        Assert.Contains($"{codigo} já foi entregue: Commit {sha.Groups[1].Value}", await a.Send($"/execucao entregar {codigo}", 25));
        Assert.Contains("não encontrado", await a.Send($"/acoes aprovar {id} {hash}", 26));
        Assert.Equal("1", RepositorioGitDeTeste.Git(worktree, "rev-list", "--count", $"{baseDoCommit}..HEAD"));
    }

    [PostgreSqlFact]
    public async Task ConteudoAlteradoDepoisDoPedidoCancelaAEntregaSemCommit()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco);
        await a.IniciarAsync();
        var (codigo, worktree) = await GateAprovadoAsync(a);
        var baseDoCommit = RepositorioGitDeTeste.Git(worktree, "rev-parse", "HEAD");
        var (id, hash) = Comando(await a.Send($"/execucao entregar {codigo}", 20));

        File.AppendAllText(Path.Combine(worktree, "saude.txt"), "alterado depois do pedido\n");
        var execucao = await a.Send($"/acoes aprovar {id} {hash}", 21);

        Assert.Contains("o conteúdo ou os fatos mudaram depois da aprovação", execucao);
        Assert.Equal(baseDoCommit, RepositorioGitDeTeste.Git(worktree, "rev-parse", "HEAD"));
        Assert.Equal("?? saude.txt", RepositorioGitDeTeste.Git(worktree, "status", "--porcelain"));
        Assert.Equal((int)EstadoDaAprovacaoDeAcao.Cancelada,
            (await LinhaAsync(banco, "SELECT estado FROM orquestracao.aprovacoes_de_acao"))[0]);
        // Um pedido novo também é recusado: o conteúdo não é mais o aprovado no gate.
        Assert.Contains("mudou depois da aprovação no gate", await a.Send($"/execucao entregar {codigo}", 22));
    }

    [PostgreSqlFact]
    public async Task FalhaDoGitNoMeioNaoCommitaEUmPedidoNovoEntregaDepois()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco);
        await a.IniciarAsync();
        var (codigo, worktree) = await GateAprovadoAsync(a);
        var baseDoCommit = RepositorioGitDeTeste.Git(worktree, "rev-parse", "HEAD");
        var (id, hash) = Comando(await a.Send($"/execucao entregar {codigo}", 20));
        // O index da worktree está travado (outro processo Git): o add falha e nada é gravado.
        var trava = Path.Combine(RepositorioGitDeTeste.Git(worktree, "rev-parse", "--absolute-git-dir"), "index.lock");
        File.WriteAllText(trava, "");

        var falha = await a.Send($"/acoes aprovar {id} {hash}", 21);

        Assert.Contains("não teve efeito: O Git recusou preparar as alterações", falha);
        Assert.Equal(baseDoCommit, RepositorioGitDeTeste.Git(worktree, "rev-parse", "HEAD"));
        Assert.Equal((int)EstadoDaAprovacaoDeAcao.Falhou,
            (await LinhaAsync(banco, "SELECT estado FROM orquestracao.aprovacoes_de_acao"))[0]);

        File.Delete(trava);
        var (novoId, novoHash) = Comando(await a.Send($"/execucao entregar {codigo}", 22));
        Assert.NotEqual(id, novoId);
        Assert.Equal(hash, novoHash);
        Assert.Contains("executada: Commit", await a.Send($"/acoes aprovar {novoId} {novoHash}", 23));
        Assert.Equal("1", RepositorioGitDeTeste.Git(worktree, "rev-list", "--count", $"{baseDoCommit}..HEAD"));
    }

    [PostgreSqlFact]
    public async Task SemIdentidadeGitConfiguradaNadaEPedido()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco, entrega: new OpcoesDaEntrega());
        await a.IniciarAsync();
        var (codigo, worktree) = await GateAprovadoAsync(a);

        Assert.Equal(EntregaDoArtefatoAppService.SemIdentidade, await a.Send($"/execucao entregar {codigo}", 20));

        Assert.Equal(0L, await Escalar(banco, "SELECT count(*) FROM orquestracao.aprovacoes_de_acao"));
        Assert.Equal(RepositorioGitDeTeste.Git(a.Primeiro, "rev-parse", "HEAD"), RepositorioGitDeTeste.Git(worktree, "rev-parse", "HEAD"));
        Assert.Equal("?? saude.txt", RepositorioGitDeTeste.Git(worktree, "status", "--porcelain"));
    }

    // Reinício com o commit gravado e o desfecho ainda não: a recuperação marca a entrega Incerta com o estado real
    // (um commit além da base), ninguém a repete e o pedido seguinte é recusado com esse estado.
    [PostgreSqlFact]
    public async Task ReinicioNoMeioDaEntregaFicaIncertoComOEstadoRealENuncaRepete()
    {
        await using var banco = await FilaAsync();
        var trava = new CommitQueTrava();
        await using var a = await Ambiente.CriarAsync(banco, configurar: services =>
            services.Replace(ServiceDescriptor.Singleton<ICommitDeWorktree>(provider =>
                trava.Com(provider.GetRequiredService<GerenciadorDeWorktreesGit>()))));
        await a.IniciarAsync();
        var (codigo, worktree) = await GateAprovadoAsync(a);
        var baseDoCommit = RepositorioGitDeTeste.Git(worktree, "rev-parse", "HEAD");
        var (idCurto, hash) = Comando(await a.Send($"/execucao entregar {codigo}", 20));
        var id = (Guid)(await LinhaAsync(banco, "SELECT id FROM orquestracao.aprovacoes_de_acao"))[0]!;
        var dono = new ProprietarioDaExecucao(LedgerChannel.Telegram, 123);
        // Aprovada pelo gateway, sem a execução imediata do adaptador.
        await NoEscopoAsync(a, s => s.GetRequiredService<IAprovacoesDeAcaoAppService>().AprovarAsync(dono, id, hash));

        var interrompida = Task.Run(() => NoEscopoAsync(a, s =>
            s.GetRequiredService<IEntregaDoArtefatoAppService>().ExecutarAprovadaAsync(dono, id)));
        await trava.CommitGravado.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal((int)EstadoDaAprovacaoDeAcao.EmExecucao,
            (await LinhaAsync(banco, "SELECT estado FROM orquestracao.aprovacoes_de_acao"))[0]);

        // "Reinício": o processo que executava não existe mais.
        var manutencao = await NoEscopoAsync(a, s => s.GetRequiredService<IAprovacoesDeAcaoAppService>().RecuperarAposReinicioAsync());
        var incerta = Assert.Single(manutencao.Incertas);
        Assert.Equal(EstadoDaAprovacaoDeAcao.Incerto, incerta.Estado);
        Assert.Contains($"1 commit(s) além da base {baseDoCommit[..12]}", incerta.Resultado);
        var aviso = new TelegramAprovacoesDeAcao(a.Escopos, a.Api, NullLogger<TelegramAprovacoesDeAcao>.Instance);
        await aviso.AvisarAsync(incerta, CancellationToken.None);
        Assert.Contains("Estado observado: HEAD", await a.MensagemAsync(m => m.Contains("interrompida por um reinício")));

        // O processo antigo "volta": o desfecho dele não sobrescreve o Incerto.
        trava.Liberar.SetResult();
        await Assert.ThrowsAsync<ConflitoDeConcorrenciaException>(() => interrompida);
        Assert.Equal((int)EstadoDaAprovacaoDeAcao.Incerto,
            (await LinhaAsync(banco, "SELECT estado FROM orquestracao.aprovacoes_de_acao"))[0]);
        var recusa = await a.Send($"/execucao entregar {codigo}", 21);
        Assert.Contains("ficou incerta e não será repetida", recusa);
        Assert.Contains("1 commit(s) além da base", recusa);
        Assert.Contains("não encontrado", await a.Send($"/acoes aprovar {idCurto} {hash}", 22));
        Assert.Equal("1", RepositorioGitDeTeste.Git(worktree, "rev-list", "--count", $"{baseDoCommit}..HEAD"));
    }

    // Duas aprovações da mesma entrega (mensagens diferentes) e a mesma aprovação executada duas vezes, ao mesmo tempo:
    // exatamente um commit e uma execução. A mesma aprovação não começa duas vezes (xmin); a outra aprovação ou vê o
    // HEAD já movido (payload alterado) ou perde o compare-and-swap da branch no Git (sem efeito).
    [PostgreSqlFact]
    public async Task ExecucoesConcorrentesDaMesmaEntregaProduzemUmCommit()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco);
        await a.IniciarAsync();
        var (codigo, worktree) = await GateAprovadoAsync(a);
        var baseDoCommit = RepositorioGitDeTeste.Git(worktree, "rev-parse", "HEAD");
        var (_, hash) = Comando(await a.Send($"/execucao entregar {codigo} primeira", 20));
        var linha = await LinhaAsync(banco, "SELECT id, id_execucao, id_etapa, payload FROM orquestracao.aprovacoes_de_acao");
        var (primeira, idExecucao, idEtapa) = ((Guid)linha[0]!, (Guid)linha[1]!, (Guid)linha[2]!);
        var dono = new ProprietarioDaExecucao(LedgerChannel.Telegram, 123);
        // A segunda, com outra mensagem, pedida direto ao gateway (o caso de uso recusaria um segundo pedido em aberto).
        var parametros = JsonSerializer.Deserialize<Dictionary<string, string>>(
            JsonDocument.Parse((string)linha[3]!).RootElement.GetProperty("parametros").GetRawText())!;
        parametros["mensagem"] = "segunda";
        var segunda = await NoEscopoAsync(a, s => s.GetRequiredService<IAprovacoesDeAcaoAppService>().SolicitarAsync(
            new SolicitacaoDeAcaoDto(CatalogoDeAcoes.EntregarArtefatoDoRoteiro.Nome, idExecucao, idEtapa, "@first",
                EntregaDoArtefatoAppService.Capacidades, JsonSerializer.Serialize(parametros), dono)));
        Assert.True(segunda.Novo);
        foreach (var (idDoPedido, hashDoPedido) in new[] { (primeira, hash), (segunda.Aprovacao.Id, segunda.Aprovacao.HashCurto) })
            Assert.Equal(DesfechoDaDecisaoDeAcao.Aprovada, (await NoEscopoAsync(a, s =>
                s.GetRequiredService<IAprovacoesDeAcaoAppService>().AprovarAsync(dono, idDoPedido, hashDoPedido))).Desfecho);

        // Outra pessoa não executa a entrega de ninguém (indistinguível de inexistente).
        Assert.Null(await NoEscopoAsync(a, s => s.GetRequiredService<IEntregaDoArtefatoAppService>().ExecutarAprovadaAsync(
            new ProprietarioDaExecucao(LedgerChannel.Telegram, 999), primeira)));
        Assert.Equal(baseDoCommit, RepositorioGitDeTeste.Git(worktree, "rev-parse", "HEAD"));

        var tentativas = await Task.WhenAll(new[] { primeira, primeira, segunda.Aprovacao.Id, segunda.Aprovacao.Id }
            .Select(idDoPedido => Task.Run(() => NoEscopoAsync(a, s =>
                s.GetRequiredService<IEntregaDoArtefatoAppService>().ExecutarAprovadaAsync(dono, idDoPedido)))));

        Assert.Single(tentativas, t => t!.Resultado == ResultadoDaExecucaoDeAcao.Executada);
        Assert.All(tentativas.Where(t => t!.Resultado != ResultadoDaExecucaoDeAcao.Executada), t => Assert.Contains(t!.Resultado,
            new[] { ResultadoDaExecucaoDeAcao.JaIniciada, ResultadoDaExecucaoDeAcao.NaoAutorizada,
                ResultadoDaExecucaoDeAcao.PayloadAlterado, ResultadoDaExecucaoDeAcao.Falhou }));
        Assert.Equal("1", RepositorioGitDeTeste.Git(worktree, "rev-list", "--count", $"{baseDoCommit}..HEAD"));
        Assert.Equal(1L, await Escalar(banco, "SELECT count(*) FROM orquestracao.aprovacoes_de_acao WHERE estado = 6"));
        Assert.Equal(0L, await Escalar(banco, "SELECT count(*) FROM orquestracao.aprovacoes_de_acao WHERE estado IN (5, 8)"));
    }

    // Nada configurado: sem autor e sem problema (a entrega diz que falta). Válida: o autor. Inválida: sem autor, com o
    // motivo (sem o valor).
    // Revisão do PR #326, ponto 2: pedidos simultâneos da mesma execução com mensagens diferentes. A execução fica
    // travada da leitura dos pedidos até a gravação, então só um pedido existe; os outros veem o pedido em aberto com
    // outra mensagem e são recusados. O gateway continua aceitando payloads diferentes (semântica da PR 2 intacta).
    [PostgreSqlFact]
    public async Task PedidosSimultaneosDaMesmaEntregaCriamUmSoPedido()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco);
        await a.IniciarAsync();
        var (codigo, _) = await GateAprovadoAsync(a);
        var dono = new ProprietarioDaExecucao(LedgerChannel.Telegram, 123);

        var tentativas = await Task.WhenAll(Enumerable.Range(1, 6).Select(i => Task.Run(async () =>
        {
            try
            {
                return (await NoEscopoAsync(a, s => s.GetRequiredService<IEntregaDoArtefatoAppService>()
                    .SolicitarAsync(dono, codigo, $"mensagem {i}"))).Resultado.ToString();
            }
            catch (InvalidOperationException recusa)
            {
                return recusa.Message;
            }
        })));

        Assert.Single(tentativas, t => t == nameof(ResultadoDaSolicitacaoDeEntrega.Solicitada));
        Assert.All(tentativas.Where(t => t != nameof(ResultadoDaSolicitacaoDeEntrega.Solicitada)),
            t => Assert.Contains("em aberto, com outra mensagem", t));
        Assert.Equal(1L, await Escalar(banco, "SELECT count(*) FROM orquestracao.aprovacoes_de_acao"));
    }

    [Theory]
    [InlineData(null, null, false, null)]
    [InlineData("Dante Bot", "dante@example.com", true, null)]
    [InlineData("Dante Bot", null, false, "e-mail do autor inválido")]
    [InlineData(null, "dante@example.com", false, "nome do autor inválido")]
    [InlineData("Nome <x>", "dante@example.com", false, "nome do autor inválido")]
    [InlineData(" Dante", "dante@example.com", false, "nome do autor inválido")]
    [InlineData("Dante", "dante@exemplo", false, "e-mail do autor inválido")]
    [InlineData("Dante", "a b@c.d", false, "e-mail do autor inválido")]
    public void IdentidadeDaEntregaEExplicitaEValidada(string? nome, string? email, bool valida, string? problema)
    {
        var opcoes = OpcoesDaEntrega.De(nome, email);
        Assert.Equal((valida, problema), (opcoes.Autor is not null, opcoes.ProblemaDaConfiguracao));
    }

    // ---- apoio ----

    // Roteiro de uma rodada até o gate aprovado, com saude.txt novo na worktree.
    private static async Task<(string Codigo, string Worktree)> GateAprovadoAsync(Ambiente a)
    {
        await a.GrupoAtivoAsync(rodadas: 0);
        var codigo = Regex.Match(await a.Send("/grupo eng @first crie o endpoint de saúde", 10), @"E\d+").Value;
        var worktree = Path.Combine(a.RaizDasWorktrees, "first", codigo);
        var implementador = await a.DriverAsync(c => c.StartsWith("turn:Tarefa do grupo", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(worktree, "saude.txt"), "GET /saude\n");
        Concluir(implementador, "Feito.");
        var revisor = await a.DriverAsync(c => c.StartsWith("turn:Revisão da rodada 0", StringComparison.Ordinal), implementador);
        await EntregarAsync(revisor, "aprovado", "ok", []);
        Concluir(revisor, "ok");
        var hash = Regex.Match(await a.MensagemAsync(m => m.Contains("aguarda sua decisão")), @"hash ([0-9a-f]{12})").Groups[1].Value;
        var aprovado = await a.Send($"/execucao aprovar {codigo} {hash}", 11);
        Assert.Contains($"/execucao entregar {codigo}", aprovado);
        return (codigo, worktree);
    }

    private static (string Id, string Hash) Comando(string texto)
    {
        var comando = Regex.Match(texto, @"/acoes aprovar ([0-9a-f]{8}) ([0-9a-f]{12})");
        Assert.True(comando.Success, texto);
        return (comando.Groups[1].Value, comando.Groups[2].Value);
    }

    private static async Task<T> NoEscopoAsync<T>(Ambiente a, Func<IServiceProvider, Task<T>> acao)
    {
        await using var escopo = a.Provider.CreateAsyncScope();
        return await acao(escopo.ServiceProvider);
    }

    private static async Task<object?[]> LinhaAsync(Banco banco, string sql)
    {
        await using var conexao = new Npgsql.NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new Npgsql.NpgsqlCommand(sql, conexao);
        await using var leitor = await comando.ExecuteReaderAsync();
        Assert.True(await leitor.ReadAsync(), "nenhuma linha");
        var valores = new object?[leitor.FieldCount];
        for (var i = 0; i < leitor.FieldCount; i++) valores[i] = leitor.IsDBNull(i) ? null : leitor.GetValue(i);
        Assert.False(await leitor.ReadAsync(), "mais de uma linha");
        return valores;
    }

    // Um remoto bare com a main publicada; a ref de rastreio do push de teste é apagada para a conferência final.
    private static string Remoto(string checkout)
    {
        var remoto = checkout + "-remoto.git";
        RepositorioGitDeTeste.Git(Path.GetDirectoryName(checkout)!, "init", "-q", "--bare", remoto);
        RepositorioGitDeTeste.Git(checkout, "remote", "add", "origin", remoto);
        RepositorioGitDeTeste.Git(checkout, "push", "-q", "origin", "HEAD:refs/heads/main");
        foreach (var referencia in RepositorioGitDeTeste.Git(checkout, "for-each-ref", "--format=%(refname)", "refs/remotes")
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            RepositorioGitDeTeste.Git(checkout, "update-ref", "-d", referencia);
        return remoto;
    }

    // O commit real acontece; depois, o "processo" para antes de devolver o desfecho, até o teste liberar.
    private sealed class CommitQueTrava
    {
        public TaskCompletionSource CommitGravado { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Liberar { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ICommitDeWorktree Com(GerenciadorDeWorktreesGit real) => new Decorador(real, this);

        private sealed class Decorador(GerenciadorDeWorktreesGit real, CommitQueTrava trava) : ICommitDeWorktree
        {
            public Task<string> LerHeadAsync(WorktreePlanejada worktree, CancellationToken cancellationToken = default) =>
                real.LerHeadAsync(worktree, cancellationToken);

            public async Task<DesfechoDoCommitDto> CommitarAsync(WorktreePlanejada worktree, CommitDoArtefatoDto commit,
                CancellationToken cancellationToken = default)
            {
                var desfecho = await real.CommitarAsync(worktree, commit, cancellationToken);
                Assert.IsType<DesfechoDoCommitDto.Commitado>(desfecho);
                trava.CommitGravado.SetResult();
                await trava.Liberar.Task;
                return desfecho;
            }

            public Task<string> DescreverEstadoAsync(WorktreePlanejada worktree, CancellationToken cancellationToken = default) =>
                real.DescreverEstadoAsync(worktree, cancellationToken);
        }
    }
}
