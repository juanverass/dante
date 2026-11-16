using Dante.Application.Agentes;
using Dante.Application.Comum;
using Dante.Domain.Agentes;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

// #301 (AD-68): caso de uso do cadastro sobre portas em memória e catálogo fixo de modelos.
public sealed class CadastroDeAgentesAppServiceTests : IDisposable
{
    private readonly CadastroDeAgentesEmMemoria portas = new();
    private readonly CatalogoDeModelosFixo catalogo = new();
    private readonly ServiceProvider provider;
    private readonly ICadastroDeAgentesAppService cadastro;

    public CadastroDeAgentesAppServiceTests()
    {
        provider = portas.Provider(catalogo);
        cadastro = provider.GetRequiredService<ICadastroDeAgentesAppService>();
    }

    public void Dispose() => provider.Dispose();

    [Fact]
    public async Task CriaConsultaEListaComEmbutidosPrimeiro()
    {
        var criado = await cadastro.CriarAsync(Novo("Revisor", new ConfiguracaoDoAgenteDto
        {
            Driver = AgentKind.Claude, Modelo = "OPUS", Esforco = "HIGH", Instrucoes = "Revise com rigor.",
            Capacidades = [CapacidadeDoAgente.Revisar], Repositorios = ["@dante"]
        }));
        Assert.Equal(("revisor", 1, EstadoDoAgente.Ativo), (criado.Handle, criado.NumeroDaVersaoAtual, criado.Estado));
        // Nomes como o catálogo os conhece.
        Assert.Equal(("opus", "high"), (criado.VersaoAtual!.Configuracao.Modelo, criado.VersaoAtual.Configuracao.Esforco));
        Assert.Equal(AgentPermissionProfile.Plan, criado.VersaoAtual.Configuracao.PerfilMaximo);
        var lido = (await cadastro.ObterAsync("REVISOR"))!;
        Assert.Equal(criado.VersaoAtual.HashDaConfiguracao, lido.VersaoAtual!.HashDaConfiguracao);
        Assert.Single(lido.Versoes);
        Assert.Equal(["claude", "codex", "revisor"], (await cadastro.ListarAsync()).Select(agente => agente.Handle));
        Assert.True((await cadastro.ObterAsync("codex"))!.Embutido);
        Assert.Null(await cadastro.ObterAsync("inexistente"));
    }

    [Fact]
    public async Task HandleDuplicadoEHandleArquivadoNuncaSaoReutilizados()
    {
        await cadastro.CriarAsync(Novo("revisor"));
        Assert.Contains("Já existe", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.CriarAsync(Novo("revisor")))).Message);
        await cadastro.DesativarAsync("revisor");
        await cadastro.ArquivarAsync("revisor");
        Assert.Contains("não é reutilizado", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.CriarAsync(Novo("revisor")))).Message);
        Assert.Empty((await cadastro.ListarAsync()).Where(agente => !agente.Embutido));
        Assert.Single((await cadastro.ListarAsync(incluirArquivados: true)).Where(agente => !agente.Embutido));
        // @, reservados e embutidos são recusados com mensagem clara, sem o nome do parâmetro.
        Assert.Equal("Handle de agente não usa @: @ é sempre repositório.",
            (await Assert.ThrowsAsync<ArgumentException>(() => cadastro.CriarAsync(Novo("@dev")))).Message);
        Assert.Contains("reservado", (await Assert.ThrowsAsync<ArgumentException>(() => cadastro.CriarAsync(Novo("codex")))).Message);
        Assert.Contains("reservado", (await Assert.ThrowsAsync<ArgumentException>(() => cadastro.CriarAsync(Novo("plan")))).Message);
    }

    [Fact]
    public async Task CorridaPeloMesmoHandleViraConflitoSemCriarDois()
    {
        // A segunda criação passa pela consulta antes da primeira confirmar; o índice único decide.
        var primeira = cadastro.CriarAsync(Novo("dev"));
        await primeira;
        await using var escopo = provider.CreateAsyncScope();
        var portasDiretas = (IAgenteRepository)portas;
        var agente = new Agente("dev", null, null, 1, DateTimeOffset.UtcNow);
        await portasDiretas.AdicionarAsync(agente);
        await Assert.ThrowsAsync<ConflitoDeConcorrenciaException>(() => portas.SalvarAlteracoesAsync());
        Assert.Single(await portas.ListarAsync(true));
    }

    [Fact]
    public async Task EditarCriaVersaoNovaImutavelERecusaQuandoNadaMuda()
    {
        var v1 = await cadastro.CriarAsync(Novo("revisor", new ConfiguracaoDoAgenteDto
        {
            Driver = AgentKind.Claude, Modelo = "sonnet", Esforco = "low", Instrucoes = "v1"
        }));
        var v2 = await cadastro.EditarAsync("revisor", new AlteracaoDoAgenteDto { Instrucoes = "v2" }, 9);
        Assert.Equal(2, v2.NumeroDaVersaoAtual);
        Assert.Equal(("sonnet", "low", "v2"), (v2.VersaoAtual!.Configuracao.Modelo, v2.VersaoAtual.Configuracao.Esforco,
            v2.VersaoAtual.Configuracao.Instrucoes));
        Assert.Equal(9, v2.VersaoAtual.IdDoAutor);
        var lido = (await cadastro.ObterAsync("revisor"))!;
        Assert.Equal([2, 1], lido.Versoes.Select(versao => versao.Numero));
        Assert.Equal("v1", lido.Versoes[1].Configuracao.Instrucoes);
        Assert.Equal(v1.VersaoAtual!.HashDaConfiguracao, lido.Versoes[1].HashDaConfiguracao);
        Assert.Contains("Nada mudou", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.EditarAsync("revisor", new AlteracaoDoAgenteDto { Instrucoes = "  v2 " }, 9))).Message);

        // Trocar de driver sem novo modelo volta modelo e esforço ao padrão: nada passa de um driver a outro.
        var codex = await cadastro.EditarAsync("revisor", new AlteracaoDoAgenteDto { Driver = AgentKind.Codex }, 9);
        Assert.Equal((AgentKind.Codex, null, null), (codex.VersaoAtual!.Configuracao.Driver,
            codex.VersaoAtual.Configuracao.Modelo, codex.VersaoAtual.Configuracao.Esforco));
        var semInstrucoes = await cadastro.EditarAsync("revisor", new AlteracaoDoAgenteDto { LimparInstrucoes = true }, 9);
        Assert.Null(semInstrucoes.VersaoAtual!.Configuracao.Instrucoes);
        // Subir o máximo além de plan concede escrita; o padrão continua o anterior.
        var manual = await cadastro.EditarAsync("revisor", new AlteracaoDoAgenteDto
        {
            PerfilMaximo = AgentPermissionProfile.Manual, ExecucoesSimultaneas = 2
        }, 9);
        Assert.Equal((AgentPermissionProfile.Plan, AgentPermissionProfile.Manual, true, 2),
            (manual.VersaoAtual!.Configuracao.PerfilPadrao, manual.VersaoAtual.Configuracao.PerfilMaximo,
                manual.VersaoAtual.Configuracao.PodeEscrever, manual.VersaoAtual.Configuracao.Limites.ExecucoesSimultaneas));
        var semLimite = await cadastro.EditarAsync("revisor", new AlteracaoDoAgenteDto { ExecucoesSimultaneas = 0 }, 9);
        Assert.Null(semLimite.VersaoAtual!.Configuracao.Limites.ExecucoesSimultaneas);
        Assert.Equal(6, (await cadastro.ObterAsync("revisor"))!.Versoes.Count);
        Assert.All(portas.Versoes, versao => Assert.True(versao.Integra));
    }

    [Fact]
    public async Task ModeloEEsforcoSaoValidadosNoCatalogoAoSalvar()
    {
        Assert.Contains("não é oferecido", (await Assert.ThrowsAsync<ArgumentException>(() => cadastro.CriarAsync(
            Novo("dev", new ConfiguracaoDoAgenteDto { Driver = AgentKind.Codex, Modelo = "opus" })))).Message);
        Assert.Contains("incompatível", (await Assert.ThrowsAsync<ArgumentException>(() => cadastro.CriarAsync(
            Novo("dev", new ConfiguracaoDoAgenteDto { Driver = AgentKind.Claude, Modelo = "sonnet", Esforco = "high" })))).Message);
        Assert.Contains("inválido", (await Assert.ThrowsAsync<ArgumentException>(() => cadastro.CriarAsync(
            Novo("dev", new ConfiguracaoDoAgenteDto { Driver = AgentKind.Claude, Modelo = "-m" })))).Message);
        // Sem modelo, o esforço é conferido no padrão do driver; o id resolvido também é aceito.
        var criado = await cadastro.CriarAsync(Novo("dev", new ConfiguracaoDoAgenteDto { Driver = AgentKind.Codex, Esforco = "xhigh" }));
        Assert.Equal((null, "xhigh"), (criado.VersaoAtual!.Configuracao.Modelo, criado.VersaoAtual.Configuracao.Esforco));
        var resolvido = await cadastro.CriarAsync(Novo("dev2", new ConfiguracaoDoAgenteDto { Driver = AgentKind.Claude, Modelo = "claude-opus-5-5" }));
        Assert.Equal("claude-opus-5-5", resolvido.VersaoAtual!.Configuracao.Modelo);
        catalogo.Falha = true;
        Assert.Contains("Não foi possível validar", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.CriarAsync(Novo("dev3", new ConfiguracaoDoAgenteDto { Driver = AgentKind.Claude, Modelo = "opus" })))).Message);
        // Sem modelo nem esforço, nada a validar.
        await cadastro.CriarAsync(Novo("dev4"));
    }

    [Fact]
    public async Task SegredoNuncaEntraNoCadastro()
    {
        foreach (var instrucoes in new[] { "use a senha=hunter2hunter2", "ghp_abcdefghijklmnopqrstuvwxyz0123",
                     "-----BEGIN OPENSSH PRIVATE KEY-----" })
            Assert.Contains("credencial", (await Assert.ThrowsAsync<ArgumentException>(() => cadastro.CriarAsync(
                Novo("dev", new ConfiguracaoDoAgenteDto { Driver = AgentKind.Claude, Instrucoes = instrucoes })))).Message);
        await cadastro.CriarAsync(Novo("dev"));
        await Assert.ThrowsAsync<ArgumentException>(() => cadastro.EditarAsync("dev",
            new AlteracaoDoAgenteDto { Instrucoes = "token: sk-abcdefghijklmnopqrstuvwxyz" }, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => cadastro.ApresentarAsync("dev", "api_key=12345678", null));
        Assert.Equal(0, portas.Versoes.Count(versao => versao.Instrucoes is not null));
    }

    [Fact]
    public async Task EstadosControlamAResolucaoSemFallback()
    {
        await cadastro.CriarAsync(Novo("revisor"));
        var ativo = await cadastro.ResolverAsync("Revisor");
        Assert.True(ativo.Resolvido);
        Assert.Equal(("revisor", 1), (ativo.Handle, ativo.Versao!.Numero));
        Assert.Equal(AgentKind.Claude, (await cadastro.ResolverAsync("claude")).DriverEmbutido);

        Assert.Equal(FalhaNaResolucaoDeAgente.Inexistente, (await cadastro.ResolverAsync("outro")).Falha);
        Assert.Equal(FalhaNaResolucaoDeAgente.HandleInvalido, (await cadastro.ResolverAsync("@revisor")).Falha);
        Assert.Equal(FalhaNaResolucaoDeAgente.HandleInvalido, (await cadastro.ResolverAsync("plan")).Falha);
        await cadastro.DesativarAsync("revisor");
        var desativado = await cadastro.ResolverAsync("revisor");
        Assert.Equal(FalhaNaResolucaoDeAgente.Desativado, desativado.Falha);
        Assert.Null(desativado.Versao);
        Assert.Contains("reative", desativado.Erro);
        // Editar um desativado é permitido (prepara a volta); reativar torna a nova versão a usada.
        await cadastro.EditarAsync("revisor", new AlteracaoDoAgenteDto { Instrucoes = "nova" }, 1);
        await cadastro.ReativarAsync("revisor");
        Assert.Equal(2, (await cadastro.ResolverAsync("revisor")).Versao!.Numero);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cadastro.ReativarAsync("revisor"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cadastro.ArquivarAsync("revisor"));
        await cadastro.DesativarAsync("revisor");
        portas.TrabalhoAtivo = 1;
        Assert.Contains("ainda tem 1", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.ArquivarAsync("revisor"))).Message);
        portas.TrabalhoAtivo = 0;
        await cadastro.ArquivarAsync("revisor");
        Assert.Equal(FalhaNaResolucaoDeAgente.Arquivado, (await cadastro.ResolverAsync("revisor")).Falha);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.EditarAsync("revisor", new AlteracaoDoAgenteDto { Instrucoes = "x" }, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cadastro.ApresentarAsync("revisor", "x", null));
    }

    [Fact]
    public async Task EmbutidosNaoSaoEditaveis()
    {
        foreach (var embutido in new[] { "claude", "codex" })
        {
            Assert.Contains("não é editável", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cadastro.EditarAsync(embutido, new AlteracaoDoAgenteDto { Instrucoes = "x" }, 1))).Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => cadastro.DesativarAsync(embutido));
            await Assert.ThrowsAsync<InvalidOperationException>(() => cadastro.ArquivarAsync(embutido));
        }
        Assert.Equal(0, portas.Confirmacoes);
    }

    [Fact]
    public async Task NomeEDescricaoNaoCriamVersao()
    {
        await cadastro.CriarAsync(Novo("revisor"));
        var apresentado = await cadastro.ApresentarAsync("revisor", "Revisor de PR", "Lê diffs");
        Assert.Equal(("Revisor de PR", "Lê diffs", 1), (apresentado.Nome, apresentado.Descricao, apresentado.NumeroDaVersaoAtual));
        Assert.Single(portas.Versoes);
    }

    private static NovoAgenteDto Novo(string handle, ConfiguracaoDoAgenteDto? configuracao = null) =>
        new(handle, configuracao ?? new ConfiguracaoDoAgenteDto { Driver = AgentKind.Claude }, 1);
}
