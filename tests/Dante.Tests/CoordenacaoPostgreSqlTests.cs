using Dante.Application.Agentes;
using Dante.Application.Comum;
using Dante.Application.Execucoes;
using Dante.Application.Grupos;
using Dante.Domain.Execucoes;
using Dante.Domain.Grupos;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Dante.Tests.CoordenacaoDominioTests;
using static Dante.Tests.FilaDuravelPostgreSqlTests;

namespace Dante.Tests;

// #310 (AD-75), PR 1: persistência da coordenação por papel contra PostgreSQL descartável (DANTE_TEST_POSTGRES). O
// fluxo gravado preserva a proveniência raiz → grupo → plano → etapa → delegação → agente → resultado; o banco recusa,
// mesmo por fora do domínio, plano alterado, hash que não é do conteúdo, aprovação sem a decisão do gate sobre o mesmo
// hash, unidade fora do plano aprovado ou do snapshot, dependência fora da ordem topológica e despacho fora do plano;
// replanejar preserva as revisões e as etapas; revisões e materializações simultâneas gravam uma só.
[Collection("FilaDuravel")]
public sealed class CoordenacaoPostgreSqlTests
{
    [PostgreSqlFact]
    public async Task FluxoGravadoPreservaAProvenienciaDaRaizAoResultado()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using var escopo = provider.CreateAsyncScope();
        var s = escopo.ServiceProvider;
        var c = await CenarioGravadoAsync(s);
        var (plano, etapas) = await PlanoMaterializadoAsync(s, c);

        // Despacho da primeira unidade: só pelo plano aprovado e pelo snapshot.
        etapas[0].TornarPronta([]);
        etapas[0].Iniciar(Agora);
        await Salvar(s);
        var nova = await DespacharAsync(s, c, plano, etapas[0], "pesq");
        // Cada transição da delegação é gravada (o banco confere uma por vez).
        nova.Delegacao.Aceitar(Agora);
        await Salvar(s);
        nova.Delegacao.Iniciar(Agora);
        await Salvar(s);
        nova.Delegacao.Responder("""{"resposta":"Há índice em estado.","confianca":"alta"}""", c.Pesquisador.Versao.Id, null,
            null, Agora);
        await Salvar(s);

        await using var conexao = new NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand($"""
            SELECT raiz.numero, g.nome, vg.numero, vg.modo, c.papel_do_coordenador, p.revisao, p.hash, et.numero, et.unidade,
                   et.papel, d.contrato, sub.numero, ag.handle, va.numero, d.hash_do_resultado, d.escrita, pa.estado, pa.finalidade
            FROM orquestracao.execucoes raiz
            JOIN orquestracao.coordenacoes_da_execucao c ON c.id_execucao = raiz.id
            JOIN orquestracao.grupos_de_agentes g ON g.id = c.id_grupo
            JOIN orquestracao.versoes_de_grupo vg ON vg.id = raiz.id_versao_do_grupo AND vg.id = c.id_versao_do_grupo
            JOIN orquestracao.planos_da_coordenacao p ON p.id_execucao = raiz.id
            JOIN orquestracao.pedidos_de_aprovacao pa ON pa.id_plano = p.id
            JOIN orquestracao.etapas_da_execucao et ON et.id_plano = p.id
            JOIN orquestracao.delegacoes d ON d.id_etapa_origem = et.id AND d.id_execucao_origem = raiz.id
            JOIN orquestracao.execucoes sub ON sub.id = d.id_execucao_subordinada AND sub.id_execucao_raiz = raiz.id
            JOIN orquestracao.versoes_de_agente va ON va.id = d.id_versao_destinataria AND va.id = sub.id_versao_do_agente
            JOIN orquestracao.agentes ag ON ag.id = va.id_agente
            WHERE raiz.id = '{c.Execucao.Id}'
            """, conexao);
        await using var leitor = await comando.ExecuteReaderAsync();
        Assert.True(await leitor.ReadAsync());
        Assert.Equal((c.Execucao.Numero, "pesquisa", 1, (int)ModoDeCoordenacao.Coordenador, "coord"),
            (leitor.GetInt64(0), leitor.GetString(1), leitor.GetInt32(2), leitor.GetInt32(3), leitor.GetString(4)));
        Assert.Equal((1, plano.Hash, etapas[0].Numero, "indice", "pesq", "consulta.v1"),
            (leitor.GetInt32(5), leitor.GetString(6), leitor.GetInt32(7), leitor.GetString(8), leitor.GetString(9),
             leitor.GetString(10)));
        Assert.Equal((nova.Subordinada.Numero, "pesq", c.Pesquisador.Versao.Numero, nova.Delegacao.HashDoResultado, false),
            (leitor.GetInt64(11), leitor.GetString(12), leitor.GetInt32(13), leitor.GetString(14), leitor.GetBoolean(15)));
        Assert.Equal(((int)EstadoDoPedidoDeAprovacao.Aprovado, (int)FinalidadeDaAprovacao.PlanoSomenteLeitura),
            (leitor.GetInt32(16), leitor.GetInt32(17)));
        Assert.False(await leitor.ReadAsync());

        // Relidos: plano íntegro e as unidades vêm dele; o grupo continua íntegro, com o coordenador.
        await using var outro = provider.CreateAsyncScope();
        var lido = (await outro.ServiceProvider.GetRequiredService<IPlanoDaCoordenacaoRepository>()
            .ObterUltimaRevisaoAsync(c.Execucao.Id))!;
        Assert.Equal((EstadoDoPlano.Aprovado, plano.Hash, 3), (lido.Estado, lido.Ler().Hash, lido.Ler().Unidades.Count));
        var etapasLidas = await outro.ServiceProvider.GetRequiredService<IEtapaDaExecucaoRepository>().ListarDoPlanoAsync(plano.Id);
        Assert.Equal(["indice", "consulta", "sintese"], etapasLidas.Select(e => e.Unidade));
        var versao = (await outro.ServiceProvider.GetRequiredService<IVersaoDoGrupoRepository>()
            .ObterComParticipantesAsync(c.VersaoDoGrupo.Id))!;
        Assert.True(versao.Integra);
        Assert.Equal((ModoDeCoordenacao.Coordenador, "coord"), (versao.Modo, versao.PapelDoCoordenador));
        var coordenacao = (await outro.ServiceProvider.GetRequiredService<ICoordenacaoDaExecucaoRepository>()
            .ObterDaExecucaoAsync(c.Execucao.Id))!;
        var participantes = await outro.ServiceProvider.GetRequiredService<IParticipanteDaExecucaoRepository>()
            .ListarDaExecucaoAsync(c.Execucao.Id);
        Assert.True(coordenacao.SnapshotConfere(participantes));
    }

    [PostgreSqlFact]
    public async Task BancoProtegePlanoEAprovacaoVinculadaAoHash()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using var escopo = provider.CreateAsyncScope();
        var s = escopo.ServiceProvider;
        var c = await CenarioGravadoAsync(s);
        var plano = PlanoDaCoordenacao.Inicial(c.Execucao, c.Coordenacao, c.Validado(Proposta), Agora);
        await s.GetRequiredService<IPlanoDaCoordenacaoRepository>().AdicionarAsync(plano);
        await Salvar(s);
        var id = plano.Id;

        // Aprovar sem a decisão do gate: recusado no commit.
        await Recusada(banco, "decisão do plano sem a decisão do gate",
            $"UPDATE orquestracao.planos_da_coordenacao SET estado = 1, decidido_em = now() WHERE id = '{id}'");
        // Pedido de aprovação do plano com outro hash, de outra revisão ou sem o plano: recusado.
        var pedido = PedidoCru(c, id, $"'{new string('e', 64)}'", "1");
        await Recusada(banco, "aprovação de plano só do plano que aguarda decisão", pedido);
        await Recusada(banco, "aprovação de plano só do plano que aguarda decisão", PedidoCru(c, id, "p.hash", "2"));
        await Recusada(banco, "aprovação de plano só do plano que aguarda decisão", PedidoCru(c, null, "p.hash", "1"));
        await Recusada(banco, "aprovação de artefato só num Roteiro", PedidoCru(c, id, "p.hash", "1", finalidade: 0));

        // Pelo domínio: pedido gravado e aprovado com o hash do conteúdo; o plano aprovado no mesmo commit.
        var doPlano = PedidoDeAprovacao.DoPlano(c.Execucao, c.EtapaDeAprovacao, plano, c.Coordenacao, Agora);
        await s.GetRequiredService<IPedidoDeAprovacaoRepository>().AdicionarAsync(doPlano);
        await Salvar(s);
        // A finalidade e o plano de um pedido não mudam.
        await Recusada(banco, "o artefato de um pedido de aprovação é imutável",
            $"UPDATE orquestracao.pedidos_de_aprovacao SET finalidade = 0, id_plano = NULL WHERE id = '{doPlano.Id}'");
        doPlano.Aprovar("telegram", 1001, plano.Hash, Agora);
        plano.Aprovar(doPlano);
        await Salvar(s);
        Assert.Equal(1L, await Escalar(banco, $"SELECT estado FROM orquestracao.planos_da_coordenacao WHERE id = '{id}'"));

        // Conteúdo, hash e identidade imutáveis; transição fora do domínio; nunca removido.
        await Recusada(banco, "o conteúdo e a identidade do plano são imutáveis",
            $"UPDATE orquestracao.planos_da_coordenacao SET conteudo = replace(conteudo, 'pesq', 'rev') WHERE id = '{id}'");
        await Recusada(banco, "o conteúdo e a identidade do plano são imutáveis",
            $"UPDATE orquestracao.planos_da_coordenacao SET hash = repeat('a', 64) WHERE id = '{id}'");
        await Recusada(banco, "transição de plano inválida",
            $"UPDATE orquestracao.planos_da_coordenacao SET estado = 0, decidido_em = NULL WHERE id = '{id}'");
        await Recusada(banco, "planos não são removidos", $"DELETE FROM orquestracao.planos_da_coordenacao WHERE id = '{id}'");

        // Inserções diretas: hash que não é do conteúdo, plano já decidido, numa execução que não é Coordenada, revisão fora
        // da cadeia ou acima do limite.
        await Recusada(banco, "o hash do plano não é o do conteúdo", PlanoCru(c.Execucao.Id, "repeat('b', 64)"));
        await Recusada(banco, "plano nasce aguardando aprovação", PlanoCru(c.Execucao.Id, Sha("conteudo"), estado: 1));
        var avulsa = await RaizAvulsaAsync(s);
        await Recusada(banco, "plano só numa Coordenada", PlanoCru(avulsa, Sha("conteudo")));
        await Recusada(banco, "revisão fora da cadeia do plano",
            PlanoCru(c.Execucao.Id, Sha("conteudo"), revisao: 3, anterior: id, motivo: 1));
        await Recusada(banco, "ck_planos_da_coordenacao_revisao", PlanoCru(c.Execucao.Id, Sha("conteudo"), revisao: 4));
        await Recusada(banco, "ck_planos_da_coordenacao_revisao", PlanoCru(c.Execucao.Id, Sha("conteudo"), revisao: 2));
        // Revisão substituta com a anterior ainda aprovada (não substituída): recusada no commit.
        await Recusada(banco, "revisão substituta sem a anterior substituída",
            PlanoCru(c.Execucao.Id, Sha("conteudo"), revisao: 2, anterior: id, motivo: 1));
    }

    [PostgreSqlFact]
    public async Task BancoRecusaUnidadeEDespachoForaDoPlanoAprovadoOuDoSnapshot()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using var escopo = provider.CreateAsyncScope();
        var s = escopo.ServiceProvider;
        var c = await CenarioGravadoAsync(s);

        // Plano ainda não aprovado: nenhuma unidade é materializada.
        var pendente = PlanoDaCoordenacao.Inicial(c.Execucao, c.Coordenacao, c.Validado(Proposta), Agora);
        await s.GetRequiredService<IPlanoDaCoordenacaoRepository>().AdicionarAsync(pendente);
        await Salvar(s);
        await Recusada(banco, "unidade fora de plano aprovado ou do snapshot",
            UnidadeCrua(c, pendente.Id, "u9", "pesq", c.Pesquisador.Versao.Id, 90));
        Assert.Equal(0L, await Escalar(banco, "SELECT count(*) FROM orquestracao.delegacoes"));

        // Plano aprovado: unidade do coordenador, de papel fora do snapshot ou com escrita continua recusada.
        var aprovado = await AprovarAsync(s, c, pendente);
        await Recusada(banco, "unidade fora de plano aprovado ou do snapshot",
            UnidadeCrua(c, aprovado.Id, "u9", "coord", c.Coordenador.Versao.Id, 90));
        await Recusada(banco, "unidade fora de plano aprovado ou do snapshot",
            UnidadeCrua(c, aprovado.Id, "u9", "pesq", c.Revisor.Versao.Id, 90));
        await Recusada(banco, "ck_etapas_da_execucao_unidade_do_plano",
            UnidadeCrua(c, aprovado.Id, "u9", "pesq", c.Pesquisador.Versao.Id, 90, escrita: true));

        var (etapas, dependencias) = aprovado.Materializar(c.Execucao, c.Coordenacao, c.Participantes, 2, Agora);
        foreach (var etapa in etapas) await s.GetRequiredService<IEtapaDaExecucaoRepository>().AdicionarAsync(etapa);
        foreach (var dependencia in dependencias)
            await s.GetRequiredService<IDependenciaDeEtapaRepository>().AdicionarAsync(dependencia);
        await Salvar(s);
        // Materializar de novo a mesma unidade; mudar unidade, papel ou versão de uma etapa; dependência ao contrário.
        await Recusada(banco, "IX_etapas_da_execucao_id_plano_unidade",
            UnidadeCrua(c, aprovado.Id, "indice", "pesq", c.Pesquisador.Versao.Id, 90));
        await Recusada(banco, "a unidade de plano de uma etapa é imutável",
            $"UPDATE orquestracao.etapas_da_execucao SET papel = 'rev', id_versao_do_agente = '{c.Revisor.Versao.Id}' WHERE id = '{etapas[0].Id}'");
        await Recusada(banco, "a unidade de plano de uma etapa é imutável",
            $"UPDATE orquestracao.etapas_da_execucao SET id_plano = NULL, unidade = NULL, tipo = 1 WHERE id = '{etapas[0].Id}'");
        await Recusada(banco, "a etapa requerida precisa vir antes da dependente",
            $"INSERT INTO orquestracao.dependencias_de_etapa (id, id_execucao, id_etapa, id_etapa_requerida) " +
            $"VALUES (gen_random_uuid(), '{c.Execucao.Id}', '{etapas[0].Id}', '{etapas[2].Id}')");

        // Despacho válido pelo domínio; cópias cruas quebrando uma regra de cada vez.
        etapas[0].TornarPronta([]);
        etapas[0].Iniciar(Agora);
        await Salvar(s);
        var valida = await DespacharAsync(s, c, aprovado, etapas[0], "pesq");
        var id = valida.Delegacao.Id;
        // Destinatário que é participante, mas não o do papel da unidade.
        var livreRev = await DelegadaCruaAsync(banco, c, c.Revisor.Versao.Id);
        await Recusada(banco, "delegação de coordenação fora do snapshot",
            Copia(id, livreRev, c.Revisor, unidade: null, etapa: null));
        // Contrato diferente de consulta.v1.
        var livrePesq = await DelegadaCruaAsync(banco, c, c.Pesquisador.Versao.Id);
        await Recusada(banco, "só de unidade de plano, por consulta.v1",
            Copia(id, livrePesq, c.Pesquisador, unidade: null, etapa: null, contrato: "revisao.v1"));
        // Etapa que não é unidade de plano (a de aprovação).
        await Recusada(banco, "só de unidade de plano, por consulta.v1",
            Copia(id, livrePesq, c.Pesquisador, unidade: null, etapa: c.EtapaDeAprovacao.Id));
        // Participante do snapshot sem a capacidade do contrato: a unidade dele passa no banco (é participante), o despacho não.
        await Executar(banco, UnidadeCrua(c, aprovado.Id, "extra", "analista", c.Analista.Versao.Id, 9));
        var livreAnalista = await DelegadaCruaAsync(banco, c, c.Analista.Versao.Id);
        await Recusada(banco, "destinatário sem capacidade para consulta.v1",
            Copia(id, livreAnalista, c.Analista, unidade: "extra", etapa: null));
        Assert.Equal(1L, await Escalar(banco, "SELECT count(*) FROM orquestracao.delegacoes"));
    }

    [PostgreSqlFact]
    public async Task ReplanejamentoGravadoPreservaRevisoesEtapasEProveniencia()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using var escopo = provider.CreateAsyncScope();
        var s = escopo.ServiceProvider;
        var c = await CenarioGravadoAsync(s);
        var (plano, etapas) = await PlanoMaterializadoAsync(s, c);
        etapas[0].TornarPronta([]);
        etapas[0].Iniciar(Agora);
        await Salvar(s);
        var falha = await DespacharAsync(s, c, plano, etapas[0], "pesq");
        falha.Delegacao.Aceitar(Agora);
        await Salvar(s);
        falha.Delegacao.Expirar("Prazo para iniciar excedido.", Agora);
        etapas[0].Falhar("Delegação expirada.", null, Agora);
        await Salvar(s);

        var segunda = PlanoDaCoordenacao.Revisar(c.Execucao, plano, MotivoDoPlano.UnidadeSemResposta,
            c.Validado("""{"unidades":[{"id":"indice2","papel":"rev","contrato":"consulta.v1","objetivo":"Levantar os índices","entrada":{"pergunta":"Quais índices?"}}]}""",
                delegacoes: 1), etapas, null, Agora);
        await s.GetRequiredService<IPlanoDaCoordenacaoRepository>().AdicionarAsync(segunda);
        await Salvar(s);

        Assert.Equal(2L, await Escalar(banco, $"SELECT count(*) FROM orquestracao.planos_da_coordenacao WHERE id_execucao = '{c.Execucao.Id}'"));
        Assert.Equal(1L, await Escalar(banco,
            $"SELECT count(*) FROM orquestracao.planos_da_coordenacao WHERE id = '{segunda.Id}' AND revisao = 2 " +
            $"AND id_plano_anterior = '{plano.Id}' AND motivo = {(int)MotivoDoPlano.UnidadeSemResposta} AND estado = 0"));
        Assert.Equal((long)EstadoDoPlano.Substituido,
            await Escalar(banco, $"SELECT estado FROM orquestracao.planos_da_coordenacao WHERE id = '{plano.Id}'"));
        // A etapa que falhou e a delegação expirada continuam; as pendentes ficaram ignoradas, com motivo; nada apagado.
        Assert.Equal(3L, await Escalar(banco, $"SELECT count(*) FROM orquestracao.etapas_da_execucao WHERE id_plano = '{plano.Id}'"));
        Assert.Equal(2L, await Escalar(banco,
            $"SELECT count(*) FROM orquestracao.etapas_da_execucao WHERE id_plano = '{plano.Id}' AND estado = {(int)EstadoDaEtapa.Ignorada} " +
            "AND motivo = 'Unidade substituída pela revisão 2 do plano.'"));
        Assert.Equal((long)EstadoDaDelegacao.Expirada,
            await Escalar(banco, $"SELECT estado FROM orquestracao.delegacoes WHERE id = '{falha.Delegacao.Id}'"));
        // O plano substituído não volta, e não despacha: uma unidade dele não é delegada de novo.
        await Recusada(banco, "plano encerrado é imutável",
            $"UPDATE orquestracao.planos_da_coordenacao SET estado = 1 WHERE id = '{plano.Id}'");
        var livre = await DelegadaCruaAsync(banco, c, c.Pesquisador.Versao.Id);
        await Recusada(banco, "unidade de plano não aprovado",
            Copia(falha.Delegacao.Id, livre, c.Pesquisador, unidade: "consulta", etapa: null));
    }

    [PostgreSqlFact]
    public async Task RevisoesEMaterializacoesSimultaneasGravamUmaSo()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        Cenario c;
        PlanoDaCoordenacao aprovado;
        await using (var escopo = provider.CreateAsyncScope())
        {
            c = await CenarioGravadoAsync(escopo.ServiceProvider);
            var pendente = PlanoDaCoordenacao.Inicial(c.Execucao, c.Coordenacao, c.Validado(Proposta), Agora);
            await escopo.ServiceProvider.GetRequiredService<IPlanoDaCoordenacaoRepository>().AdicionarAsync(pendente);
            await Salvar(escopo.ServiceProvider);
            aprovado = await AprovarAsync(escopo.ServiceProvider, c, pendente);
        }

        // Dois escopos leem o mesmo plano aprovado e materializam: o índice (plano, unidade) deixa gravar um só.
        var resultados = new List<Exception?>();
        await using var a = provider.CreateAsyncScope();
        await using var b = provider.CreateAsyncScope();
        var lidos = new List<(AsyncServiceScope Escopo, MaterializacaoDoPlano Materializacao)>();
        foreach (var escopo in new[] { a, b })
        {
            var s = escopo.ServiceProvider;
            var plano = (await s.GetRequiredService<IPlanoDaCoordenacaoRepository>().ObterUltimaRevisaoAsync(c.Execucao.Id))!;
            var execucao = (await s.GetRequiredService<IExecucaoRepository>().ObterPorIdAsync(c.Execucao.Id))!;
            var coordenacao = (await s.GetRequiredService<ICoordenacaoDaExecucaoRepository>().ObterDaExecucaoAsync(c.Execucao.Id))!;
            var participantes = await s.GetRequiredService<IParticipanteDaExecucaoRepository>().ListarDaExecucaoAsync(c.Execucao.Id);
            lidos.Add((escopo, plano.Materializar(execucao, coordenacao, participantes, 2, Agora)));
        }
        foreach (var (escopo, materializacao) in lidos)
        {
            var s = escopo.ServiceProvider;
            foreach (var etapa in materializacao.Etapas) await s.GetRequiredService<IEtapaDaExecucaoRepository>().AdicionarAsync(etapa);
            foreach (var dependencia in materializacao.Dependencias)
                await s.GetRequiredService<IDependenciaDeEtapaRepository>().AdicionarAsync(dependencia);
            resultados.Add(await Record.ExceptionAsync(() => Salvar(s)));
        }
        Assert.Null(resultados[0]);
        Assert.IsType<ConflitoDeConcorrenciaException>(resultados[1]);
        Assert.Equal(3L, await Escalar(banco, $"SELECT count(*) FROM orquestracao.etapas_da_execucao WHERE id_plano = '{aprovado.Id}'"));

        // Duas revisões substitutas do mesmo plano, a pedido de uma unidade que falhou: só uma grava.
        await using (var escopo = provider.CreateAsyncScope())
        {
            var s = escopo.ServiceProvider;
            var etapa = (await s.GetRequiredService<IEtapaDaExecucaoRepository>().ListarDoPlanoAsync(aprovado.Id))[0];
            etapa.TornarPronta([]);
            etapa.Iniciar(Agora);
            etapa.Falhar("sem resposta", null, Agora);
            await Salvar(s);
        }
        await using var x = provider.CreateAsyncScope();
        await using var y = provider.CreateAsyncScope();
        var revisoes = new List<(AsyncServiceScope Escopo, PlanoDaCoordenacao Plano)>();
        foreach (var escopo in new[] { x, y })
        {
            var s = escopo.ServiceProvider;
            var plano = (await s.GetRequiredService<IPlanoDaCoordenacaoRepository>().ObterUltimaRevisaoAsync(c.Execucao.Id))!;
            var execucao = (await s.GetRequiredService<IExecucaoRepository>().ObterPorIdAsync(c.Execucao.Id))!;
            var etapas = await s.GetRequiredService<IEtapaDaExecucaoRepository>().ListarDoPlanoAsync(plano.Id);
            revisoes.Add((escopo, PlanoDaCoordenacao.Revisar(execucao, plano, MotivoDoPlano.UnidadeSemResposta,
                c.Validado("""{"unidades":[{"id":"u1","papel":"rev","contrato":"consulta.v1","objetivo":"Outra","entrada":{"pergunta":"Q?"}}]}"""),
                etapas, null, Agora)));
        }
        var erros = new List<Exception?>();
        foreach (var (escopo, plano) in revisoes)
        {
            await escopo.ServiceProvider.GetRequiredService<IPlanoDaCoordenacaoRepository>().AdicionarAsync(plano);
            erros.Add(await Record.ExceptionAsync(() => Salvar(escopo.ServiceProvider)));
        }
        Assert.Null(erros[0]);
        Assert.IsType<ConflitoDeConcorrenciaException>(erros[1]);
        Assert.Equal(1L, await Escalar(banco, $"SELECT count(*) FROM orquestracao.planos_da_coordenacao WHERE revisao = 2"));
    }

    [PostgreSqlFact]
    public async Task GrupoCoordenadoGravadoNaoAceitaParticipanteComEscritaNemSnapshotAlterado()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using var escopo = provider.CreateAsyncScope();
        var c = await CenarioGravadoAsync(escopo.ServiceProvider);

        await Recusada(banco, "grupo coordenado não tem participante com escrita",
            "INSERT INTO orquestracao.participantes_de_grupo (id, id_versao_do_grupo, id_agente, papel, responsabilidades, pode_escrever) " +
            $"VALUES (gen_random_uuid(), '{c.VersaoDoGrupo.Id}', '{c.Pesquisador.Agente.Id}', 'dev', NULL, true)");
        await Recusada(banco, "ck_versoes_de_grupo_coordenador",
            "INSERT INTO orquestracao.versoes_de_grupo (id, id_grupo, numero, modo, roteiro, rodadas_de_revisao_maximas, paralelismo_maximo, " +
            "repositorios_permitidos, hash_da_configuracao, id_do_autor, criada_em) " +
            $"VALUES (gen_random_uuid(), '{c.Grupo.Id}', 9, 1, '{{}}', 0, 1, '{{}}', repeat('a', 64), 1, now())");
        await Recusada(banco, "o snapshot da coordenação é imutável",
            $"UPDATE orquestracao.coordenacoes_da_execucao SET papel_do_coordenador = 'pesq' WHERE id_execucao = '{c.Execucao.Id}'");
        await Recusada(banco, "coordenações não são removidas",
            $"DELETE FROM orquestracao.coordenacoes_da_execucao WHERE id_execucao = '{c.Execucao.Id}'");
        await Recusada(banco, "participantes da execução são imutáveis",
            $"UPDATE orquestracao.participantes_da_execucao SET id_versao_do_agente = '{c.Revisor.Versao.Id}' WHERE papel = 'pesq'");
        // Coordenada sem a versão do coordenador; Roteiro com coordenador: o check da execução recusa.
        await Recusada(banco, "ck_execucoes_roteiro_com_grupo",
            $"UPDATE orquestracao.execucoes SET id_versao_do_agente = NULL WHERE id = '{c.Execucao.Id}'");
    }

    [PostgreSqlFact]
    public async Task MigrationSobreDadosAnterioresPreservaOGateDoRoteiroEOHashDoGrupo()
    {
        await using var banco = await Banco.CriarAsync(migrar: false);
        await using (var contexto = new Dante.Infrastructure.Data.Orquestracao.OrquestracaoDbContext(
                         Dante.Infrastructure.Data.Orquestracao.OrquestracaoDbContextFactory.Opcoes(banco.ConnectionString)))
            await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<
                Microsoft.EntityFrameworkCore.Migrations.IMigrator>(contexto).MigrateAsync("20261118193108_Delegacoes");
        // Os ids dos agentes entram no hash do grupo calculado pelo código anterior (CompatibilidadeDosHashesTests).
        var dev = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var rev = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var versaoDev = Guid.NewGuid();
        var versaoRev = Guid.NewGuid();
        var versaoDoGrupo = Guid.NewGuid();
        var roteiro = Guid.NewGuid();
        var etapa = Guid.NewGuid();
        var pedido = Guid.NewGuid();
        // Schema anterior: um Roteiro com o gate pendente; hashes calculados pelo código anterior à #310.
        await Executar(banco, $$"""
            INSERT INTO orquestracao.agentes (id, handle, estado, id_versao_atual, numero_da_versao_atual, id_do_criador, criado_em, atualizado_em)
            VALUES ('{{dev}}', 'dev', 0, '{{versaoDev}}', 1, 1, now(), now()), ('{{rev}}', 'rev', 0, '{{versaoRev}}', 1, 1, now(), now());
            INSERT INTO orquestracao.versoes_de_agente (id, id_agente, numero, driver, capacidades, perfil_padrao, perfil_maximo,
                pode_escrever, ferramentas, repositorios, hash_da_configuracao, id_do_autor, criada_em)
            VALUES ('{{versaoDev}}', '{{dev}}', 1, 1, '{Implementar}', 2, 2, true, '{}', '{}', repeat('a', 64), 1, now()),
                   ('{{versaoRev}}', '{{rev}}', 1, 1, '{Revisar}', 0, 0, false, '{}', '{}', repeat('b', 64), 1, now());
            INSERT INTO orquestracao.grupos_de_agentes (id, nome, estado, id_versao_atual, numero_da_versao_atual, id_do_criador, criado_em, atualizado_em)
            VALUES ('{{Guid.NewGuid()}}', 'engenharia', 0, '{{versaoDoGrupo}}', 1, 1, now(), now());
            INSERT INTO orquestracao.versoes_de_grupo (id, id_grupo, numero, modo, roteiro, rodadas_de_revisao_maximas, paralelismo_maximo,
                repositorios_permitidos, duracao_maxima_em_minutos, hash_da_configuracao, id_do_autor, criada_em)
            SELECT '{{versaoDoGrupo}}', id, 1, 0, '{Implementar:dev,Revisar:rev,Corrigir:dev,AprovacaoHumana}', 2, 1, '{@dante}', 90,
                   '{{CompatibilidadeDosHashesTests.HashDoGrupo}}', 1, now() FROM orquestracao.grupos_de_agentes WHERE nome = 'engenharia';
            INSERT INTO orquestracao.participantes_de_grupo (id, id_versao_do_grupo, id_agente, papel, responsabilidades, pode_escrever)
            VALUES ('{{Guid.NewGuid()}}', '{{versaoDoGrupo}}', '{{dev}}', 'dev', 'implementa', true),
                   ('{{Guid.NewGuid()}}', '{{versaoDoGrupo}}', '{{rev}}', 'rev', NULL, false);
            INSERT INTO orquestracao.execucoes (id, numero, tipo, estado, canal, id_do_usuario_no_canal, contexto, criada_em, id_versao_do_grupo)
            VALUES ('{{roteiro}}', 7, {{(int)TipoDeExecucao.Roteiro}}, {{(int)EstadoDaExecucao.AguardandoAprovacao}}, 'telegram', 1001, '{}', now(), '{{versaoDoGrupo}}');
            INSERT INTO orquestracao.etapas_da_execucao (id, id_execucao, numero, tipo, estado, escrita, politica_de_retomada,
                tentativa, chave_de_idempotencia, criada_em, passo, rodada)
            VALUES ('{{etapa}}', '{{roteiro}}', 3, {{(int)TipoDeEtapa.AprovacaoHumana}}, {{(int)EstadoDaEtapa.AguardandoAprovacao}}, false, 0, 1,
                    '{{roteiro:N}}:3:1', now(), {{(int)TipoDePassoDoRoteiro.AprovacaoHumana}}, 0);
            INSERT INTO orquestracao.pedidos_de_aprovacao (id, id_execucao, id_etapa, rodada, hash_do_snapshot, hash_do_artefato, estado, criado_em)
            VALUES ('{{pedido}}', '{{roteiro}}', '{{etapa}}', 0, repeat('c', 64), repeat('d', 64), 0, now());
            """);

        await using (var contexto = new Dante.Infrastructure.Data.Orquestracao.OrquestracaoDbContext(
                         Dante.Infrastructure.Data.Orquestracao.OrquestracaoDbContextFactory.Opcoes(banco.ConnectionString)))
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(contexto.Database);

        // O pedido existente é do Roteiro, sem plano; o gate continua decidível como antes, e só uma vez.
        Assert.Equal(1L, await Escalar(banco,
            $"SELECT count(*) FROM orquestracao.pedidos_de_aprovacao WHERE id = '{pedido}' AND finalidade = 0 AND id_plano IS NULL"));
        await Executar(banco, $"UPDATE orquestracao.pedidos_de_aprovacao SET estado = {(int)EstadoDoPedidoDeAprovacao.Aprovado}, " +
            $"canal_do_decisor = 'telegram', id_do_decisor = 1001, decidido_em = now() WHERE id = '{pedido}'");
        await Recusada(banco, "pedido de aprovação já decidido",
            $"UPDATE orquestracao.pedidos_de_aprovacao SET estado = 0, decidido_em = NULL, id_do_decisor = NULL WHERE id = '{pedido}'");
        // A versão do grupo gravada antes continua íntegra (formato 1, sem coordenador).
        await using var provider = Provider(banco.ConnectionString);
        await using var escopo = provider.CreateAsyncScope();
        var versao = (await escopo.ServiceProvider.GetRequiredService<IVersaoDoGrupoRepository>().ObterComParticipantesAsync(versaoDoGrupo))!;
        Assert.True(versao.Integra);
        Assert.Equal((ModoDeCoordenacao.Roteiro, (string?)null), (versao.Modo, versao.PapelDoCoordenador));
    }

    // ---- cenário gravado ----

    private static async Task<Cenario> CenarioGravadoAsync(IServiceProvider s)
    {
        var c = new Cenario(await s.GetRequiredService<IExecucaoRepository>().ReservarNumeroAsync());
        foreach (var agente in c.Agentes) await s.GetRequiredService<IAgenteRepository>().AdicionarAsync(agente);
        foreach (var versao in c.Versoes) await s.GetRequiredService<IVersaoDoAgenteRepository>().AdicionarAsync(versao);
        await s.GetRequiredService<IGrupoDeAgentesRepository>().AdicionarAsync(c.Grupo);
        await s.GetRequiredService<IVersaoDoGrupoRepository>().AdicionarAsync(c.VersaoDoGrupo);
        await s.GetRequiredService<IAtivacaoDeVersaoDoGrupoRepository>().AdicionarAsync(c.Ativacao);
        await Salvar(s);
        await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(c.Execucao);
        foreach (var participante in c.Participantes)
            await s.GetRequiredService<IParticipanteDaExecucaoRepository>().AdicionarAsync(participante);
        await s.GetRequiredService<ICoordenacaoDaExecucaoRepository>().AdicionarAsync(c.Coordenacao);
        await s.GetRequiredService<IEtapaDaExecucaoRepository>().AdicionarAsync(c.EtapaDeAprovacao);
        await Salvar(s);
        return c;
    }

    private static async Task<PlanoDaCoordenacao> AprovarAsync(IServiceProvider s, Cenario c, PlanoDaCoordenacao plano)
    {
        var pedido = PedidoDeAprovacao.DoPlano(c.Execucao, c.EtapaDeAprovacao, plano, c.Coordenacao, Agora);
        await s.GetRequiredService<IPedidoDeAprovacaoRepository>().AdicionarAsync(pedido);
        await Salvar(s);
        pedido.Aprovar("telegram", 1001, plano.Hash[..12], Agora);
        plano.Aprovar(pedido);
        await Salvar(s);
        return plano;
    }

    private static async Task<(PlanoDaCoordenacao, IReadOnlyList<EtapaDaExecucao>)> PlanoMaterializadoAsync(IServiceProvider s,
        Cenario c)
    {
        var plano = PlanoDaCoordenacao.Inicial(c.Execucao, c.Coordenacao, c.Validado(Proposta), Agora);
        await s.GetRequiredService<IPlanoDaCoordenacaoRepository>().AdicionarAsync(plano);
        await Salvar(s);
        await AprovarAsync(s, c, plano);
        var (etapas, dependencias) = plano.Materializar(c.Execucao, c.Coordenacao, c.Participantes, 2, Agora);
        foreach (var etapa in etapas) await s.GetRequiredService<IEtapaDaExecucaoRepository>().AdicionarAsync(etapa);
        foreach (var dependencia in dependencias)
            await s.GetRequiredService<IDependenciaDeEtapaRepository>().AdicionarAsync(dependencia);
        await Salvar(s);
        return (plano, etapas);
    }

    private static async Task<NovaDelegacao> DespacharAsync(IServiceProvider s, Cenario c, PlanoDaCoordenacao plano,
        EtapaDaExecucao etapa, string papel)
    {
        var delegacoes = await s.GetRequiredService<IDelegacaoRepository>().ContarDaRaizAsync(c.Execucao.Id);
        var nova = Delegacao.CriarDoPlano(c.Despacho(plano, etapa, papel, delegacoes),
            await s.GetRequiredService<IExecucaoRepository>().ReservarNumeroAsync(), Agora);
        await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(nova.Subordinada);
        await s.GetRequiredService<IDelegacaoRepository>().AdicionarAsync(nova.Delegacao);
        await Salvar(s);
        return nova;
    }

    private static async Task<Guid> RaizAvulsaAsync(IServiceProvider s)
    {
        var raiz = new Execucao(await s.GetRequiredService<IExecucaoRepository>().ReservarNumeroAsync(), TipoDeExecucao.Avulsa,
            "telegram", 1001, "{}", null, Agora);
        await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(raiz);
        await Salvar(s);
        return raiz.Id;
    }

    private static Task Salvar(IServiceProvider s) => s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();

    // ---- SQL cru ----

    private static string Sha(string texto) => $"encode(sha256(convert_to('{texto}', 'UTF8')), 'hex')";

    private static string PlanoCru(Guid execucao, string hash, int estado = 0, int revisao = 1, Guid? anterior = null,
        int motivo = 0) =>
        "INSERT INTO orquestracao.planos_da_coordenacao (id, id_execucao, revisao, id_plano_anterior, motivo, conteudo, hash, " +
        "quantidade_de_unidades, estado, criado_em, decidido_em) " +
        $"VALUES (gen_random_uuid(), '{execucao}', {revisao}, {(anterior is { } a ? $"'{a}'" : "NULL")}, {motivo}, 'conteudo', " +
        $"{hash}, 1, {estado}, now(), {(estado == 0 ? "NULL" : "now()")})";

    private static string PedidoCru(Cenario c, Guid? plano, string hash, string rodada, int finalidade = 1) =>
        "INSERT INTO orquestracao.pedidos_de_aprovacao (id, id_execucao, id_etapa, rodada, hash_do_snapshot, hash_do_artefato, " +
        "estado, criado_em, finalidade, id_plano) " +
        $"SELECT gen_random_uuid(), '{c.Execucao.Id}', '{c.EtapaDeAprovacao.Id}', {rodada}, '{c.Coordenacao.HashDoSnapshot}', " +
        $"{hash}, 0, now(), {finalidade}, {(plano is { } p ? $"'{p}'" : "NULL")} " +
        $"FROM orquestracao.planos_da_coordenacao p WHERE p.id_execucao = '{c.Execucao.Id}' LIMIT 1";

    private static string UnidadeCrua(Cenario c, Guid plano, string unidade, string papel, Guid versao, int numero,
        bool escrita = false) =>
        "INSERT INTO orquestracao.etapas_da_execucao (id, id_execucao, numero, tipo, estado, escrita, politica_de_retomada, " +
        "tentativa, chave_de_idempotencia, criada_em, papel, id_versao_do_agente, id_plano, unidade) " +
        $"VALUES (gen_random_uuid(), '{c.Execucao.Id}', {numero}, {(int)TipoDeEtapa.UnidadeDoPlano}, " +
        $"{(int)EstadoDaEtapa.EmExecucao}, {(escrita ? "true" : "false")}, 0, 1, '{c.Execucao.Id:N}:{numero}:1', now(), " +
        $"'{papel}', '{versao}', '{plano}', '{unidade}')";

    // Execução Delegada filha da Coordenada, sem delegação, com a versão indicada como responsável.
    private static async Task<Guid> DelegadaCruaAsync(Banco banco, Cenario c, Guid versao)
    {
        var id = Guid.NewGuid();
        await Executar(banco,
            "INSERT INTO orquestracao.execucoes (id, numero, tipo, estado, canal, id_do_usuario_no_canal, contexto, criada_em, " +
            "id_versao_do_agente, id_execucao_pai, id_execucao_raiz, profundidade) " +
            $"VALUES ('{id}', {Random.Shared.Next(10_000, 1_000_000)}, {(int)TipoDeExecucao.Delegada}, 0, 'telegram', 1001, '{{}}', " +
            $"now(), '{versao}', '{c.Execucao.Id}', '{c.Execucao.Id}', 1)");
        return id;
    }

    private static readonly string[] ColunasDaDelegacao =
    [
        "id_execucao_origem", "id_etapa_origem", "id_execucao_subordinada", "id_execucao_raiz", "id_agente_solicitante",
        "id_versao_solicitante", "hash_da_versao_solicitante", "id_agente_destinatario", "handle_do_destinatario",
        "id_versao_destinataria", "hash_da_versao_destinataria", "contrato", "entrada", "hash_da_entrada", "objetivo_redigido",
        "objetivo_alterado_pela_redacao", "hash_do_objetivo", "criterios", "minutos_para_iniciar", "minutos_de_duracao", "escrita",
        "cadeia_de_agentes", "chave_de_deduplicacao", "estado", "criada_em"
    ];

    // Cópia crua da delegação válida para outra subordinada e outro destinatário (e, opcionalmente, outra etapa de origem,
    // pela unidade ou pelo id, e outro contrato). A chave muda para não esbarrar no índice de deduplicação.
    private static string Copia(Guid id, Guid subordinada, (Domain.Agentes.Agente Agente, Domain.Agentes.VersaoDoAgente Versao) destino,
        string? unidade, Guid? etapa, string contrato = "consulta.v1")
    {
        var trocas = new Dictionary<string, string>
        {
            ["id_execucao_subordinada"] = $"'{subordinada}'", ["id_agente_destinatario"] = $"'{destino.Agente.Id}'",
            ["handle_do_destinatario"] = $"'{destino.Agente.Handle}'", ["id_versao_destinataria"] = $"'{destino.Versao.Id}'",
            ["hash_da_versao_destinataria"] = $"'{destino.Versao.HashDaConfiguracao}'", ["contrato"] = $"'{contrato}'",
            ["chave_de_deduplicacao"] = "md5(random()::text) || md5(random()::text)", ["estado"] = "0",
            ["id_etapa_origem"] = etapa is { } e ? $"'{e}'"
                : unidade is null ? "id_etapa_origem"
                : $"(SELECT et.id FROM orquestracao.etapas_da_execucao et WHERE et.id_execucao = d.id_execucao_origem AND et.unidade = '{unidade}' LIMIT 1)"
        };
        var valores = ColunasDaDelegacao.Select(coluna => trocas.TryGetValue(coluna, out var valor) ? valor : "d." + coluna);
        return $"INSERT INTO orquestracao.delegacoes (id, {string.Join(", ", ColunasDaDelegacao)}) " +
            $"SELECT gen_random_uuid(), {string.Join(", ", valores)} FROM orquestracao.delegacoes d WHERE d.id = '{id}'";
    }

    // A recusa é do banco e pela regra esperada: o texto da exceção (ou o nome da constraint) a identifica.
    private static async Task Recusada(Banco banco, string regra, string sql)
    {
        var erro = await Assert.ThrowsAsync<PostgresException>(() => Executar(banco, sql));
        Assert.Contains(regra, erro.MessageText + " " + erro.ConstraintName);
    }
}
