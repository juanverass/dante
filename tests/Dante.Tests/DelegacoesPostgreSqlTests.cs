using Dante.Application.Agentes;
using Dante.Application.Comum;
using Dante.Application.Execucoes;
using Dante.Application.Grupos;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Dante.Tests.DelegacoesDominioTests;
using static Dante.Tests.FilaDuravelPostgreSqlTests;

namespace Dante.Tests;

// #309 (AD-70), PR 1: persistência das delegações contra PostgreSQL descartável (DANTE_TEST_POSTGRES). Migration com
// backfill sobre dados gravados no schema anterior (execuções, versões de agente e de grupo, com os hashes calculados
// pelo código de antes da #309), constraints e triggers da hierarquia e da delegação, resultado imutável, nenhuma
// exclusão e deduplicação concorrente real da unidade de trabalho.
[Collection("FilaDuravel")]
public sealed class DelegacoesPostgreSqlTests
{
    private const string MigrationAnterior = "20261118122411_VereditosDeRevisao";
    private static readonly Guid Dev = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Rev = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [PostgreSqlFact]
    public async Task MigrationPreservaOsDadosETornaCadaExecucaoExistenteUmaRaiz()
    {
        await using var banco = await Banco.CriarAsync(migrar: false);
        await using (var contexto = new OrquestracaoDbContext(OrquestracaoDbContextFactory.Opcoes(banco.ConnectionString)))
            await contexto.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
        var versaoDev = Guid.NewGuid();
        var versaoRev = Guid.NewGuid();
        var versaoDoGrupo = Guid.NewGuid();
        var avulsa = Guid.NewGuid();
        var tarefa = Guid.NewGuid();
        // Schema anterior: nada de delegação. Hashes calculados pelo código de main antes da #309.
        await Executar(banco, $$"""
            INSERT INTO orquestracao.agentes (id, handle, estado, id_versao_atual, numero_da_versao_atual, id_do_criador, criado_em, atualizado_em)
            VALUES ('{{Dev}}', 'dev', 0, '{{versaoDev}}', 1, 1, now(), now()), ('{{Rev}}', 'rev', 0, '{{versaoRev}}', 1, 1, now(), now());
            INSERT INTO orquestracao.versoes_de_agente (id, id_agente, numero, driver, modelo, esforco, instrucoes, capacidades,
                perfil_padrao, perfil_maximo, pode_escrever, ferramentas, repositorios, execucoes_simultaneas, tokens_por_execucao,
                turnos_por_etapa, minutos_por_etapa, hash_da_configuracao, id_do_autor, criada_em)
            VALUES ('{{versaoDev}}', '{{Dev}}', 1, {{(int)AgentKind.Codex}}, 'gpt-5.5', 'high', E'Revise com cuidado.\nSem pressa.',
                    '{Implementar,Revisar}', {{(int)AgentPermissionProfile.Auto}}, {{(int)AgentPermissionProfile.Auto}}, true,
                    '{Brain,Planilhas}', '{@dante,@outro}', 2, 1000, 5, 30, '{{CompatibilidadeDosHashesTests.HashDaCompleta}}', 1, now()),
                   ('{{versaoRev}}', '{{Rev}}', 1, {{(int)AgentKind.Codex}}, NULL, NULL, NULL, '{}', {{(int)AgentPermissionProfile.Plan}},
                    {{(int)AgentPermissionProfile.Plan}}, false, '{}', '{}', NULL, NULL, NULL, NULL,
                    '{{CompatibilidadeDosHashesTests.HashDaMinima}}', 1, now());
            INSERT INTO orquestracao.grupos_de_agentes (id, nome, estado, id_versao_atual, numero_da_versao_atual, id_do_criador, criado_em, atualizado_em)
            VALUES ('{{Guid.NewGuid()}}', 'engenharia', 0, '{{versaoDoGrupo}}', 1, 1, now(), now());
            INSERT INTO orquestracao.versoes_de_grupo (id, id_grupo, numero, modo, roteiro, rodadas_de_revisao_maximas, paralelismo_maximo,
                repositorios_permitidos, duracao_maxima_em_minutos, hash_da_configuracao, id_do_autor, criada_em)
            SELECT '{{versaoDoGrupo}}', id, 1, 0, '{Implementar:dev,Revisar:rev,Corrigir:dev,AprovacaoHumana}', 2, 1, '{@dante}', 90,
                   '{{CompatibilidadeDosHashesTests.HashDoGrupo}}', 1, now() FROM orquestracao.grupos_de_agentes WHERE nome = 'engenharia';
            INSERT INTO orquestracao.participantes_de_grupo (id, id_versao_do_grupo, id_agente, papel, responsabilidades, pode_escrever)
            VALUES ('{{Guid.NewGuid()}}', '{{versaoDoGrupo}}', '{{Dev}}', 'dev', 'implementa', true),
                   ('{{Guid.NewGuid()}}', '{{versaoDoGrupo}}', '{{Rev}}', 'rev', NULL, false);
            INSERT INTO orquestracao.execucoes (id, numero, tipo, estado, canal, id_do_usuario_no_canal, contexto, objetivo, criada_em, id_versao_do_agente)
            VALUES ('{{avulsa}}', 1, {{(int)TipoDeExecucao.Avulsa}}, {{(int)EstadoDaExecucao.Concluida}}, 'telegram', 1001, '{}', NULL, now(), NULL),
                   ('{{tarefa}}', 2, {{(int)TipoDeExecucao.Tarefa}}, {{(int)EstadoDaExecucao.Pendente}}, 'telegram', 1001, '{}', 'ler', now(), '{{versaoRev}}');
            INSERT INTO orquestracao.etapas_da_execucao (id, id_execucao, numero, tipo, estado, escrita, politica_de_retomada,
                tentativa, chave_de_idempotencia, criada_em)
            VALUES ('{{Guid.NewGuid()}}', '{{tarefa}}', 1, {{(int)TipoDeEtapa.TurnoDeAgente}}, 0, false, 0, 1, '{{tarefa:N}}:1:1', now());
            """);

        await using (var contexto = new OrquestracaoDbContext(OrquestracaoDbContextFactory.Opcoes(banco.ConnectionString)))
        {
            await contexto.Database.MigrateAsync();
            Assert.Empty(await contexto.Database.GetPendingMigrationsAsync());
        }

        // Backfill: as duas execuções são raízes de profundidade 0, apontando para si; nada mais mudou.
        Assert.Equal(2L, await Escalar(banco,
            "SELECT count(*) FROM orquestracao.execucoes WHERE id_execucao_raiz = id AND id_execucao_pai IS NULL AND profundidade = 0"));
        Assert.Equal(2L, await Escalar(banco, "SELECT count(*) FROM orquestracao.execucoes"));
        Assert.Equal(1L, await Escalar(banco,
            $"SELECT count(*) FROM orquestracao.execucoes WHERE id = '{tarefa}' AND numero = 2 AND objetivo = 'ler' AND id_versao_do_agente = '{versaoRev}'"));
        Assert.Equal(0L, await Escalar(banco,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'orquestracao' AND table_name = 'execucoes' " +
            "AND column_name IN ('id_execucao_raiz', 'profundidade') AND column_default IS NOT NULL"));

        await using var provider = Provider(banco.ConnectionString);
        await using (var escopo = provider.CreateAsyncScope())
        {
            var s = escopo.ServiceProvider;
            // As versões gravadas antes continuam íntegras e sem delegação; o grupo também.
            var dev = (await s.GetRequiredService<IVersaoDoAgenteRepository>().ObterPorIdAsync(versaoDev))!;
            var rev = (await s.GetRequiredService<IVersaoDoAgenteRepository>().ObterPorIdAsync(versaoRev))!;
            Assert.True(dev.Integra && rev.Integra);
            Assert.Equal(CompatibilidadeDosHashesTests.HashDaCompleta, dev.Configuracao.Hash());
            Assert.Empty(rev.PodeConsultar);
            Assert.Null(rev.Configuracao.LimitesDeDelegacao);
            var grupo = (await s.GetRequiredService<IVersaoDoGrupoRepository>().ObterComParticipantesAsync(versaoDoGrupo))!;
            Assert.True(grupo.Integra);
            Assert.Equal(CompatibilidadeDosHashesTests.HashDoGrupo, grupo.HashDaConfiguracao);
            // Uma raiz nova, depois da migration, aponta para si mesma (número acima dos gravados à mão sem a sequência).
            await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(
                new Execucao(1000, TipoDeExecucao.Avulsa, "telegram", 1001, "{}", null, Agora));
            await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        }
        // SQL escrito para o schema anterior (sem raiz nem profundidade) ainda grava uma raiz válida.
        await Executar(banco, "INSERT INTO orquestracao.execucoes (id, numero, tipo, estado, canal, id_do_usuario_no_canal, contexto, criada_em) " +
            "VALUES (gen_random_uuid(), 1001, 0, 0, 'telegram', 1001, '{}', now())");
        Assert.Equal(4L, await Escalar(banco,
            "SELECT count(*) FROM orquestracao.execucoes WHERE id_execucao_raiz = id AND profundidade = 0 AND id_execucao_pai IS NULL"));
    }

    [PostgreSqlFact]
    public async Task DelegacaoGravadaComHierarquiaEResultadoImutavelSemExclusao()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var c = await CenarioGravadoAsync(provider);
        var nova = await DelegarAsync(provider, c);

        Assert.Equal(1L, await Escalar(banco,
            $"SELECT count(*) FROM orquestracao.execucoes WHERE id = '{nova.Subordinada.Id}' AND tipo = {(int)TipoDeExecucao.Delegada} " +
            $"AND id_execucao_pai = '{c.Origem.Id}' AND id_execucao_raiz = '{c.Origem.Id}' AND profundidade = 1"));
        await using (var escopo = provider.CreateAsyncScope())
        {
            var repositorio = escopo.ServiceProvider.GetRequiredService<IDelegacaoRepository>();
            var lida = (await repositorio.ObterPorChaveAsync(c.Origem.Id, nova.Delegacao.ChaveDeDeduplicacao))!;
            Assert.Equal((nova.Delegacao.Entrada, nova.Delegacao.HashDaEntrada), (lida.Entrada, lida.HashDaEntrada));
            Assert.Equal([c.Solicitante.Id], lida.CadeiaDeAgentes);
            Assert.Equal(lida.Id, (await repositorio.ObterDaSubordinadaAsync(nova.Subordinada.Id))!.Id);
            Assert.Equal((1, 1), (await repositorio.ContarDaOrigemAsync(c.Origem.Id), await repositorio.ContarDaRaizAsync(c.Origem.Id)));
            // Aceita, inicia e responde pelo domínio; cada passo gravado com xmin.
            lida.Aceitar(Agora);
            await escopo.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
            lida.Iniciar(Agora);
            await escopo.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
            lida.Responder("""{"resposta":"Sim.","confianca":"alta"}""", c.VersaoDestinataria.Id, null, null, Agora);
            await escopo.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        }
        await using (var escopo = provider.CreateAsyncScope())
        {
            var lida = (await escopo.ServiceProvider.GetRequiredService<IDelegacaoRepository>().ObterDaSubordinadaAsync(nova.Subordinada.Id))!;
            Assert.Equal(EstadoDaDelegacao.Respondida, lida.Estado);
            Assert.True(lida.MesmoResultado("""{"confianca":"alta","resposta":"Sim."}"""));
            // Terminal continua sendo a delegação daquela unidade: a chave não é liberada.
            Assert.Equal(lida.Id, (await escopo.ServiceProvider.GetRequiredService<IDelegacaoRepository>()
                .ObterPorChaveAsync(c.Origem.Id, nova.Delegacao.ChaveDeDeduplicacao))!.Id);
        }

        // O banco protege o resultado e a identidade, mesmo por fora do domínio, e recusa remover.
        var id = nova.Delegacao.Id;
        await Recusada(banco, PostgresErrorCodes.RestrictViolation,
            $"UPDATE orquestracao.delegacoes SET resultado = '{{\"resposta\":\"Não.\",\"confianca\":\"baixa\"}}' WHERE id = '{id}'");
        await Recusada(banco, PostgresErrorCodes.RestrictViolation,
            $"UPDATE orquestracao.delegacoes SET estado = {(int)EstadoDaDelegacao.Cancelada} WHERE id = '{id}'");
        await Recusada(banco, PostgresErrorCodes.RestrictViolation, $"DELETE FROM orquestracao.delegacoes WHERE id = '{id}'");
        // A hierarquia e a autoridade da execução não mudam; a execução com filha não é removida.
        await Recusada(banco, PostgresErrorCodes.RestrictViolation,
            $"UPDATE orquestracao.execucoes SET profundidade = 0, id_execucao_pai = NULL, id_execucao_raiz = id, tipo = {(int)TipoDeExecucao.Tarefa} WHERE id = '{nova.Subordinada.Id}'");
        await Recusada(banco, PostgresErrorCodes.RestrictViolation,
            $"UPDATE orquestracao.execucoes SET id_do_usuario_no_canal = 999 WHERE id = '{c.Origem.Id}'");
        await Recusada(banco, PostgresErrorCodes.ForeignKeyViolation, $"DELETE FROM orquestracao.execucoes WHERE id = '{c.Origem.Id}'");
    }

    [PostgreSqlFact]
    public async Task BancoRecusaDelegacaoForaDaRegraMesmoSemPassarPeloDominio()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var c = await CenarioGravadoAsync(provider);
        var nova = await DelegarAsync(provider, c);
        var id = nova.Delegacao.Id;

        // Delegação não terminal: só transições válidas, identidade e pedido imutáveis, escrita nunca.
        await Recusada(banco, PostgresErrorCodes.CheckViolation,
            $"UPDATE orquestracao.delegacoes SET estado = {(int)EstadoDaDelegacao.Respondida} WHERE id = '{id}'");
        await Recusada(banco, PostgresErrorCodes.CheckViolation,
            $"UPDATE orquestracao.delegacoes SET estado = {(int)EstadoDaDelegacao.EmAndamento} WHERE id = '{id}'");
        await Recusada(banco, PostgresErrorCodes.RestrictViolation,
            $"UPDATE orquestracao.delegacoes SET entrada = '{{\"pergunta\":\"outra\",\"referencias\":[]}}' WHERE id = '{id}'");
        await Recusada(banco, PostgresErrorCodes.RestrictViolation,
            $"UPDATE orquestracao.delegacoes SET id_versao_destinataria = id_versao_solicitante WHERE id = '{id}'");
        await Recusada(banco, PostgresErrorCodes.RestrictViolation, $"UPDATE orquestracao.delegacoes SET escrita = true WHERE id = '{id}'");

        // Inserções diretas copiando a válida e quebrando uma regra de cada vez. A subordinada "livre" é uma Delegada
        // válida da origem, sem delegação: o trigger da inserção passa e o que recusa é a regra quebrada.
        var livre = Guid.NewGuid();
        await Executar(banco, DelegadaCrua(c, "profundidade", "1", id: livre));
        await Recusada(banco, PostgresErrorCodes.UniqueViolation, Copia(id));
        await Recusada(banco, PostgresErrorCodes.UniqueViolation, Copia(id, new() { ["id_execucao_subordinada"] = Q(livre) }));
        await Recusada(banco, PostgresErrorCodes.CheckViolation, Copia(id, Livre(livre, "escrita", "true")));
        await Recusada(banco, PostgresErrorCodes.CheckViolation, Copia(id, Livre(livre, "contrato", "'livre.v1'")));
        await Recusada(banco, PostgresErrorCodes.CheckViolation, Copia(id, Livre(livre, "minutos_para_iniciar", "31")));
        await Recusada(banco, PostgresErrorCodes.CheckViolation, Copia(id, Livre(livre, "minutos_de_duracao", "61")));
        await Recusada(banco, PostgresErrorCodes.CheckViolation,
            Copia(id, Livre(livre, "cadeia_de_agentes", "ARRAY[id_agente_destinatario]")));
        await Recusada(banco, PostgresErrorCodes.CheckViolation, Copia(id, Livre(livre, "cadeia_de_agentes", "ARRAY[NULL::uuid]")));
        await Recusada(banco, PostgresErrorCodes.CheckViolation, Copia(id, Livre(livre, "estado", "1")));
        await Recusada(banco, PostgresErrorCodes.CheckViolation, Copia(id, Livre(livre, "hash_da_versao_destinataria", $"'{new string('a', 64)}'")));
        await Recusada(banco, PostgresErrorCodes.CheckViolation,
            Copia(id, Livre(livre, "resultado", "'{}'")));
        var outraRaiz = await RaizAvulsaAsync(provider);
        await Recusada(banco, PostgresErrorCodes.CheckViolation,
            Copia(id, Livre(outraRaiz, "chave_de_deduplicacao", $"'{new string('b', 64)}'")));
        await Recusada(banco, PostgresErrorCodes.ForeignKeyViolation,
            Copia(id, Livre(Guid.NewGuid(), "chave_de_deduplicacao", $"'{new string('c', 64)}'")));
        // Com outra chave e a subordinada livre, a cópia é válida: só a regra quebrada recusava.
        await Executar(banco, Copia(id, Livre(livre, "chave_de_deduplicacao", $"'{new string('d', 64)}'")));

        // Execução delegada inserida à mão fora da hierarquia do pai: profundidade, raiz ou autoridade diferentes.
        await Recusada(banco, PostgresErrorCodes.CheckViolation, DelegadaCrua(c, "profundidade", "1", raiz: Guid.NewGuid()));
        await Recusada(banco, PostgresErrorCodes.CheckViolation, DelegadaCrua(c, "id_do_usuario_no_canal", "999"));
        await Recusada(banco, PostgresErrorCodes.CheckViolation, DelegadaCrua(c, "profundidade", "2"));
        await Recusada(banco, PostgresErrorCodes.ForeignKeyViolation, DelegadaCrua(c, "profundidade", "1", pai: Guid.NewGuid()));
        // Raiz que não aponta para si, e Delegada sem pai.
        await Recusada(banco, PostgresErrorCodes.CheckViolation,
            $"INSERT INTO orquestracao.execucoes (id, numero, tipo, estado, canal, id_do_usuario_no_canal, contexto, criada_em, " +
            $"id_execucao_raiz, profundidade) VALUES (gen_random_uuid(), 9001, 0, 0, 'telegram', 1001, '{{}}', now(), '{c.Origem.Id}', 0)");
        await Recusada(banco, PostgresErrorCodes.CheckViolation,
            $"INSERT INTO orquestracao.execucoes (id, numero, tipo, estado, canal, id_do_usuario_no_canal, contexto, criada_em, " +
            $"id_versao_do_agente, id_execucao_raiz, profundidade) SELECT x, 9002, {(int)TipoDeExecucao.Delegada}, 0, 'telegram', 1001, '{{}}', " +
            $"now(), '{c.VersaoDestinataria.Id}', x, 0 FROM (SELECT gen_random_uuid() AS x) AS novo");

        // Versão de agente com delegação fora do teto ou consultados sem a concessão coordenacao.
        await Recusada(banco, PostgresErrorCodes.CheckViolation, VersaoCrua(c, "'{revisor}'", "'{}'"));
        await Recusada(banco, PostgresErrorCodes.CheckViolation, VersaoCrua(c, "'{}'", "'{Coordenacao}'", "2, 4, 30, 60, 1"));
        await Recusada(banco, PostgresErrorCodes.CheckViolation, VersaoCrua(c, "'{}'", "'{Coordenacao}'", "1, 4, 30, 60, NULL"));
    }

    // Duas gravações simultâneas da mesma unidade de trabalho (mesma raiz e chave), cada uma com a sua subordinada, em
    // conexões diferentes: o índice único parcial deixa passar uma; a outra falha inteira (nem a subordinada fica) e,
    // relida, devolve a existente quando o destinatário é o mesmo e é recusada quando é outro agente.
    [PostgreSqlFact]
    public async Task DeduplicacaoConcorrenteGravaUmaSoDelegacaoPorUnidade()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var c = await CenarioGravadoAsync(provider, consulta: ["auditor", "revisor"]);
        var auditor = await AgenteGravadoAsync(provider, "auditor", ConfiguracaoDoDestinatario());

        for (var rodada = 0; rodada < 5; rodada++)
        {
            var portao = new TaskCompletionSource();
            async Task<Exception?> Tentar(PedidoDeDelegacao pedido)
            {
                await using var escopo = provider.CreateAsyncScope();
                var s = escopo.ServiceProvider;
                var nova = Delegacao.Criar(pedido, await s.GetRequiredService<IExecucaoRepository>().ReservarNumeroAsync(), Agora);
                await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(nova.Subordinada);
                await s.GetRequiredService<IDelegacaoRepository>().AdicionarAsync(nova.Delegacao);
                await portao.Task;
                try
                {
                    await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }
            var pedido = c.Pedido($"Unidade {rodada}");
            var outro = pedido with { Destinatario = auditor.Agente, VersaoDestinataria = auditor.Versao };
            var tentativas = new[] { Tentar(pedido), Tentar(rodada % 2 == 0 ? pedido : outro) };
            portao.SetResult();
            var resultados = await Task.WhenAll(tentativas);
            Assert.Single(resultados, r => r is null);
            Assert.IsType<ConflitoDeConcorrenciaException>(Assert.Single(resultados, r => r is not null));
        }
        Assert.Equal(5L, await Escalar(banco, "SELECT count(*) FROM orquestracao.delegacoes"));
        Assert.Equal(5L, await Escalar(banco, $"SELECT count(*) FROM orquestracao.execucoes WHERE tipo = {(int)TipoDeExecucao.Delegada}"));

        // Quem perdeu relê pela chave: mesmo destinatário → a mesma delegação; outro agente → unidade já atribuída.
        await using var leitura = provider.CreateAsyncScope();
        var chave = Delegacao.Criar(c.Pedido("Unidade 0"), 999_999, Agora).Delegacao.ChaveDeDeduplicacao;
        var existente = (await leitura.ServiceProvider.GetRequiredService<IDelegacaoRepository>()
            .ObterPorChaveAsync(c.Origem.Id, chave))!;
        Assert.Same(existente, existente.ConferirRepeticao(existente.IdAgenteDestinatario));
        Assert.Equal(MotivoDaRecusaDeDelegacao.UnidadeJaAtribuida, Assert.Throws<DelegacaoRecusadaException>(() =>
            existente.ConferirRepeticao(existente.IdAgenteDestinatario == auditor.Agente.Id ? c.Destinatario.Id : auditor.Agente.Id)).Motivo);
    }

    // A identidade da unidade vale em qualquer estado (revisão do PR #318): depois de Respondida, Falhou, Expirada,
    // Cancelada ou Recusada, o banco recusa outra delegação com a mesma raiz e chave (mesmo destinatário ou outro, também
    // em gravações simultâneas), nada é criado nem alterado, e quem relê pela chave recebe a original com o estado e o
    // resultado gravados: o mesmo destinatário a recebe, outro é recusado com UnidadeJaAtribuida.
    [PostgreSqlFact]
    public async Task RepeticaoDepoisDeEstadoTerminalDevolveAOriginalSemReabrirNemReatribuir()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var c = await CenarioGravadoAsync(provider, consulta: ["auditor", "revisor"]);
        var auditor = await AgenteGravadoAsync(provider, "auditor", ConfiguracaoDoDestinatario());
        var terminais = new (EstadoDaDelegacao Estado, Action<Delegacao>[] Passos)[]
        {
            (EstadoDaDelegacao.Respondida, [d => d.Aceitar(Agora), d => d.Iniciar(Agora),
                d => d.Responder("""{"resposta":"Sim.","confianca":"alta"}""", c.VersaoDestinataria.Id, null, null, Agora)]),
            (EstadoDaDelegacao.Falhou, [d => d.Aceitar(Agora), d => d.Falhar("subordinada falhou", Agora)]),
            (EstadoDaDelegacao.Expirada, [d => d.Aceitar(Agora), d => d.Iniciar(Agora), d => d.Expirar("prazo", Agora)]),
            (EstadoDaDelegacao.Cancelada, [d => d.Cancelar("pedido do dono", Agora)]),
            (EstadoDaDelegacao.Recusada, [d => d.Recusar("versão alterada", Agora)])
        };

        foreach (var (estado, passos) in terminais)
        {
            var pedido = c.Pedido($"Unidade {estado}");
            var original = await DelegarAsync(provider, c, pedido);
            foreach (var passo in passos)
            {
                await using var escopo = provider.CreateAsyncScope();
                var lida = (await escopo.ServiceProvider.GetRequiredService<IDelegacaoRepository>().ObterPorIdAsync(original.Delegacao.Id))!;
                passo(lida);
                await escopo.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
            }
            var gravada = await Linha(banco, original.Delegacao.Id);
            var delegacoes = await Escalar(banco, "SELECT count(*) FROM orquestracao.delegacoes");
            var execucoes = await Escalar(banco, "SELECT count(*) FROM orquestracao.execucoes");

            // Repetição para o mesmo destinatário e para outro, em sequência e simultâneas: o banco recusa todas.
            var outro = pedido with { Destinatario = auditor.Agente, VersaoDestinataria = auditor.Versao };
            Assert.IsType<ConflitoDeConcorrenciaException>(await GravarNovaAsync(provider, pedido));
            Assert.IsType<ConflitoDeConcorrenciaException>(await GravarNovaAsync(provider, outro));
            var simultaneas = await Task.WhenAll(GravarNovaAsync(provider, pedido), GravarNovaAsync(provider, outro));
            Assert.All(simultaneas, falha => Assert.IsType<ConflitoDeConcorrenciaException>(falha));

            // Nada novo, nada reaberto: a linha original é a mesma, byte a byte (estado, resultado, motivo, datas, xmin).
            Assert.Equal(delegacoes, await Escalar(banco, "SELECT count(*) FROM orquestracao.delegacoes"));
            Assert.Equal(execucoes, await Escalar(banco, "SELECT count(*) FROM orquestracao.execucoes"));
            Assert.Equal(gravada, await Linha(banco, original.Delegacao.Id));

            // Quem relê pela chave recebe a original, com estado e resultado; mesmo destinatário sim, outro não.
            await using var leitura = provider.CreateAsyncScope();
            var chave = Delegacao.Criar(pedido, 999_999, Agora).Delegacao.ChaveDeDeduplicacao;
            Assert.Equal(original.Delegacao.ChaveDeDeduplicacao, chave);
            var existente = (await leitura.ServiceProvider.GetRequiredService<IDelegacaoRepository>()
                .ObterPorChaveAsync(c.Origem.Id, chave))!;
            Assert.Equal((original.Delegacao.Id, estado), (existente.Id, existente.Estado));
            Assert.Same(existente, existente.ConferirRepeticao(c.Destinatario.Id));
            Assert.Equal(MotivoDaRecusaDeDelegacao.UnidadeJaAtribuida,
                Assert.Throws<DelegacaoRecusadaException>(() => existente.ConferirRepeticao(auditor.Agente.Id)).Motivo);
            if (estado == EstadoDaDelegacao.Respondida)
            {
                Assert.True(existente.MesmoResultado("""{"confianca":"alta","resposta":"Sim."}"""));
                Assert.Equal(ContratoDeDelegacao.Hash(existente.Resultado!), existente.HashDoResultado);
            }
            else
            {
                Assert.Null(existente.Resultado);
                Assert.NotNull(existente.Motivo);
            }
            Assert.Equal(estado is not (EstadoDaDelegacao.Recusada or EstadoDaDelegacao.Cancelada), existente.AceitaEm is not null);
            Assert.NotNull(existente.EncerradaEm);
        }
    }

    // Grava uma delegação nova (com a sua subordinada) para o pedido; devolve a exceção do salvamento, ou nulo.
    private static async Task<Exception?> GravarNovaAsync(IServiceProvider provider, PedidoDeDelegacao pedido)
    {
        await using var escopo = provider.CreateAsyncScope();
        var s = escopo.ServiceProvider;
        var nova = Delegacao.Criar(pedido, await s.GetRequiredService<IExecucaoRepository>().ReservarNumeroAsync(), Agora);
        await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(nova.Subordinada);
        await s.GetRequiredService<IDelegacaoRepository>().AdicionarAsync(nova.Delegacao);
        try
        {
            await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    // A linha inteira da delegação como texto, para comparar antes e depois.
    private static async Task<string> Linha(Banco banco, Guid id)
    {
        await using var conexao = new NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(
            $"SELECT row_to_json(d)::text || xmin::text FROM orquestracao.delegacoes d WHERE id = '{id}'", conexao);
        return (string)(await comando.ExecuteScalarAsync())!;
    }

    [PostgreSqlFact]
    public async Task MigrationEhReversivelSemDeixarFuncoesNemColunas()
    {
        await using var banco = await FilaAsync();
        await using (var contexto = new OrquestracaoDbContext(OrquestracaoDbContextFactory.Opcoes(banco.ConnectionString)))
        {
            await contexto.GetService<IMigrator>().MigrateAsync(MigrationAnterior);
            Assert.Equal(0L, await Escalar(banco,
                "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'orquestracao' " +
                "AND p.proname IN ('conferir_hierarquia_da_execucao', 'conferir_delegacao', 'proteger_delegacao')"));
            Assert.Equal(0L, await Escalar(banco,
                "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'orquestracao' AND " +
                "(table_name = 'delegacoes' OR column_name IN ('id_execucao_raiz', 'id_execucao_pai', 'profundidade', 'pode_consultar'))"));
            await contexto.Database.MigrateAsync();
            Assert.Empty(await contexto.Database.GetPendingMigrationsAsync());
        }
        Assert.Equal(3L, await Escalar(banco,
            "SELECT count(*) FROM pg_trigger WHERE tgname IN ('trg_execucoes_hierarquia', 'trg_delegacoes_conferidas', 'trg_delegacoes_protegidas')"));
    }

    private sealed record AgenteGravado(Agente Agente, VersaoDoAgente Versao);

    // O Cenario do domínio, gravado: agentes, versões, a Tarefa de origem em execução e a etapa de leitura em curso.
    private static async Task<Cenario> CenarioGravadoAsync(IServiceProvider provider, IEnumerable<string>? consulta = null)
    {
        await using var escopo = provider.CreateAsyncScope();
        var s = escopo.ServiceProvider;
        var numero = await s.GetRequiredService<IExecucaoRepository>().ReservarNumeroAsync();
        var c = new Cenario(ConfiguracaoDoSolicitante(consulta), numero: numero);
        await s.GetRequiredService<IAgenteRepository>().AdicionarAsync(c.Solicitante);
        await s.GetRequiredService<IAgenteRepository>().AdicionarAsync(c.Destinatario);
        await s.GetRequiredService<IVersaoDoAgenteRepository>().AdicionarAsync(c.VersaoSolicitante);
        await s.GetRequiredService<IVersaoDoAgenteRepository>().AdicionarAsync(c.VersaoDestinataria);
        await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(c.Origem);
        await s.GetRequiredService<IEtapaDaExecucaoRepository>().AdicionarAsync(c.Etapa);
        await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        return c;
    }

    private static async Task<AgenteGravado> AgenteGravadoAsync(IServiceProvider provider, string handle,
        ConfiguracaoDoAgente configuracao)
    {
        await using var escopo = provider.CreateAsyncScope();
        var s = escopo.ServiceProvider;
        var agente = new Agente(handle, null, null, 1, Agora);
        var versao = agente.NovaVersao(configuracao, 1, Agora);
        await s.GetRequiredService<IAgenteRepository>().AdicionarAsync(agente);
        await s.GetRequiredService<IVersaoDoAgenteRepository>().AdicionarAsync(versao);
        await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        return new AgenteGravado(agente, versao);
    }

    private static async Task<NovaDelegacao> DelegarAsync(IServiceProvider provider, Cenario c, PedidoDeDelegacao? pedido = null)
    {
        await using var escopo = provider.CreateAsyncScope();
        var s = escopo.ServiceProvider;
        var nova = Delegacao.Criar(pedido ?? c.Pedido(), await s.GetRequiredService<IExecucaoRepository>().ReservarNumeroAsync(), Agora);
        await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(nova.Subordinada);
        await s.GetRequiredService<IDelegacaoRepository>().AdicionarAsync(nova.Delegacao);
        await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        return nova;
    }

    private static async Task<Guid> RaizAvulsaAsync(IServiceProvider provider)
    {
        await using var escopo = provider.CreateAsyncScope();
        var s = escopo.ServiceProvider;
        var raiz = new Execucao(await s.GetRequiredService<IExecucaoRepository>().ReservarNumeroAsync(), TipoDeExecucao.Avulsa,
            "telegram", 1001, "{}", null, Agora);
        await s.GetRequiredService<IExecucaoRepository>().AdicionarAsync(raiz);
        await s.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        return raiz.Id;
    }

    private static readonly string[] ColunasDaDelegacao =
    [
        "id_execucao_origem", "id_etapa_origem", "id_execucao_subordinada", "id_execucao_raiz", "id_agente_solicitante",
        "id_versao_solicitante", "hash_da_versao_solicitante", "id_agente_destinatario", "handle_do_destinatario",
        "id_versao_destinataria", "hash_da_versao_destinataria", "contrato", "entrada", "hash_da_entrada", "objetivo_redigido",
        "objetivo_alterado_pela_redacao", "hash_do_objetivo", "criterios", "minutos_para_iniciar", "minutos_de_duracao", "escrita",
        "cadeia_de_agentes", "chave_de_deduplicacao", "estado", "criada_em", "resultado"
    ];

    // Cópia crua da delegação gravada, com colunas trocadas por expressões SQL.
    private static string Copia(Guid id, Dictionary<string, string>? trocas = null)
    {
        var valores = ColunasDaDelegacao.Select(coluna => trocas is not null && trocas.TryGetValue(coluna, out var valor) ? valor : coluna);
        return $"INSERT INTO orquestracao.delegacoes (id, {string.Join(", ", ColunasDaDelegacao)}) " +
            $"SELECT gen_random_uuid(), {string.Join(", ", valores)} FROM orquestracao.delegacoes WHERE id = '{id}'";
    }

    private static Dictionary<string, string> Livre(Guid subordinada, string coluna, string valor) =>
        new() { ["id_execucao_subordinada"] = Q(subordinada), [coluna] = valor };

    private static string Q(Guid id) => $"'{id}'";

    // Execução Delegada filha da origem, com um campo trocado (e, opcionalmente, outra raiz ou outro pai).
    private static string DelegadaCrua(Cenario c, string coluna, string valor, Guid? raiz = null, Guid? pai = null, Guid? id = null)
    {
        var colunas = new Dictionary<string, string>
        {
            ["numero"] = Random.Shared.Next(10_000, 1_000_000).ToString(), ["tipo"] = ((int)TipoDeExecucao.Delegada).ToString(),
            ["estado"] = "0", ["canal"] = "'telegram'", ["id_do_usuario_no_canal"] = "1001", ["contexto"] = "'{}'",
            ["criada_em"] = "now()", ["id_versao_do_agente"] = $"'{c.VersaoDestinataria.Id}'",
            ["id_execucao_pai"] = $"'{pai ?? c.Origem.Id}'", ["id_execucao_raiz"] = $"'{raiz ?? c.Origem.Id}'", ["profundidade"] = "1"
        };
        colunas[coluna] = valor;
        return $"INSERT INTO orquestracao.execucoes (id, {string.Join(", ", colunas.Keys)}) " +
            $"VALUES ({(id is { } escolhido ? Q(escolhido) : "gen_random_uuid()")}, {string.Join(", ", colunas.Values)})";
    }

    private static string VersaoCrua(Cenario c, string consulta, string ferramentas, string limites = "NULL, NULL, NULL, NULL, NULL") =>
        $$"""
        INSERT INTO orquestracao.versoes_de_agente (id, id_agente, numero, driver, capacidades, perfil_padrao, perfil_maximo,
            pode_escrever, ferramentas, repositorios, hash_da_configuracao, id_do_autor, criada_em, pode_consultar,
            profundidade_maxima_de_delegacao, delegacoes_por_execucao, minutos_para_iniciar_delegacao, minutos_de_delegacao,
            paralelismo_de_delegacoes)
        VALUES (gen_random_uuid(), '{{c.Solicitante.Id}}', {{Random.Shared.Next(100, 100_000)}}, 0, '{}', 2, 2, false, {{ferramentas}}, '{}',
            '{{new string('a', 64)}}', 1, now(), {{consulta}}, {{limites}})
        """;

    private static async Task Recusada(Banco banco, string codigo, string sql) =>
        Assert.Equal(codigo, (await Assert.ThrowsAsync<PostgresException>(() => Executar(banco, sql))).SqlState);
}
