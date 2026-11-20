using Dante.Application.Agentes;
using Dante.Application.Comum;
using Dante.Application.Grupos;
using Dante.Domain.Agentes;
using Dante.Domain.Grupos;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

// #308 (AD-69): caso de uso do cadastro de grupos sobre portas em memória. Os participantes são agentes registrados
// de verdade (cadastro de agentes real sobre as mesmas portas).
public sealed class CadastroDeGruposAppServiceTests : IDisposable
{
    private readonly CadastroDeGruposEmMemoria portas = new(new CadastroDeAgentesEmMemoria());
    private readonly ServiceProvider provider;
    private readonly ICadastroDeGruposAppService cadastro;
    private readonly ICadastroDeAgentesAppService agentes;

    public CadastroDeGruposAppServiceTests()
    {
        provider = portas.Provider();
        cadastro = provider.GetRequiredService<ICadastroDeGruposAppService>();
        agentes = provider.GetRequiredService<ICadastroDeAgentesAppService>();
    }

    public void Dispose() => provider.Dispose();

    internal static readonly PassoDoRoteiro[] Roteiro =
    [
        new(TipoDePassoDoRoteiro.Implementar, "dev"), new(TipoDePassoDoRoteiro.Revisar, "rev"),
        new(TipoDePassoDoRoteiro.Corrigir, "dev"), new(TipoDePassoDoRoteiro.AprovacaoHumana)
    ];

    internal static ConfiguracaoDoGrupoDto Configuracao(string dev = "codex-dev", string rev = "codex-rev",
        int rodadas = 2) => new()
    {
        Participantes = [new("dev", dev, true, "Implementa a tarefa."), new("rev", rev, false, "Revisa o diff.")],
        Roteiro = Roteiro,
        RodadasDeRevisaoMaximas = rodadas,
        Repositorios = ["@dante"]
    };

    internal static async Task CriarAgentesAsync(ICadastroDeAgentesAppService agentes)
    {
        await agentes.CriarAsync(new NovoAgenteDto("codex-dev", new ConfiguracaoDoAgenteDto
        {
            Driver = AgentKind.Codex, PodeEscrever = true, PerfilPadrao = AgentPermissionProfile.Auto,
            PerfilMaximo = AgentPermissionProfile.Auto, Capacidades = [CapacidadeDoAgente.Implementar],
            Repositorios = ["@dante"]
        }, 1));
        await agentes.CriarAsync(new NovoAgenteDto("codex-rev", new ConfiguracaoDoAgenteDto
        {
            Driver = AgentKind.Codex, Capacidades = [CapacidadeDoAgente.Revisar], Repositorios = ["@dante"]
        }, 1));
    }

    [Fact]
    public async Task CriaGrupoComV1NaoAtivadaEConsultaComHandles()
    {
        await CriarAgentesAsync(agentes);
        var criado = await cadastro.CriarAsync(new NovoGrupoDto("Engenharia", Configuracao(), 42, "Time de código"));
        Assert.Equal(("engenharia", 1, EstadoDoGrupo.Ativo, (int?)null), (criado.Nome, criado.NumeroDaVersaoAtual,
            criado.Estado, criado.NumeroDaVersaoAtiva));
        Assert.Null(criado.VersaoAtiva);
        var versao = criado.VersaoAtual!;
        Assert.True(versao.Integra);
        Assert.Equal(42L, versao.IdDoAutor);
        Assert.Equal([("dev", "codex-dev", true), ("rev", "codex-rev", false)],
            versao.Participantes.Select(p => (p.Papel, p.Handle, p.PodeEscrever)));
        var lido = (await cadastro.ObterAsync("ENGENHARIA"))!;
        Assert.Equal(versao.HashDaConfiguracao, lido.VersaoAtual!.HashDaConfiguracao);
        Assert.Single(lido.Versoes);
        Assert.Empty(lido.Ativacoes);
        Assert.Equal(["engenharia"], (await cadastro.ListarAsync()).Select(grupo => grupo.Nome));
        Assert.Null(await cadastro.ObterAsync("inexistente"));
    }

    // #310 (AD-75), PR 3: grupo no modo coordenador pelo cadastro. O modo vem do papel do coordenador; o domínio recusa
    // roteiro, rodadas e escrita; o cadastro exige alguém além do coordenador com capacidade para consulta.v1. A edição
    // mantém o modo da versão atual.
    [Fact]
    public async Task GrupoCoordenadoCriadoEditadoNoMesmoModoEConferidoPelasCapacidades()
    {
        await CriarAgentesAsync(agentes);
        await agentes.CriarAsync(new NovoAgenteDto("coord", new ConfiguracaoDoAgenteDto
        {
            Driver = AgentKind.Codex, Capacidades = [CapacidadeDoAgente.Planejar], Repositorios = ["@dante"]
        }, 1));
        await agentes.CriarAsync(new NovoAgenteDto("analista", new ConfiguracaoDoAgenteDto
        {
            Driver = AgentKind.Codex, Capacidades = [CapacidadeDoAgente.Planejar], Repositorios = ["@dante"]
        }, 1));
        ConfiguracaoDoGrupoDto Coordenado(params ParticipanteInformadoDto[] participantes) => new()
        {
            PapelDoCoordenador = "chefe", Participantes = participantes, Repositorios = ["@dante"]
        };
        // Ninguém além do coordenador recebe consulta.v1 (planejar não basta).
        var semQuemDelegar = await Assert.ThrowsAsync<InvalidOperationException>(() => cadastro.CriarAsync(new NovoGrupoDto(
            "pesquisa", Coordenado(new("chefe", "coord", false, null), new("outro", "analista", false, null)), 1)));
        Assert.Contains("não teria a quem delegar", semQuemDelegar.Message);
        // Escrita num grupo coordenado depende do gateway (#311): recusada pelo domínio.
        await Assert.ThrowsAsync<ArgumentException>(() => cadastro.CriarAsync(new NovoGrupoDto("pesquisa",
            Coordenado(new("chefe", "coord", false, null), new("dev", "codex-dev", true, null)), 1)));
        await Assert.ThrowsAsync<ArgumentException>(() => cadastro.CriarAsync(new NovoGrupoDto("pesquisa",
            Coordenado(new("chefe", "coord", false, null), new("rev", "codex-rev", false, null)) with { Roteiro = Roteiro }, 1)));

        var criado = await cadastro.CriarAsync(new NovoGrupoDto("pesquisa",
            Coordenado(new("chefe", "coord", false, "Planeja."), new("rev", "codex-rev", false, "Revisa.")), 1));
        var configuracao = criado.VersaoAtual!.Configuracao!;
        Assert.Equal((ModoDeCoordenacao.Coordenador, "chefe", 0), (configuracao.Modo, configuracao.PapelDoCoordenador,
            configuracao.Roteiro.Count));
        var ativado = await cadastro.AtivarAsync("pesquisa", 1, criado.VersaoAtual.HashDaConfiguracao, 1);
        var resolvido = await cadastro.ResolverParaExecucaoAsync("pesquisa");
        Assert.True(resolvido.Resolvido, resolvido.Erro);
        Assert.Equal(ModoDeCoordenacao.Coordenador, resolvido.Versao!.Configuracao!.Modo);
        Assert.Equal(1, ativado.NumeroDaVersaoAtiva);

        // A edição herda o modo. Trocar o coordenador para o único capaz de receber consulta.v1 deixaria o grupo sem a
        // quem delegar: recusado. Outra mudança mantém o modo e o coordenador; roteiro é recusado.
        Assert.Contains("não teria a quem delegar", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.EditarAsync("pesquisa", new AlteracaoDoGrupoDto { PapelDoCoordenador = "rev" }, 1))).Message);
        var editado = await cadastro.EditarAsync("pesquisa", new AlteracaoDoGrupoDto { DuracaoMaximaEmMinutos = 30 }, 1);
        Assert.Equal((ModoDeCoordenacao.Coordenador, "chefe", 30), (editado.VersaoAtual!.Configuracao!.Modo,
            editado.VersaoAtual.Configuracao.PapelDoCoordenador, editado.VersaoAtual.Configuracao.Politica.DuracaoMaximaEmMinutos));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            cadastro.EditarAsync("pesquisa", new AlteracaoDoGrupoDto { Roteiro = Roteiro }, 1));
        // Um grupo de roteiro não vira coordenado numa edição.
        await cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao(), 1));
        Assert.Contains("o modo não muda", (await Assert.ThrowsAsync<ArgumentException>(() =>
            cadastro.EditarAsync("eng", new AlteracaoDoGrupoDto { PapelDoCoordenador = "rev" }, 1))).Message);
    }

    [Fact]
    public async Task NomeDuplicadoOuArquivadoNuncaEReutilizado()
    {
        await CriarAgentesAsync(agentes);
        await cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao(), 1));
        Assert.Contains("Já existe", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao(), 1)))).Message);
        await cadastro.DesativarAsync("eng");
        await cadastro.ArquivarAsync("eng");
        Assert.Contains("não é reutilizado", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao(), 1)))).Message);
        Assert.Empty(await cadastro.ListarAsync());
        Assert.Single(await cadastro.ListarAsync(incluirArquivados: true));
        Assert.Contains("reservado", (await Assert.ThrowsAsync<ArgumentException>(() =>
            cadastro.CriarAsync(new NovoGrupoDto("ativar", Configuracao(), 1)))).Message);
    }

    [Fact]
    public async Task ParticipantesSaoConferidosContraOsAgentesSemInferirNada()
    {
        await CriarAgentesAsync(agentes);
        // Embutido, inexistente e desativado.
        Assert.Contains("embutido", (await Assert.ThrowsAsync<ArgumentException>(() =>
            cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao(dev: "codex"), 1)))).Message);
        Assert.Contains("não cadastrado", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao(dev: "fantasma"), 1)))).Message);
        // Revisor sem a capacidade revisar; implementador com agente sem escrita.
        Assert.Contains("capacidade revisar", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao(rev: "codex-dev"), 1)))).Message);
        Assert.Contains("não tem escrita", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao(dev: "codex-rev"), 1)))).Message);
        // Claude não escreve (AD-71) nem revisa em sandbox somente leitura nesta etapa.
        await agentes.CriarAsync(new NovoAgenteDto("claude-dev", new ConfiguracaoDoAgenteDto
        {
            Driver = AgentKind.Claude, PodeEscrever = true, PerfilMaximo = AgentPermissionProfile.Auto,
            Capacidades = [CapacidadeDoAgente.Implementar]
        }, 1));
        await agentes.CriarAsync(new NovoAgenteDto("claude-rev", new ConfiguracaoDoAgenteDto
        {
            Driver = AgentKind.Claude, Capacidades = [CapacidadeDoAgente.Revisar]
        }, 1));
        Assert.Contains("Escrita via Claude está desabilitada", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao(dev: "claude-dev"), 1)))).Message);
        Assert.Contains("sessão Codex", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao(rev: "claude-rev"), 1)))).Message);
        await agentes.DesativarAsync("codex-rev");
        Assert.Contains("desativado", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao(), 1)))).Message);
        // Credencial nunca entra em papel nem responsabilidades.
        await agentes.ReativarAsync("codex-rev");
        Assert.Contains("credencial", (await Assert.ThrowsAsync<ArgumentException>(() =>
            cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao() with
            {
                Participantes = [new("dev", "codex-dev", true, "use ghp_0123456789abcdefghijABCDEFGHIJ0123456789"),
                    new("rev", "codex-rev", false)]
            }, 1)))).Message);
        Assert.Empty(await cadastro.ListarAsync(incluirArquivados: true));
    }

    [Fact]
    public async Task EditarCriaVersaoNovaSemTrocarAAtivaERecusaSemMudanca()
    {
        await CriarAgentesAsync(agentes);
        var v1 = (await cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao(), 1))).VersaoAtual!;
        await cadastro.AtivarAsync("eng", 1, v1.HashDaConfiguracao[..12], 5);
        Assert.Contains("Nada mudou", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.EditarAsync("eng", new AlteracaoDoGrupoDto(), 2))).Message);
        var editado = await cadastro.EditarAsync("eng", new AlteracaoDoGrupoDto { RodadasDeRevisaoMaximas = 3 }, 2);
        Assert.Equal((2, (int?)1), (editado.NumeroDaVersaoAtual, editado.NumeroDaVersaoAtiva));
        Assert.Equal(v1.Id, editado.VersaoAtiva!.Id);
        Assert.Equal(3, editado.VersaoAtual!.Configuracao!.Politica.RodadasDeRevisaoMaximas);
        // Participantes herdados; trocar a forma do roteiro ajusta as rodadas quando não informadas.
        var semCorrecao = await cadastro.EditarAsync("eng", new AlteracaoDoGrupoDto
        {
            Roteiro = [Roteiro[0], Roteiro[1], Roteiro[3]]
        }, 2);
        Assert.Equal((3, 0), (semCorrecao.NumeroDaVersaoAtual, semCorrecao.VersaoAtual!.Configuracao!.Politica.RodadasDeRevisaoMaximas));
        Assert.Equal(["dev", "rev"], semCorrecao.VersaoAtual.Participantes.Select(p => p.Papel));
        // A versão já gravada não muda.
        var lido = (await cadastro.ObterAsync("eng"))!;
        Assert.Equal([3, 2, 1], lido.Versoes.Select(versao => versao.Numero));
        Assert.Equal(v1.HashDaConfiguracao, lido.Versoes[2].HashDaConfiguracao);
        Assert.All(portas.Versoes, versao => Assert.True(versao.Integra));
    }

    [Fact]
    public async Task AtivacaoEDecisaoHumanaVinculadaAoHashEReconfereOsAgentes()
    {
        await CriarAgentesAsync(agentes);
        var v1 = (await cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao(), 1))).VersaoAtual!;
        Assert.Contains("hash confirmado", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.AtivarAsync("eng", 1, new string('a', 12), 5))).Message);
        Assert.Contains("não tem a versão 2", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.AtivarAsync("eng", 2, v1.HashDaConfiguracao, 5))).Message);
        // O agente mudou depois de salvar a versão: perdeu a capacidade exigida, a ativação recusa.
        await agentes.EditarAsync("codex-rev", new AlteracaoDoAgenteDto { Capacidades = [CapacidadeDoAgente.Pesquisar] }, 1);
        Assert.Contains("capacidade revisar", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.AtivarAsync("eng", 1, v1.HashDaConfiguracao, 5))).Message);
        await agentes.EditarAsync("codex-rev", new AlteracaoDoAgenteDto { Capacidades = [CapacidadeDoAgente.Revisar] }, 1);
        var ativado = await cadastro.AtivarAsync("eng", 1, v1.HashDaConfiguracao, 5);
        Assert.Equal((int?)1, ativado.NumeroDaVersaoAtiva);
        var decisao = Assert.Single((await cadastro.ObterAsync("eng"))!.Ativacoes);
        Assert.Equal((1, v1.HashDaConfiguracao, 5L), (decisao.NumeroDaVersao, decisao.HashAprovado, decisao.IdDoAutor));
        Assert.Contains("já está ativa", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.AtivarAsync("eng", 1, v1.HashDaConfiguracao, 5))).Message);
        Assert.Single(portas.Ativacoes);
    }

    [Fact]
    public async Task ResolverParaExecucaoCongelaVersoesAtuaisDosAgentesSemSubstituir()
    {
        await CriarAgentesAsync(agentes);
        Assert.Equal(FalhaNaResolucaoDeGrupo.Inexistente, (await cadastro.ResolverParaExecucaoAsync("eng")).Falha);
        Assert.Equal(FalhaNaResolucaoDeGrupo.NomeInvalido, (await cadastro.ResolverParaExecucaoAsync("@eng")).Falha);
        var v1 = (await cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao(), 1))).VersaoAtual!;
        var semAtiva = await cadastro.ResolverParaExecucaoAsync("eng");
        Assert.Equal(FalhaNaResolucaoDeGrupo.SemVersaoAtiva, semAtiva.Falha);
        Assert.Contains("/grupo ativar eng", semAtiva.Erro);
        await cadastro.AtivarAsync("eng", 1, v1.HashDaConfiguracao, 5);
        var resolvido = await cadastro.ResolverParaExecucaoAsync("eng");
        Assert.True(resolvido.Resolvido);
        Assert.Equal(v1.Id, resolvido.Versao!.Id);
        Assert.Equal([("dev", "codex-dev", 1), ("rev", "codex-rev", 1)],
            resolvido.Participantes.Select(p => (p.Papel, p.Handle, p.VersaoDoAgente.Numero)));
        // O agente ganhou uma versão nova: a próxima resolução congela a nova; a anterior já resolvida não muda.
        await agentes.EditarAsync("codex-dev", new AlteracaoDoAgenteDto { Instrucoes = "Testes primeiro." }, 1);
        var depois = await cadastro.ResolverParaExecucaoAsync("eng");
        Assert.Equal(2, depois.Participantes[0].VersaoDoAgente.Numero);
        Assert.Equal(1, resolvido.Participantes[0].VersaoDoAgente.Numero);
        // Uma versão nova do grupo, não ativada, não é usada.
        await cadastro.EditarAsync("eng", new AlteracaoDoGrupoDto { RodadasDeRevisaoMaximas = 1 }, 1);
        Assert.Equal(v1.Id, (await cadastro.ResolverParaExecucaoAsync("eng")).Versao!.Id);
        // Participante desativado: recusa com motivo, sem cair para outro agente.
        await agentes.DesativarAsync("codex-rev");
        var recusa = await cadastro.ResolverParaExecucaoAsync("eng");
        Assert.Equal(FalhaNaResolucaoDeGrupo.ParticipanteInvalido, recusa.Falha);
        Assert.Contains("codex-rev", recusa.Erro);
        await agentes.ReativarAsync("codex-rev");
        // Grupo desativado e arquivado.
        await cadastro.DesativarAsync("eng");
        Assert.Equal(FalhaNaResolucaoDeGrupo.Desativado, (await cadastro.ResolverParaExecucaoAsync("eng")).Falha);
        await cadastro.ArquivarAsync("eng");
        Assert.Equal(FalhaNaResolucaoDeGrupo.Arquivado, (await cadastro.ResolverParaExecucaoAsync("eng")).Falha);
    }

    [Fact]
    public async Task VersaoAtivaAdulteradaNaoEResolvida()
    {
        await CriarAgentesAsync(agentes);
        var v1 = (await cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao(), 1))).VersaoAtual!;
        await cadastro.AtivarAsync("eng", 1, v1.HashDaConfiguracao, 5);
        var gravada = portas.Versoes.Single();
        typeof(VersaoDoGrupo).GetProperty(nameof(VersaoDoGrupo.RodadasDeRevisaoMaximas))!.SetValue(gravada, 5);
        var resolucao = await cadastro.ResolverParaExecucaoAsync("eng");
        Assert.Equal(FalhaNaResolucaoDeGrupo.Indisponivel, resolucao.Falha);
        Assert.False((await cadastro.ObterAsync("eng"))!.VersaoAtual!.Integra);
    }

    [Fact]
    public async Task CicloDeVidaRecusaTransicoesForaDeOrdem()
    {
        await CriarAgentesAsync(agentes);
        await cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao(), 1));
        Assert.Contains("Desative", (await Assert.ThrowsAsync<InvalidOperationException>(() => cadastro.ArquivarAsync("eng"))).Message);
        Assert.Contains("já está ativo", (await Assert.ThrowsAsync<InvalidOperationException>(() => cadastro.ReativarAsync("eng"))).Message);
        await cadastro.DesativarAsync("eng");
        Assert.Contains("não está ativo", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.AtivarAsync("eng", 1, new string('a', 64), 1))).Message);
        await cadastro.ArquivarAsync("eng");
        Assert.Contains("arquivado", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.EditarAsync("eng", new AlteracaoDoGrupoDto { RodadasDeRevisaoMaximas = 1 }, 1))).Message);
        Assert.Contains("arquivado", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.DescreverAsync("eng", "x"))).Message);
        Assert.Contains("não cadastrado", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cadastro.DesativarAsync("outro"))).Message);
    }

    [Fact]
    public async Task EdicoesSimultaneasGravamUmaVersaoSo()
    {
        await CriarAgentesAsync(agentes);
        await cadastro.CriarAsync(new NovoGrupoDto("eng", Configuracao(), 1));
        // Duas leituras do mesmo estado (v1) produzem a mesma v2; só a primeira grava.
        var (grupoA, grupoB) = (await LerAsync(), await LerAsync());
        var configuracao = portas.Versoes.Single().Configuracao!;
        var mudada = new ConfiguracaoDoGrupo(configuracao.Modo, configuracao.Participantes, configuracao.Roteiro,
            configuracao.Politica with { RodadasDeRevisaoMaximas = 4 });
        var a = grupoA.NovaVersao(mudada, 1, DateTimeOffset.UtcNow);
        var b = grupoB.NovaVersao(mudada, 1, DateTimeOffset.UtcNow);
        Assert.Equal(a.Numero, b.Numero);
        await portas.AdicionarAsync(a);
        await portas.SalvarAlteracoesAsync();
        await portas.AdicionarAsync(b);
        await Assert.ThrowsAsync<ConflitoDeConcorrenciaException>(() => portas.SalvarAlteracoesAsync());
        Assert.Equal(2, portas.Versoes.Count);

        // Cópia rasa do grupo gravado, como cada escopo leria do banco.
        async Task<GrupoDeAgentes> LerAsync()
        {
            var gravado = (await portas.ObterPorNomeAsync("eng"))!;
            return (GrupoDeAgentes)typeof(object).GetMethod("MemberwiseClone",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(gravado, null)!;
        }
    }
}
