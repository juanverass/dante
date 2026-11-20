using System.Text.Json.Nodes;
using Dante.Application.Agentes;
using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Worker.Coordenacao;
using Dante.Worker.Execucoes;
using Dante.Worker.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

// #310 (AD-75), PR 3: o coordenador pelo dante_coordenacao existente (mesmo servidor, pipe, token e limite da #308/#309).
// Um perfil por etapa: planejamento (propor_plano) e síntese (entregar_sintese); o perfil é o da etapa que o orquestrador
// abriu, só em plan, e nunca lista ferramenta de outro perfil. Sem concessão no agente: é o único canal de saída da etapa,
// de mundo fechado. O fluxo real (validação, gravação sob posse, correção única) está em CoordenacaoPelaFilaEndToEndTests.
public sealed class CoordenadorPeloMcpTests
{
    [Fact]
    public async Task CadaPerfilDoCoordenadorAnunciaSoASuaFerramentaEAFormaEValidadaPeloPerfil()
    {
        Assert.Equal(["propor_plano"], Nomes(PerfilDeCoordenacao.Planejamento));
        Assert.Equal(["entregar_sintese"], Nomes(PerfilDeCoordenacao.Sintese));
        Assert.Equal(["propor_plano"], ServidorMcpDeCoordenacao.PreAutorizadas(PerfilDeCoordenacao.Planejamento));
        Assert.Equal(["entregar_sintese"], ServidorMcpDeCoordenacao.PreAutorizadas(PerfilDeCoordenacao.Sintese));
        Assert.Empty(ServidorMcpDeCoordenacao.DeLeitura(PerfilDeCoordenacao.Planejamento));
        Assert.Equal("planejamento", ServidorMcpDeCoordenacao.NomeDoPerfil(PerfilDeCoordenacao.Planejamento));
        Assert.Equal(PerfilDeCoordenacao.Sintese, ServidorMcpDeCoordenacao.Perfil("sintese"));
        // Só o objeto do plano ou da síntese: nenhum campo de execução, hash, dono, repositório ou permissão.
        foreach (var (perfil, campo) in new[] { (PerfilDeCoordenacao.Planejamento, "plano"), (PerfilDeCoordenacao.Sintese, "sintese") })
        {
            var schema = ServidorMcpDeCoordenacao.Lista(perfil)["tools"]![0]!["inputSchema"]!;
            Assert.Equal([campo], schema["properties"]!.AsObject().Select(p => p.Key));
            Assert.Equal([campo], schema["required"]!.AsArray().Select(v => (string?)v));
            Assert.False((bool)schema["additionalProperties"]!);
        }

        // O processo MCP valida pelo perfil: a ferramenta do outro perfil, um hash ou a execução nos argumentos e a falta
        // do obrigatório nunca chegam ao Worker.
        var encaminhadas = new List<string>();
        var entrada = string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"propor_plano","arguments":{"plano":{"unidades":[]}}}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"entregar_sintese","arguments":{"sintese":{}}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"consultar_agente","arguments":{"destinatario":"rev"}}}""",
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"propor_plano","arguments":{"plano":{},"hash":"abc"}}}""",
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"propor_plano","arguments":{"plano":{},"execucao":"E1"}}}""",
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"propor_plano","arguments":{}}}""") + "\n";
        var saida = new StringWriter();
        await ServidorMcpDeCoordenacao.ExecutarAsync(new StringReader(entrada), saida, (nome, _, _) =>
        {
            encaminhadas.Add(nome);
            return Task.FromResult(ServidorMcpDeCoordenacao.Resultado(new { registrado = true }));
        }, PerfilDeCoordenacao.Planejamento);
        var respostas = saida.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToArray();
        Assert.Equal(["propor_plano"], respostas[0]["result"]!["tools"]!.AsArray().Select(f => (string?)f!["name"]));
        Assert.Equal(["propor_plano"], encaminhadas);
        Assert.All(respostas[2..], r => Assert.True((bool)r["result"]!["isError"]!));
    }

    // O perfil do canal vem da etapa (planejamento ou síntese) e só em plan; sessão sem escopo ou fora de plan não recebe
    // o servidor. Nenhuma concessão no agente é exigida nem usada.
    [Fact]
    public void PerfilDoCoordenadorVemDaEtapaESoEmPlan()
    {
        var controle = Controle();
        var contexto = JobExecutionContext.General(Path.GetTempPath());
        PerfilDeCoordenacao? Perfil(EscopoDeCoordenacao? escopo, AgentPermissionProfile perfil = AgentPermissionProfile.Plan) =>
            FerramentasDeCoordenacao.Perfil(new SessionStartRequest(1, AgentKind.Codex, contexto, Profile: perfil,
                RegisteredAgent: Agente() with { Coordenacao = escopo }));

        Assert.Equal(PerfilDeCoordenacao.Planejamento,
            Perfil(new CoordenacaoDoCoordenador(new EntregaDoCoordenador(controle, PassoDaCoordenacao.Planejamento))));
        Assert.Equal(PerfilDeCoordenacao.Sintese,
            Perfil(new CoordenacaoDoCoordenador(new EntregaDoCoordenador(controle, PassoDaCoordenacao.Sintese))));
        Assert.Null(Perfil(new CoordenacaoDoCoordenador(new EntregaDoCoordenador(controle, PassoDaCoordenacao.Sintese)),
            AgentPermissionProfile.Manual));
        Assert.Null(Perfil(null));
        // Gate, unidade e avanço não têm entrega do coordenador.
        Assert.Throws<ArgumentOutOfRangeException>(() => new EntregaDoCoordenador(controle, PassoDaCoordenacao.Avanco));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EntregaDoCoordenador(controle, PassoDaCoordenacao.Unidade));
    }

    // Sem a sessão vinculada à etapa (o executor vincula antes do turno), nada é entregue nem gravado.
    [Fact]
    public async Task EntregaSemSessaoVinculadaERecusadaSemGravar()
    {
        var entrega = new EntregaDoCoordenador(Controle(), PassoDaCoordenacao.Planejamento);
        await Assert.ThrowsAsync<InvalidOperationException>(() => entrega.EntregarAsync("{}", CancellationToken.None));
        Assert.Null(entrega.Proposta);
        Assert.Null(entrega.Sintese);
    }

    private static string[] Nomes(PerfilDeCoordenacao perfil) =>
        ServidorMcpDeCoordenacao.Catalogo(perfil).Select(f => f.Nome).ToArray();

    private static readonly AlvoDoTrabalhoDto Alvo = new(Guid.NewGuid(), 7, "E7", TipoDeExecucao.Coordenada,
        EstadoDaExecucao.EmExecucao, "telegram", 1001, Guid.NewGuid(), 1, EstadoDaEtapa.EmExecucao, false,
        PoliticaDeRetomada.Automatica, 1);

    private static ExecucaoSobPosse Controle()
    {
        var trabalho = new TrabalhoReivindicado(Guid.NewGuid(), TipoDeTrabalho.AvancarExecucao, Alvo.IdExecucao, Alvo.IdEtapa,
            null, "coordenada:x", 1, 3, 4, DateTimeOffset.UtcNow.AddMinutes(5), "teste", Guid.NewGuid());
        return new ExecucaoSobPosse(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            trabalho, Alvo, CancellationToken.None, () => Task.CompletedTask);
    }

    // O coordenador sem nenhuma concessão de ferramenta do D.A.N.T.E.
    private static AgenteDaSessao Agente() =>
        new("coordenador", new VersaoDoAgenteDto
        {
            Id = Guid.NewGuid(), Numero = 1,
            Configuracao = new ConfiguracaoDoAgente(AgentKind.Codex, AgentPermissionProfile.Plan, AgentPermissionProfile.Manual,
                true, capacidades: [CapacidadeDoAgente.Planejar], repositorios: ["*"]),
            HashDaConfiguracao = new string('c', 64)
        })
        {
            TetoDaExecucao = AgentPermissionProfile.Plan
        };
}
