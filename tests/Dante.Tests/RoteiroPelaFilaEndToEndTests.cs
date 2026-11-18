using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dante.Application.Agentes;
using Dante.Application.Execucoes;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Domain.Grupos;
using Dante.Infrastructure.Contextos;
using Dante.Worker.Coordenacao;
using Dante.Worker.Execucoes;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using static Dante.Tests.FilaDuravelPostgreSqlTests;

namespace Dante.Tests;

// #308 de ponta a ponta contra PostgreSQL descartável (DANTE_TEST_POSTGRES), Git real e drivers falsos: /grupo grava o
// roteiro, o ProcessadorDaFila real executa um passo por trabalho com o ExecutorDeRoteirosDaFila, cada passo numa
// sessão Codex reservada e separada (escrita confinada na worktree; revisão somente leitura com o dante_coordenacao),
// e o revisor falso registra o veredito pelo canal real da sessão (pipe e token), como o processo MCP faria. O fim é o
// gate humano pelo /execucao. Nenhuma CLI real roda e nenhum agente faz commit.
[Collection("FilaDuravel")]
public sealed class RoteiroPelaFilaEndToEndTests
{
    private const string CriarDev = "/agente criar codex-dev driver=codex model=fake-mini modo=auto maximo=auto escrita=sim " +
        "capacidades=implementar repos=@first\nImplemente com testes.";
    private const string CriarRev = "/agente criar codex-rev driver=codex model=fake-mini capacidades=revisar repos=@first\nRevise.";

    [PostgreSqlFact]
    public async Task RoteiroImplementaRevisaCorrigeChegaAoGateEAprovacaoPreservaAWorktreeSemCommit()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco);
        var impressao = RepositorioGitDeTeste.Impressao(a.Primeiro);
        await a.IniciarAsync();
        await a.GrupoAtivoAsync(rodadas: 2);

        var confirmacao = await a.Send("/grupo eng @first crie o endpoint de saúde", 10);
        Assert.Matches(@"^Roteiro E\d+ enfileirado: grupo eng v1", confirmacao);
        Assert.Contains("Nenhum agente faz commit, push, PR ou merge.", confirmacao);
        var codigo = Regex.Match(confirmacao, @"E\d+").Value;
        var worktree = Path.Combine(a.RaizDasWorktrees, "first", codigo);

        // 1. implementar: escrita confinada só na worktree, sem ferramentas, perfil padrão da versão.
        var implementador = await a.DriverAsync(c => c.StartsWith("turn:Tarefa do grupo eng para o papel dev"));
        var opcoes = implementador.StartOptions!;
        Assert.Equal((true, false, AgentPermissionProfile.Auto, worktree),
            (opcoes.EscritaConfinada, opcoes.LeituraConfinada, opcoes.Profile, opcoes.WorkingDirectory));
        Assert.Empty(opcoes.ToolServers ?? []);
        Assert.Equal([a.Primeiro], opcoes.ProjetosNaoConfiaveis);
        // Sem o ambiente do Worker: o lançador fica só com a allowlist mínima.
        Assert.Empty(opcoes.EnvironmentVariables!);
        Assert.Contains("crie o endpoint de saúde", implementador.Calls.Single(c => c.StartsWith("turn:")));
        File.WriteAllText(Path.Combine(worktree, "saude.txt"), "GET /saude\n");
        Concluir(implementador, "Endpoint criado.");

        // 2. revisar: sessão separada, somente leitura, só o dante_coordenacao; o diff vai no prompt.
        var revisor = await a.DriverAsync(c => c.StartsWith("turn:Revisão da rodada 0"), implementador);
        var leitura = revisor.StartOptions!;
        Assert.Equal((true, false, AgentPermissionProfile.Plan, worktree),
            (leitura.LeituraConfinada, leitura.EscritaConfinada, leitura.Profile, leitura.WorkingDirectory));
        Assert.Equal([ServidorMcpDeCoordenacao.Nome], (leitura.ToolServers ?? []).Select(s => s.Name));
        Assert.Empty(leitura.EnvironmentVariables!);
        Assert.Contains("saude.txt", revisor.Calls.Single(c => c.StartsWith("turn:")));
        var resposta = await EntregarAsync(revisor, "requer_correcoes", "falta o teste", ["adicione o teste do endpoint"]);
        Assert.Contains("registrado", resposta);
        // Entregar de novo o mesmo veredito é idempotente; outro é recusado.
        Assert.Contains("ja_registrado", await EntregarAsync(revisor, "requer_correcoes", "falta o teste", ["adicione o teste do endpoint"]));
        Assert.Contains("não muda", await EntregarAsync(revisor, "aprovado", "mudei de ideia", []));
        Concluir(revisor, "Veredito registrado.");

        // 3. corrigir (rodada 1): escrita confinada na mesma worktree, com o item do revisor.
        var corretor = await a.DriverAsync(c => c.StartsWith("turn:Rodada de correção 1"), revisor);
        Assert.Equal((true, worktree), (corretor.StartOptions!.EscritaConfinada, corretor.StartOptions.WorkingDirectory));
        Assert.Contains("adicione o teste do endpoint", corretor.Calls.Single(c => c.StartsWith("turn:")));
        File.WriteAllText(Path.Combine(worktree, "saude.test.txt"), "teste\n");
        Concluir(corretor, "Teste adicionado.");

        // 4. revisar (rodada 1) e aprovar: o gate abre com o hash do diff revisado.
        var segundoRevisor = await a.DriverAsync(c => c.StartsWith("turn:Revisão da rodada 1"), corretor);
        Assert.Contains("saude.test.txt", segundoRevisor.Calls.Single(c => c.StartsWith("turn:")));
        await EntregarAsync(segundoRevisor, "aprovado", "pronto", []);
        Concluir(segundoRevisor, "Aprovado.");
        var aviso = await a.MensagemAsync(m => m.Contains("aguarda sua decisão"));
        var hash = Regex.Match(aviso, @"hash ([0-9a-f]{12})").Groups[1].Value;
        Assert.Contains($"/execucao aprovar {codigo} {hash}", aviso);
        Assert.Contains("Aprovar não faz commit, push, PR nem merge.", aviso);

        // Quatro sessões distintas, todas encerradas; nenhuma aprovação interativa concedida.
        Assert.Equal(4, a.Drivers.Created.Count);
        Assert.All(a.Drivers.Created, d => Assert.True(d.Disposed));
        var vereditos = await a.VereditosAsync(codigo);
        Assert.Equal([(0, VereditoDeRevisao.RequerCorrecoes), (1, VereditoDeRevisao.Aprovado)],
            vereditos.Select(v => (v.Rodada, v.Veredito)));
        Assert.StartsWith(hash, vereditos[1].HashDoArtefato);

        Assert.Contains("aprovada (rodada 1) e encerrada", await a.Send($"/execucao aprovar {codigo} {hash}", 11));
        // Sem commit: a branch da execução continua na base, e as alterações ficam na worktree.
        Assert.Equal(RepositorioGitDeTeste.Git(a.Primeiro, "rev-parse", "HEAD"), RepositorioGitDeTeste.Git(worktree, "rev-parse", "HEAD"));
        Assert.True(File.Exists(Path.Combine(worktree, "saude.test.txt")));
        Assert.False(File.Exists(Path.Combine(a.Primeiro, "saude.txt")));
        Assert.Equal(impressao, RepositorioGitDeTeste.Impressao(a.Primeiro));
        var estado = await ExecucaoAsync(a.Provider, long.Parse(codigo[1..]));
        Assert.Equal(EstadoDaExecucao.Concluida, estado.Estado);
    }

    // Review do PR #316 de ponta a ponta: o revisor aprovou H e o gate abriu; a worktree muda para H2; aprovar com H não
    // decide nada (gate pendente); /execucao revisar faz uma nova revisão, numa sessão nova, do conteúdo atual (a
    // reserva continua aguardando a decisão); o novo gate é de H2, e só então a aprovação vale.
    [PostgreSqlFact]
    public async Task ArtefatoAlteradoNoGateExigeNovaRevisaoAntesDaDecisao()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco);
        await a.IniciarAsync();
        await a.GrupoAtivoAsync(rodadas: 1);
        var codigo = Regex.Match(await a.Send("/grupo eng @first crie o arquivo", 10), @"E\d+").Value;
        var worktree = Path.Combine(a.RaizDasWorktrees, "first", codigo);
        var implementador = await a.DriverAsync(c => c.StartsWith("turn:Tarefa do grupo"));
        File.WriteAllText(Path.Combine(worktree, "a.txt"), "versão revisada\n");
        Concluir(implementador, "Feito.");
        var revisor = await a.DriverAsync(c => c.StartsWith("turn:Revisão da rodada 0"), implementador);
        await EntregarAsync(revisor, "aprovado", "ok", []);
        Concluir(revisor, "ok");
        var h = Regex.Match(await a.MensagemAsync(m => m.Contains("aguarda sua decisão")), @"hash ([0-9a-f]{12})").Groups[1].Value;

        File.WriteAllText(Path.Combine(worktree, "a.txt"), "alterada depois da revisão\n");
        var divergencia = await a.Send($"/execucao aprovar {codigo} {h}", 11);
        Assert.Contains("mudou depois da revisão", divergencia);
        Assert.Equal(EstadoDaExecucao.AguardandoAprovacao, (await ExecucaoAsync(a.Provider, long.Parse(codigo[1..]))).Estado);
        Assert.Contains("nova revisão da rodada 0", await a.Send($"/execucao revisar {codigo}", 12));

        var segundo = await a.DriverAsync(c => c.StartsWith("turn:Revisão da rodada 0"), revisor);
        Assert.True(segundo.StartOptions!.LeituraConfinada);
        // O prompt da nova revisão é do conteúdo atual: outro hash, o arquivo novo a ler na worktree.
        var prompt = segundo.Calls.Single(c => c.StartsWith("turn:"));
        Assert.Contains("a.txt", prompt);
        Assert.DoesNotContain($"hash {h}", prompt);
        await EntregarAsync(segundo, "aprovado", "ok de novo", []);
        Concluir(segundo, "ok");
        var novoAviso = await a.MensagemAsync(m => m.Contains("aguarda sua decisão") && !m.Contains($"hash {h}"));
        var h2 = Regex.Match(novoAviso, @"hash ([0-9a-f]{12})").Groups[1].Value;
        Assert.NotEqual(h, h2);
        Assert.Contains("aprovada (rodada 0) e encerrada", await a.Send($"/execucao aprovar {codigo} {h2}", 13));
        Assert.Equal(EstadoDaExecucao.Concluida, (await ExecucaoAsync(a.Provider, long.Parse(codigo[1..]))).Estado);
        Assert.Equal(2, (await a.VereditosAsync(codigo)).Count);
    }

    [PostgreSqlFact]
    public async Task RevisorSemVereditoRecebeUmLembreteEDepoisAEtapaFalhaSemAprovar()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco);
        await a.IniciarAsync();
        await a.GrupoAtivoAsync(rodadas: 2);
        var codigo = Regex.Match(await a.Send("/grupo eng @first crie o arquivo", 10), @"E\d+").Value;
        var implementador = await a.DriverAsync(c => c.StartsWith("turn:Tarefa do grupo"));
        File.WriteAllText(Path.Combine(a.RaizDasWorktrees, "first", codigo, "a.txt"), "a\n");
        Concluir(implementador, "Feito.");
        var revisor = await a.DriverAsync(c => c.StartsWith("turn:Revisão da rodada 0"), implementador);
        // Texto livre dizendo "aprovado" não registra nada.
        Concluir(revisor, "Aprovado, está tudo certo.");
        await Eventually(() => revisor.Calls.Any(c => c.Contains("ainda não registrou o veredito")));
        Concluir(revisor, "Já disse que está aprovado.");
        Assert.Contains("O revisor terminou sem registrar o veredito", await a.MensagemAsync(m => m.Contains("falhou")));
        Assert.Equal(EstadoDaExecucao.Falhou, (await ExecucaoAsync(a.Provider, long.Parse(codigo[1..]))).Estado);
        Assert.Empty(await a.VereditosAsync(codigo));
    }

    [PostgreSqlFact]
    public async Task RodadasEsgotadasBloqueiamComMotivoEOGateNuncaAbre()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco);
        await a.IniciarAsync();
        await a.GrupoAtivoAsync(rodadas: 1);
        var codigo = Regex.Match(await a.Send("/grupo eng @first crie o arquivo", 10), @"E\d+").Value;
        var worktree = Path.Combine(a.RaizDasWorktrees, "first", codigo);
        FakeSessionDriver? anterior = await a.DriverAsync(c => c.StartsWith("turn:Tarefa do grupo"));
        File.WriteAllText(Path.Combine(worktree, "a.txt"), "a\n");
        Concluir(anterior, "Feito.");
        anterior = await a.DriverAsync(c => c.StartsWith("turn:Revisão da rodada 0"), anterior);
        await EntregarAsync(anterior, "requer_correcoes", "errado", ["corrija a"]);
        Concluir(anterior, "ok");
        anterior = await a.DriverAsync(c => c.StartsWith("turn:Rodada de correção 1"), anterior);
        File.WriteAllText(Path.Combine(worktree, "a.txt"), "a2\n");
        Concluir(anterior, "Corrigido.");
        anterior = await a.DriverAsync(c => c.StartsWith("turn:Revisão da rodada 1"), anterior);
        await EntregarAsync(anterior, "requer_correcoes", "ainda errado", ["corrija de novo"]);
        Concluir(anterior, "ok");
        var bloqueio = await a.MensagemAsync(m => m.Contains("bloqueado"));
        Assert.Contains("Limite de 1 rodada(s) de correção atingido", bloqueio);
        var execucao = await ExecucaoAsync(a.Provider, long.Parse(codigo[1..]));
        Assert.Equal(EstadoDaExecucao.Bloqueada, execucao.Estado);
        Assert.Equal(0L, await Escalar(banco, "SELECT count(*) FROM orquestracao.pedidos_de_aprovacao"));
        Assert.True(File.Exists(Path.Combine(worktree, "a.txt")));
    }

    // Reinício: o processo que executa não tem o objetivo original, protegido pela redação. A execução pede um novo,
    // sanitizado, e continua com ele; o bruto nunca chega ao ledger nem ao agente depois disso.
    [PostgreSqlFact]
    public async Task ObjetivoProtegidoSemOOriginalEmMemoriaPedeUmNovoEContinuaComEle()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco, executorSeparado: true);
        await a.IniciarAsync();
        await a.GrupoAtivoAsync(rodadas: 1);
        var codigo = Regex.Match(await a.Send("/grupo eng @first use token=supersecreto123 para publicar", 10), @"E\d+").Value;
        var pedido = await a.MensagemAsync(m => m.Contains("Envie-a de novo"));
        Assert.Contains($"/execucao objetivo {codigo}", pedido);
        Assert.Empty(a.Drivers.Created);
        Assert.Equal(EstadoDaExecucao.AguardandoEntrada, (await ExecucaoAsync(a.Provider, long.Parse(codigo[1..]))).Estado);
        Assert.Contains("ainda parece conter uma credencial", await a.Send($"/execucao objetivo {codigo} token=outrosegredo99", 11));
        Assert.Contains("tarefa recebida", await a.Send($"/execucao objetivo {codigo} publique lendo a credencial do ambiente", 12));
        var implementador = await a.DriverAsync(c => c.StartsWith("turn:Tarefa do grupo"));
        var turno = implementador.Calls.Single(c => c.StartsWith("turn:"));
        Assert.Contains("publique lendo a credencial do ambiente", turno);
        Assert.DoesNotContain("supersecreto", turno);
        Assert.Equal(0L, await Escalar(banco, "SELECT count(*) FROM orquestracao.roteiros_da_execucao r WHERE r::text LIKE '%supersecreto%'"));
    }

    // O veredito é do caso de uso confiável, no escopo da sessão: etapa errada, rodada errada, hash errado, credencial e
    // revisão encerrada são recusados; o banco recusa alterar ou remover um veredito.
    [PostgreSqlFact]
    public async Task VereditoSoNoEscopoDaRevisaoEmCursoESemCredencial()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco);
        await a.IniciarAsync();
        await a.GrupoAtivoAsync(rodadas: 1);
        var codigo = Regex.Match(await a.Send("/grupo eng @first crie o arquivo", 10), @"E\d+").Value;
        var implementador = await a.DriverAsync(c => c.StartsWith("turn:Tarefa do grupo"));
        File.WriteAllText(Path.Combine(a.RaizDasWorktrees, "first", codigo, "a.txt"), "a\n");
        Concluir(implementador, "Feito.");
        var revisor = await a.DriverAsync(c => c.StartsWith("turn:Revisão da rodada 0"), implementador);
        var roteiro = (await a.RoteirosAsync(r => r.ObterDoProprietarioAsync(new ProprietarioDaExecucao("telegram", 123), codigo)))!;
        var revisao = roteiro.Etapas.Single(e => e.Passo == TipoDePassoDoRoteiro.Revisar);
        var hash = Regex.Match(revisor.Calls.Single(c => c.StartsWith("turn:")), @"hash ([0-9a-f]{12})").Groups[1].Value;
        var completo = (await a.RoteirosAsync(r => r.ObterAsync(roteiro.IdExecucao)))!;
        var implementacao = completo.Etapas.First();
        foreach (var escopo in new[]
                 {
                     new EscopoDaRevisaoDto(roteiro.IdExecucao, codigo, implementacao.IdEtapa, 0, new string('a', 64)),
                     new EscopoDaRevisaoDto(roteiro.IdExecucao, codigo, revisao.IdEtapa, 1, new string('a', 64)),
                     new EscopoDaRevisaoDto(Guid.NewGuid(), codigo, revisao.IdEtapa, 0, new string('a', 64)),
                     new EscopoDaRevisaoDto(roteiro.IdExecucao, codigo, revisao.IdEtapa, 0, "curto")
                 })
            await Assert.ThrowsAsync<InvalidOperationException>(() => a.RoteirosAsync(r => r.RegistrarVereditoAsync(escopo,
                new VereditoInformadoDto(VereditoDeRevisao.Aprovado, "ok", []), "S1")));
        Assert.Contains("credencial", await EntregarAsync(revisor, "aprovado", "use password=abcd1234 em produção", []));
        Assert.Contains("item", await EntregarAsync(revisor, "requer_correcoes", "falta algo", []));
        Assert.Contains("registrado", await EntregarAsync(revisor, "aprovado", "ok", []));
        Concluir(revisor, "ok");
        await a.MensagemAsync(m => m.Contains("aguarda sua decisão"));
        var gravado = Assert.Single(await a.VereditosAsync(codigo));
        Assert.StartsWith(hash, gravado.HashDoArtefato);
        foreach (var sql in new[] { "UPDATE orquestracao.vereditos_de_revisao SET veredito = 1", "DELETE FROM orquestracao.vereditos_de_revisao" })
            Assert.Equal(PostgresErrorCodes.RestrictViolation,
                (await Assert.ThrowsAsync<PostgresException>(() => Executar(banco, sql))).SqlState);
        // Revisão encerrada: o mesmo escopo não registra mais nada.
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.RoteirosAsync(r => r.RegistrarVereditoAsync(
            new EscopoDaRevisaoDto(roteiro.IdExecucao, codigo, revisao.IdEtapa, 0, gravado.HashDoArtefato),
            new VereditoInformadoDto(VereditoDeRevisao.Aprovado, "ok", []), "S1")));
    }

    [PostgreSqlFact]
    public async Task GrupoIndisponivelOuSemRepositorioNaoCriaNada()
    {
        await using var banco = await FilaAsync();
        await using var a = await Ambiente.CriarAsync(banco);
        await a.IniciarAsync();
        Assert.Contains("não cadastrado", await a.Send("/grupo eng @first faça", 1));
        await a.GrupoAtivoAsync(rodadas: 1, ativar: false);
        Assert.Contains("não tem versão ativada", await a.Send("/grupo eng @first faça", 20));
        // Sem @alias não é execução: é o uso do cadastro.
        Assert.StartsWith("Uso: /grupo", await a.Send("/grupo eng faça", 21));
        Assert.Equal(0L, await Escalar(banco, "SELECT count(*) FROM orquestracao.execucoes"));
        Assert.Empty(a.Drivers.Created);
    }

    private static void Concluir(FakeSessionDriver driver, string mensagem)
    {
        driver.Emit(new MessageCompletedEvent("m", mensagem));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
    }

    // Faz o que o processo MCP do dante_coordenacao faria: conecta uma vez no pipe da sessão, apresenta o token e chama
    // a ferramenta quantas vezes precisar pela mesma conexão.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FakeSessionDriver, NamedPipeClientStream> Canais = [];

    private static async Task<string> EntregarAsync(FakeSessionDriver revisor, string veredito, string resumo, string[] itens)
    {
        var servidor = Assert.Single(revisor.StartOptions!.ToolServers!);
        if (!Canais.TryGetValue(revisor, out var pipe))
        {
            pipe = new NamedPipeClientStream(".", servidor.Arguments[^1], PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(10_000);
            await EscreverAsync(pipe, new { token = servidor.Environment[ServidorMcpDeCoordenacao.VariavelDoToken] });
            Canais.Add(revisor, pipe);
        }
        await EscreverAsync(pipe, new { nome = "entregar_resultado", argumentos = new { veredito, resumo, itens } });
        var resposta = await LerAsync(pipe) ?? throw new IOException("canal fechado");
        return (string)System.Text.Json.Nodes.JsonNode.Parse(resposta)!["content"]![0]!["text"]!;
    }

    private static async Task EscreverAsync(Stream pipe, object mensagem)
    {
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(mensagem) + "\n"));
        await pipe.FlushAsync();
    }

    private static async Task<string?> LerAsync(Stream pipe)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await pipe.ReadAsync(buffer, prazo.Token) == 1)
        {
            if (buffer[0] == '\n') return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(buffer[0]);
        }
        return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static async Task Eventually(Func<bool> condicao)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!condicao())
        {
            if (DateTime.UtcNow > limite) throw new TimeoutException("Condição não atingida a tempo.");
            await Task.Delay(25);
        }
    }

    private sealed class Ambiente : IAsyncDisposable
    {
        private readonly string raiz = Path.Combine(Path.GetTempPath(), "dante-roteiro-" + Guid.NewGuid().ToString("N"));
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> recebidas = new();
        private TelegramPollingService? servico;
        private ProcessadorDaFila? processador;
        private ExecutorDeRoteirosDaFila? executorDoTelegram;
        private ExecutorDeRoteirosDaFila? executorDaFila;
        private bool executorSeparado;
        private FerramentasDeCoordenacao? coordenacao;

        public ServiceProvider Provider { get; private set; } = null!;
        public IServiceScopeFactory Escopos => Provider.GetRequiredService<IServiceScopeFactory>();
        public EspelhoDoLedger Espelho { get; private set; } = null!;
        public JobRegistry Jobs { get; private set; } = null!;
        public SinalDaFila Sinal { get; } = new();
        public TarefaPelaFilaEndToEndTests.BotDeTeste Api { get; } = new();
        public FakeSessionDriverFactory Drivers { get; } = new();
        public CatalogoDeModelosFixo Catalogo { get; } = new();
        public SessionRegistry Sessoes { get; private set; } = null!;
        public string Primeiro { get; private set; } = "";
        public RepositoryRegistry Registro { get; private set; } = null!;
        public string RaizDasWorktrees => Path.Combine(raiz, "worktrees");
        public OpcoesDeWorktrees OpcoesDeWorktrees => new() { Raiz = RaizDasWorktrees, EsperaPorCapacidade = TimeSpan.FromMilliseconds(300) };

        // executorSeparado: quem recebe o pedido e quem executa são processos diferentes (o executor não tem o
        // objetivo original em memória, como depois de um reinício).
        public static Task<Ambiente> CriarAsync(Banco banco, bool executorSeparado = false)
        {
            var ambiente = new Ambiente { executorSeparado = executorSeparado };
            ambiente.Registro = new RepositoryRegistry(Path.Combine(ambiente.raiz, "config", "repositories.json"));
            ambiente.Primeiro = ambiente.Git("first");
            ambiente.Registro.Add("@first", ambiente.Primeiro);
            ambiente.Provider = Provider(banco.ConnectionString, configurar: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IAgentModelCatalog>(ambiente.Catalogo));
                services.Replace(ServiceDescriptor.Singleton<Dante.Application.Contextos.ICatalogoDeRepositorios>(ambiente.Registro));
                services.AddSingleton(ambiente.OpcoesDeWorktrees);
            });
            ambiente.Espelho = new EspelhoDoLedger(ambiente.Escopos, NullLogger<EspelhoDoLedger>.Instance);
            ambiente.Jobs = new JobRegistry(ambiente.Espelho);
            ambiente.coordenacao = new FerramentasDeCoordenacao(ambiente.Escopos, NullLogger<FerramentasDeCoordenacao>.Instance);
            ambiente.Sessoes = new SessionRegistry(ambiente.Drivers, NullLogger<SessionRegistry>.Instance,
                new TelegramDeliveryService(ambiente.Api, NullLogger<TelegramDeliveryService>.Instance),
                toolServers: new CompositorDeFerramentas([ambiente.coordenacao]), ledger: ambiente.Espelho);
            return Task.FromResult(ambiente);
        }

        public async Task IniciarAsync()
        {
            var options = Options.Create(new TelegramOptions { BotToken = "test", AllowedUserIds = "123" });
            var delivery = new TelegramDeliveryService(Api, NullLogger<TelegramDeliveryService>.Instance);
            var gerenciador = Provider.GetRequiredService<IGerenciadorDeWorktrees>();
            ExecutorDeRoteirosDaFila Novo() => new(Escopos, () => Sessoes, Espelho, Sinal, NullLogger<ExecutorDeRoteirosDaFila>.Instance,
                Api, IdentidadeDoProcessador.DoProcessoAtual(), gerenciador);
            executorDoTelegram = Novo();
            executorDaFila = executorSeparado ? Novo() : executorDoTelegram;
            var runner = new Runner();
            servico = new TelegramPollingService(Api, options, new TelegramUserAuthorizer(options), runner, runner, Jobs,
                NullLogger<TelegramPollingService>.Instance, Registro, new GeneralWorkspace(Path.Combine(raiz, "general")),
                new AssistantSettingsStore(Path.Combine(raiz, "settings.json")), Sessoes, delivery, Catalogo,
                agentes: new TelegramAgentes(Escopos), execucoes: new TelegramExecucoes(Escopos, NullLogger<TelegramExecucoes>.Instance),
                grupos: new TelegramGrupos(Escopos), filaDeRoteiros: executorDoTelegram);
            await servico.StartAsync(CancellationToken.None);
            processador = new ProcessadorDaFila(Escopos, IdentidadeDoProcessador.DoProcessoAtual(), Rapidas, Sinal, [executorDaFila],
                new MetricasDaFila(), NullLogger<ProcessadorDaFila>.Instance, worktrees: OpcoesDeWorktrees);
            await processador.StartAsync(CancellationToken.None);
        }

        public async Task GrupoAtivoAsync(int rodadas, bool ativar = true)
        {
            Assert.Contains("criado (v1)", await Send(CriarDev, 1));
            Assert.Contains("criado (v1)", await Send(CriarRev, 2));
            var roteiro = rodadas == 0 ? "implementar:dev,revisar:rev,aprovar" : "implementar:dev,revisar:rev,corrigir:dev,aprovar";
            var criado = await Send($"/grupo criar eng roteiro={roteiro} rodadas={rodadas} repos=@first\n" +
                "dev=codex-dev escrita: implementa\nrev=codex-rev: revisa", 3);
            Assert.Contains("Grupo eng criado (v1)", criado);
            if (!ativar) return;
            var comando = criado.Split('\n').Single(l => l.StartsWith("Para usá-la", StringComparison.Ordinal));
            Assert.Contains("v1 ativada", await Send(comando[comando.IndexOf("/grupo ativar", StringComparison.Ordinal)..], 4));
        }

        public async Task<string> Send(string texto, long idDaMensagem)
        {
            Api.Enviar(texto, idDaMensagem);
            return await Api.ProximaAsync();
        }

        // Próxima mensagem que atende à condição; as outras (avisos intermediários) são guardadas e conferidas antes.
        public async Task<string> MensagemAsync(Func<string, bool> condicao)
        {
            var limite = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                foreach (var guardada in recebidas)
                    if (condicao(guardada)) return guardada;
                if (DateTime.UtcNow > limite) throw new TimeoutException("Mensagem não recebida: " + string.Join(" | ", recebidas));
                try { recebidas.Enqueue(await Api.ProximaAsync()); }
                catch (TimeoutException) { }
            }
        }

        public async Task<FakeSessionDriver> DriverAsync(Func<string, bool> chamada, FakeSessionDriver? depoisDe = null)
        {
            FakeSessionDriver? achado = null;
            await Eventually(() => (achado = Drivers.Created.FirstOrDefault(d => d != depoisDe && d.Calls.Any(chamada) &&
                (depoisDe is null || Drivers.Created.ToList().IndexOf(d) > Drivers.Created.ToList().IndexOf(depoisDe)))) is not null);
            return achado!;
        }

        public async Task<T> RoteirosAsync<T>(Func<IRoteirosAppService, Task<T>> acao)
        {
            await using var escopo = Provider.CreateAsyncScope();
            return await acao(escopo.ServiceProvider.GetRequiredService<IRoteirosAppService>());
        }

        public async Task<IReadOnlyList<VereditoRegistrado>> VereditosAsync(string codigo)
        {
            await using var escopo = Provider.CreateAsyncScope();
            var execucao = await escopo.ServiceProvider.GetRequiredService<IExecucaoRepository>().ConsultarPorNumeroAsync(long.Parse(codigo[1..]));
            return await escopo.ServiceProvider.GetRequiredService<IVereditoRegistradoRepository>().ListarDaExecucaoAsync(execucao!.Id);
        }

        private string Git(string nome)
        {
            var caminho = Path.Combine(raiz, nome);
            Directory.CreateDirectory(caminho);
            RepositorioGitDeTeste.Git(caminho, "init", "-q");
            File.WriteAllText(Path.Combine(caminho, "README.md"), $"# {nome}\n");
            RepositorioGitDeTeste.Git(caminho, "add", "-A");
            RepositorioGitDeTeste.Git(caminho, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "inicial");
            return caminho;
        }

        public async ValueTask DisposeAsync()
        {
            if (servico is not null)
            {
                await servico.StopAsync(CancellationToken.None);
                servico.Dispose();
            }
            if (processador is not null) await processador.StopAsync(CancellationToken.None);
            await Sessoes.DisposeAsync();
            coordenacao?.Dispose();
            await Espelho.DisposeAsync();
            await Provider.DisposeAsync();
            if (Directory.Exists(raiz)) Directory.Delete(raiz, true);
        }
    }

    private sealed class Runner : ICodexRunner, IClaudeRunner
    {
        public Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Application.Anexos.Attachment>? attachments = null, IReadOnlyList<string>? additionalDirectories = null) =>
            Task.FromResult(new AgentProcessResult(AgentProcessStatus.Succeeded, "done", "", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }
}
