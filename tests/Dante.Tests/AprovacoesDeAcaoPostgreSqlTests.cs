using Dante.Application.Agentes;
using Dante.Application.Comum;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Dante.Tests.AprovacaoDeAcaoDominioTests;
using static Dante.Tests.FilaDuravelPostgreSqlTests;

namespace Dante.Tests;

// #311 (AD-72), PR 1: aprovações de ação protegida contra PostgreSQL real e descartável (DANTE_TEST_POSTGRES). O banco
// confere de novo o que o domínio garante: hash = SHA-256 do payload, payload preso às colunas, ação protegida do
// catálogo, dono da raiz, transições, decisor, prazos, imutabilidade, replay por pedido novo e execução única (xmin).
public sealed class AprovacoesDeAcaoPostgreSqlTests
{
    private const string Tabela = "orquestracao.aprovacoes_de_acao";

    [PostgreSqlFact]
    public async Task PedidoPersistidoSobreviveAoReinicioComHashConferidoPeloBanco()
    {
        await using var banco = await FilaAsync();
        AprovacaoDeAcao pedido;
        await using (var provider = Provider(banco.ConnectionString))
        {
            var raiz = await RaizAsync(provider);
            pedido = await SolicitarAsync(provider, raiz);
        }
        // Outro provider, nada em memória: o pedido inteiro volta do ledger.
        await using (var reiniciado = Provider(banco.ConnectionString))
        {
            var lido = (await RepositorioAsync(reiniciado, r => r.ObterPorIdAsync(pedido.Id)))!;
            Assert.Equal((pedido.Payload, pedido.Hash, pedido.Acao, pedido.Risco, pedido.Alvo, pedido.ExpiraEm),
                (lido.Payload, lido.Hash, lido.Acao, lido.Risco, lido.Alvo, lido.ExpiraEm));
            Assert.Equal((EstadoDaAprovacaoDeAcao.Pendente, Canal, Dono), (lido.Estado, lido.CanalDoDono, lido.IdDoDono));
            Assert.Equal([pedido.Id], (await RepositorioAsync(reiniciado, r => r.ListarEmAbertoDoDonoAsync(Canal, Dono)))
                .Select(p => p.Id));
            Assert.Empty(await RepositorioAsync(reiniciado, r => r.ListarEmAbertoDoDonoAsync(Canal, 999)));
            Assert.Equal([pedido.Id], (await RepositorioAsync(reiniciado, r => r.ListarDaExecucaoAsync(pedido.IdExecucao)))
                .Select(p => p.Id));
        }
        Assert.Equal(1L, await Escalar(banco,
            $"SELECT count(*) FROM {Tabela} WHERE hash = encode(sha256(convert_to(payload, 'UTF8')), 'hex')"));
    }

    [PostgreSqlFact]
    public async Task BancoRecusaPayloadAdulteradoAcaoForaDoCatalogoEOutroDono()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var pedido = await SolicitarAsync(provider, await RaizAsync(provider));
        var copia = $$"""
            INSERT INTO {{Tabela}} (id, id_execucao, id_execucao_raiz, id_etapa, acao, risco, escopo, alvo, canal_do_dono, id_do_dono,
                payload, hash, estado, criada_em, expira_em)
            SELECT gen_random_uuid(), id_execucao, id_execucao_raiz, id_etapa, {0}, {1}, escopo, alvo, canal_do_dono, {2},
                {3}, {4}, {5}, criada_em, expira_em FROM {{Tabela}} WHERE id = '{{pedido.Id}}'
            """;
        string Copia(string acao = "acao", string risco = "risco", string dono = "id_do_dono", string payload = "payload",
            string? hash = null, string estado = "0") =>
            string.Format(copia, acao, risco, dono, payload, hash ?? $"encode(sha256(convert_to({payload}, 'UTF8')), 'hex')", estado);

        // Payload alterado (outro artefato) com o hash antigo: o banco recalcula e recusa.
        var outroArtefato = $"replace(payload, '{Artefato}', '{new string('c', 64)}')";
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErroAsync(banco, Copia(payload: outroArtefato, hash: "hash")));
        // Payload de outra execução, ação fora do catálogo ou com risco diferente do catálogo: recusados.
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErroAsync(banco,
            Copia(payload: $"replace(payload, '{pedido.IdExecucao}', '{Guid.NewGuid()}')")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErroAsync(banco, Copia(acao: "'dante.push'",
            payload: "replace(payload, 'dante.entregar_artefato_do_roteiro', 'dante.push')")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await ErroAsync(banco, Copia(risco: "2")));
        // Payload coerente, mas de um "dono" que não é o da raiz: o trigger recusa.
        Assert.Equal(PostgresErrorCodes.RestrictViolation, await ErroAsync(banco, Copia(dono: "999",
            payload: $"replace(payload, '\"usuario\":{Dono}', '\"usuario\":999')")));
        // Nascer já aprovado: recusado (o check da decisão ou o trigger).
        Assert.NotNull(await ErroAsync(banco, Copia(estado: "1")));
        // A cópia idêntica é replay do mesmo payload vigente: índice único.
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await ErroAsync(banco, Copia()));
        Assert.Equal(1L, await Escalar(banco, $"SELECT count(*) FROM {Tabela}"));

        // Pedido gravado: nada do pedido muda, e ele não é removido.
        Assert.Equal(PostgresErrorCodes.RestrictViolation, await ErroAsync(banco,
            $"UPDATE {Tabela} SET expira_em = expira_em + interval '1 day', estado = 1, canal_do_decisor = canal_do_dono, " +
            "id_do_decisor = id_do_dono, decidida_em = criada_em"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, await ErroAsync(banco, $"DELETE FROM {Tabela}"));
    }

    [PostgreSqlFact]
    public async Task BancoSoAceitaATransicaoDoDonoNoPrazo()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var pedido = await SolicitarAsync(provider, await RaizAsync(provider));
        var onde = $"WHERE id = '{pedido.Id}'";
        // Decisor que não é o dono, decisão depois do prazo, pulo direto para a execução: recusados.
        Assert.Equal(PostgresErrorCodes.RestrictViolation, await ErroAsync(banco,
            $"UPDATE {Tabela} SET estado = 1, canal_do_decisor = canal_do_dono, id_do_decisor = 999, decidida_em = criada_em {onde}"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, await ErroAsync(banco,
            $"UPDATE {Tabela} SET estado = 1, canal_do_decisor = canal_do_dono, id_do_decisor = id_do_dono, decidida_em = expira_em {onde}"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, await ErroAsync(banco,
            $"UPDATE {Tabela} SET estado = 5, canal_do_decisor = canal_do_dono, id_do_decisor = id_do_dono, " +
            $"decidida_em = criada_em, iniciada_em = criada_em {onde}"));
        // Expirar antes do prazo, ou "atualizar" sem transição: recusados.
        Assert.Equal(PostgresErrorCodes.RestrictViolation, await ErroAsync(banco,
            $"UPDATE {Tabela} SET estado = 3, encerrada_em = criada_em {onde}"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, await ErroAsync(banco, $"UPDATE {Tabela} SET motivo = 'x' {onde}"));

        // Pelo domínio: aprovada pelo dono; a decisão não muda depois.
        await AlterarAsync(provider, pedido.Id, p => p.Aprovar(Canal, Dono, p.Hash[..12], Agora.AddMinutes(1)));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, await ErroAsync(banco,
            $"UPDATE {Tabela} SET estado = 4, encerrada_em = criada_em, id_do_decisor = 999 {onde}"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, await ErroAsync(banco,
            $"UPDATE {Tabela} SET estado = 5, iniciada_em = expira_em {onde}"));
    }

    [PostgreSqlFact]
    public async Task DuasDecisoesOuDuasExecucoesSimultaneasSoUmaGrava()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var raiz = await RaizAsync(provider);
        var pedido = await SolicitarAsync(provider, raiz);

        // Aprovar e recusar ao mesmo tempo, cada um no seu escopo: exatamente uma decisão vale.
        var decisoes = await CorridaAsync(provider, pedido.Id,
            p => p.Aprovar(Canal, Dono, p.Hash, Agora.AddMinutes(1)),
            p => p.Recusar(Canal, Dono, "não", Agora.AddMinutes(1)));
        Assert.Equal(1, decisoes.Count(ok => ok));
        var decidido = (await RepositorioAsync(provider, r => r.ObterPorIdAsync(pedido.Id)))!;
        Assert.Contains(decidido.Estado, new[] { EstadoDaAprovacaoDeAcao.Aprovada, EstadoDaAprovacaoDeAcao.Recusada });

        // Pedido aprovado (outro payload) e duas tentativas de execução simultâneas: o CAS deixa passar uma só.
        var segundo = await SolicitarAsync(provider, raiz, artefato: new string('c', 64));
        await AlterarAsync(provider, segundo.Id, p => p.Aprovar(Canal, Dono, p.Hash, Agora.AddMinutes(1)));
        var payloadAtual = Payload(raiz, artefato: new string('c', 64));
        var execucoes = await CorridaAsync(provider, segundo.Id,
            p => Assert.Equal(InicioDaExecucaoDaAcao.Iniciada, p.IniciarExecucao(payloadAtual, Agora.AddMinutes(2))),
            p => Assert.Equal(InicioDaExecucaoDaAcao.Iniciada, p.IniciarExecucao(payloadAtual, Agora.AddMinutes(2))));
        Assert.Equal(1, execucoes.Count(ok => ok));
        Assert.Equal(1L, await Escalar(banco, $"SELECT count(*) FROM {Tabela} WHERE estado = 5"));
        // E uma terceira, depois, encontra a aprovação já em execução.
        await Assert.ThrowsAsync<TransicaoInvalidaException>(() => AlterarAsync(provider, segundo.Id,
            p => p.IniciarExecucao(payloadAtual, Agora.AddMinutes(3))));
    }

    [PostgreSqlFact]
    public async Task ReplayPorPedidoNovoERecusadoEnquantoOPayloadEstaVigenteOuJaTeveEfeito()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var raiz = await RaizAsync(provider);

        // Recusado: o mesmo payload pode ser pedido de novo (nada foi autorizado).
        var recusado = await SolicitarAsync(provider, raiz);
        await AlterarAsync(provider, recusado.Id, p => p.Recusar(Canal, Dono, "não", Agora.AddMinutes(1)));
        var executado = await SolicitarAsync(provider, raiz);
        // Pendente: um segundo pedido igual é replay.
        await Assert.ThrowsAsync<ConflitoDeConcorrenciaException>(() => SolicitarAsync(provider, raiz));
        await AlterarAsync(provider, executado.Id, p => p.Aprovar(Canal, Dono, p.Hash, Agora.AddMinutes(1)));
        await AlterarAsync(provider, executado.Id, p => p.IniciarExecucao(Payload(raiz), Agora.AddMinutes(2)));
        await AlterarAsync(provider, executado.Id, p => p.Concluir("commit " + new string('1', 40), Agora.AddMinutes(3)));
        // Executado: pedir de novo o mesmo payload seria uma segunda execução.
        await Assert.ThrowsAsync<ConflitoDeConcorrenciaException>(() => SolicitarAsync(provider, raiz));

        // Incerto: também não volta a ser pedido (nenhuma repetição automática do que pode ter tido efeito).
        var incerto = await SolicitarAsync(provider, raiz, artefato: new string('c', 64));
        await AlterarAsync(provider, incerto.Id, p => p.Aprovar(Canal, Dono, p.Hash, Agora.AddMinutes(1)));
        await AlterarAsync(provider, incerto.Id, p => p.IniciarExecucao(Payload(raiz, artefato: new string('c', 64)), Agora.AddMinutes(2)));
        await AlterarAsync(provider, incerto.Id, p => p.MarcarIncerta("Worker reiniciado.", "HEAD inalterado", Agora.AddMinutes(3)));
        await Assert.ThrowsAsync<ConflitoDeConcorrenciaException>(() => SolicitarAsync(provider, raiz, artefato: new string('c', 64)));
        // Terminal: o banco não aceita mais nenhuma mudança (nem a "repetição" por SQL direto).
        Assert.Equal(PostgresErrorCodes.RestrictViolation, await ErroAsync(banco,
            $"UPDATE {Tabela} SET estado = 6, encerrada_em = encerrada_em WHERE id = '{incerto.Id}'"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, await ErroAsync(banco,
            $"UPDATE {Tabela} SET resultado = 'outro' WHERE id = '{executado.Id}'"));
        var lido = (await RepositorioAsync(provider, r => r.ObterPorIdAsync(incerto.Id)))!;
        Assert.Equal((EstadoDaAprovacaoDeAcao.Incerto, "HEAD inalterado"), (lido.Estado, lido.Resultado));
    }

    [PostgreSqlFact]
    public async Task ExpiracaoERevogacaoPersistemESoVencidasSaoListadas()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var raiz = await RaizAsync(provider);
        var curto = await SolicitarAsync(provider, raiz, validade: TimeSpan.FromMinutes(10));
        var longo = await SolicitarAsync(provider, raiz, artefato: new string('c', 64));
        var vencidas = await RepositorioAsync(provider, r => r.ListarVencidasAsync(Agora.AddMinutes(10), 10));
        Assert.Equal([curto.Id], vencidas.Select(p => p.Id));
        await AlterarAsync(provider, curto.Id, p => p.Expirar(Agora.AddMinutes(10)));
        Assert.Empty(await RepositorioAsync(provider, r => r.ListarVencidasAsync(Agora.AddMinutes(10), 10)));
        Assert.Equal([longo.Id], (await RepositorioAsync(provider, r => r.ListarEmAbertoDoDonoAsync(Canal, Dono))).Select(p => p.Id));

        // Revogação de uma aprovada (concessão revogada, agente desativado): cancelada, e a execução não começa mais.
        await AlterarAsync(provider, longo.Id, p => p.Aprovar(Canal, Dono, p.Hash, Agora.AddMinutes(1)));
        await AlterarAsync(provider, longo.Id, p => p.Cancelar("Concessão revogada.", Agora.AddMinutes(2)));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, await ErroAsync(banco,
            $"UPDATE {Tabela} SET estado = 5, iniciada_em = decidida_em, encerrada_em = NULL WHERE id = '{longo.Id}'"));
        var lido = (await RepositorioAsync(provider, r => r.ObterPorIdAsync(longo.Id)))!;
        Assert.Equal((EstadoDaAprovacaoDeAcao.Cancelada, "Concessão revogada.", Dono), (lido.Estado, lido.Motivo, lido.IdDoDecisor));
    }

    [PostgreSqlFact]
    public async Task SolicitanteAgenteGravaAVersaoCongeladaEEtapaSoDaPropriaExecucao()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var idVersao = await ReservasDeWorktreePostgreSqlTests.VersaoAsync(provider);
        string hashDaVersao;
        await using (var escopo = provider.CreateAsyncScope())
            hashDaVersao = (await escopo.ServiceProvider.GetRequiredService<IVersaoDoAgenteRepository>().ObterPorIdAsync(idVersao))!
                .HashDaConfiguracao;
        var (execucao, etapa) = await CriarAsync(provider, "aprovacao:1");
        var solicitante = SolicitanteDaAcao.Agente(idVersao, hashDaVersao);
        var pedido = await SalvarAsync(provider, AprovacaoDeAcao.Solicitar(
            Payload(execucao, solicitante: solicitante, idEtapa: etapa.Id), execucao, execucao, etapa, Agora));
        var lido = (await RepositorioAsync(provider, r => r.ObterPorIdAsync(pedido.Id)))!;
        Assert.Equal((idVersao, hashDaVersao, etapa.Id, Dono), (lido.IdVersaoDoSolicitante, lido.HashDaVersaoDoSolicitante,
            lido.IdEtapa, lido.IdDoDono));

        // Versão inexistente: chave estrangeira.
        await Assert.ThrowsAsync<DbUpdateException>(() => SalvarAsync(provider, AprovacaoDeAcao.Solicitar(
            Payload(execucao, artefato: new string('c', 64), solicitante: SolicitanteDaAcao.Agente(Guid.NewGuid(), hashDaVersao)),
            execucao, execucao, null, Agora)));
        // Etapa de outra execução (payload coerente, por SQL direto): a chave composta da etapa recusa.
        var (outra, etapaDeOutra) = await CriarAsync(provider, "aprovacao:2");
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await ErroAsync(banco, $"""
            INSERT INTO {Tabela} (id, id_execucao, id_execucao_raiz, id_etapa, acao, risco, escopo, alvo, id_versao_do_solicitante,
                hash_da_versao_do_solicitante, canal_do_dono, id_do_dono, payload, hash, estado, criada_em, expira_em)
            SELECT gen_random_uuid(), id_execucao, id_execucao_raiz, '{etapaDeOutra.Id}', acao, risco, escopo, alvo,
                id_versao_do_solicitante, hash_da_versao_do_solicitante, canal_do_dono, id_do_dono, p.novo,
                encode(sha256(convert_to(p.novo, 'UTF8')), 'hex'), 0, criada_em, expira_em
            FROM {Tabela}, LATERAL (SELECT replace(replace(payload, '{etapa.Id}', '{etapaDeOutra.Id}'), '{Artefato}', '{new string('9', 64)}') AS novo) p
            WHERE id = '{pedido.Id}'
            """));
        Assert.NotEqual(outra.Id, execucao.Id);
    }

    // Revisão do PR #324: o solicitante do payload é conferido pelo banco contra a versão referenciada (o hash da
    // configuração gravado em versoes_de_agente) e contra o dono da raiz, com o tipo explícito e sem chave a mais. Cada
    // tentativa é uma linha nova por SQL direto, com payload coerente com as colunas e o hash recalculado; só o
    // solicitante muda.
    [PostgreSqlFact]
    public async Task SolicitanteDoPayloadConfereComAVersaoReferenciadaOuComODonoDaRaiz()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var idVersao = await ReservasDeWorktreePostgreSqlTests.VersaoAsync(provider);
        string hash;
        await using (var escopo = provider.CreateAsyncScope())
            hash = (await escopo.ServiceProvider.GetRequiredService<IVersaoDoAgenteRepository>().ObterPorIdAsync(idVersao))!
                .HashDaConfiguracao;
        var outroHash = new string('f', 64);
        Assert.NotEqual(hash, outroHash);
        var (execucao, _) = await CriarAsync(provider, "aprovacao:solicitante");
        var doAgente = await SalvarAsync(provider, AprovacaoDeAcao.Solicitar(
            Payload(execucao, solicitante: SolicitanteDaAcao.Agente(idVersao, hash)), execucao, execucao, null, Agora));
        var doDono = await SalvarAsync(provider, AprovacaoDeAcao.Solicitar(
            Payload(execucao, artefato: new string('c', 64)), execucao, execucao, null, Agora));
        var sequencia = 0;

        // Copia a linha-base com outra versão/hash nas colunas e outro objeto solicitante no payload (e um artefato
        // único, para não cair no índice de replay).
        string Copia(AprovacaoDeAcao baseDaCopia, string versao, string hashDaVersao, string solicitante)
        {
            var artefato = (++sequencia).ToString("x64");
            return $$"""
                INSERT INTO {{Tabela}} (id, id_execucao, id_execucao_raiz, id_etapa, acao, risco, escopo, alvo,
                    id_versao_do_solicitante, hash_da_versao_do_solicitante, canal_do_dono, id_do_dono, payload, hash, estado,
                    criada_em, expira_em)
                SELECT gen_random_uuid(), id_execucao, id_execucao_raiz, id_etapa, acao, risco, escopo, alvo, {{versao}},
                    {{hashDaVersao}}, canal_do_dono, id_do_dono, p.novo, encode(sha256(convert_to(p.novo, 'UTF8')), 'hex'), 0,
                    criada_em, expira_em
                FROM {{Tabela}}, LATERAL (SELECT jsonb_set(jsonb_set(payload::jsonb, '{parametros,artefato}',
                    to_jsonb('{{artefato}}'::text)), '{solicitante}', '{{solicitante}}'::jsonb)::text AS novo) p
                WHERE id = '{{baseDaCopia.Id}}'
                """;
        }
        var v = $"'{idVersao}'";
        string AgenteJson(string h, string tipo = "agente") => $$"""{"hash":"{{h}}","tipo":"{{tipo}}","versao":"{{idVersao}}"}""";
        string DonoJson(string tipo = "dono", long usuario = AprovacaoDeAcaoDominioTests.Dono, string canal = Canal,
            string extra = "") => $$"""{"canal":"{{canal}}","tipo":"{{tipo}}","usuario":{{usuario}}{{extra}}}""";

        var tentativas = new (string Caso, string Sql)[]
        {
            // Agente: versão existente com hash bem-formado que não é o dela (colunas e payload coerentes entre si).
            ("hash adulterado", Copia(doAgente, v, $"'{outroHash}'", AgenteJson(outroHash))),
            // Agente: versão preenchida, mas o payload diz dono (com ou sem os campos do dono) ou um tipo inventado.
            ("tipo dono com versão", Copia(doAgente, v, $"'{hash}'", $$"""{"hash":"{{hash}}","tipo":"dono","versao":"{{idVersao}}"}""")),
            ("objeto do dono com versão", Copia(doAgente, v, $"'{hash}'", DonoJson())),
            ("tipo inventado", Copia(doAgente, v, $"'{hash}'", AgenteJson(hash, "humano"))),
            ("agente com chave a mais", Copia(doAgente, v, $"'{hash}'",
                $$"""{"hash":"{{hash}}","tipo":"agente","usuario":1001,"versao":"{{idVersao}}"}""")),
            // Dono: tipo agente sem versão; chave de versão/hash a mais; hash na coluna sem versão; outro usuário ou canal.
            ("tipo agente sem versão", Copia(doDono, "NULL", "NULL", DonoJson(tipo: "agente"))),
            ("dono com versão no payload", Copia(doDono, "NULL", "NULL", DonoJson(extra: $",\"versao\":\"{idVersao}\""))),
            ("dono com hash no payload", Copia(doDono, "NULL", "NULL", DonoJson(extra: $",\"hash\":\"{hash}\""))),
            ("dono com hash na coluna", Copia(doDono, "NULL", $"'{hash}'", DonoJson())),
            ("dono com outro usuário", Copia(doDono, "NULL", "NULL", DonoJson(usuario: 999))),
            ("dono com outro canal", Copia(doDono, "NULL", "NULL", DonoJson(canal: "outro"))),
            ("dono sem tipo", Copia(doDono, "NULL", "NULL", $$"""{"canal":"{{Canal}}","usuario":{{AprovacaoDeAcaoDominioTests.Dono}}}"""))
        };
        // Todas as tentativas são feitas antes de afirmar, para a falha listar cada caso aceito.
        var aceitas = new List<string>();
        foreach (var (caso, sql) in tentativas)
        {
            var erro = await ErroAsync(banco, sql);
            if (erro is not (PostgresErrorCodes.RestrictViolation or PostgresErrorCodes.CheckViolation))
                aceitas.Add($"{caso} ({erro ?? "gravada"})");
        }
        Assert.True(aceitas.Count == 0, "Aceitas pelo banco: " + string.Join("; ", aceitas));
        Assert.Equal(2L, await Escalar(banco, $"SELECT count(*) FROM {Tabela}"));

        // As formas corretas, pelo mesmo caminho de SQL direto, são aceitas: a recusa acima é pelo solicitante.
        Assert.Null(await ErroAsync(banco, Copia(doAgente, v, $"'{hash}'", AgenteJson(hash))));
        Assert.Null(await ErroAsync(banco, Copia(doDono, "NULL", "NULL", DonoJson())));
        Assert.Equal(4L, await Escalar(banco, $"SELECT count(*) FROM {Tabela}"));
    }

    [PostgreSqlFact]
    public async Task MigrationDasAprovacoesEhReversivelSemDeixarFuncoes()
    {
        await using var banco = await FilaAsync();
        await using var contexto = new OrquestracaoDbContext(OrquestracaoDbContextFactory.Opcoes(banco.ConnectionString));
        var funcoes = "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'orquestracao'";
        var antes = await Escalar(banco, funcoes);
        await contexto.GetService<IMigrator>().MigrateAsync("20261120153457_SinteseDaCoordenacao");
        Assert.Equal(antes - 1, await Escalar(banco, funcoes));
        Assert.Equal(0L, await Escalar(banco, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'orquestracao' " +
            "AND table_name = 'aprovacoes_de_acao'"));
        await contexto.Database.MigrateAsync();
        Assert.Empty(await contexto.Database.GetPendingMigrationsAsync());
        Assert.Equal(antes, await Escalar(banco, funcoes));
        Assert.Equal(1L, await Escalar(banco, "SELECT count(*) FROM pg_trigger WHERE tgname = 'trg_aprovacoes_de_acao_protegidas'"));
    }

    // ---- apoio ----

    private static async Task<Execucao> RaizAsync(IServiceProvider provider) =>
        (await CriarAsync(provider, "raiz:" + Guid.NewGuid())).Execucao;

    private static Task<AprovacaoDeAcao> SolicitarAsync(IServiceProvider provider, Execucao raiz, string? artefato = null,
        TimeSpan? validade = null) =>
        SalvarAsync(provider, AprovacaoDeAcao.Solicitar(Payload(raiz, artefato), raiz, raiz, null, Agora, validade));

    private static async Task<AprovacaoDeAcao> SalvarAsync(IServiceProvider provider, AprovacaoDeAcao pedido)
    {
        await using var escopo = provider.CreateAsyncScope();
        await escopo.ServiceProvider.GetRequiredService<IAprovacaoDeAcaoRepository>().AdicionarAsync(pedido);
        await escopo.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
        return pedido;
    }

    private static async Task AlterarAsync(IServiceProvider provider, Guid id, Action<AprovacaoDeAcao> alteracao)
    {
        await using var escopo = provider.CreateAsyncScope();
        var pedido = (await escopo.ServiceProvider.GetRequiredService<IAprovacaoDeAcaoRepository>().ObterPorIdAsync(id))!;
        alteracao(pedido);
        await escopo.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
    }

    // As duas alterações leem a mesma versão da linha antes de qualquer uma gravar; depois gravam ao mesmo tempo.
    // Devolve, para cada uma, se gravou.
    private static async Task<bool[]> CorridaAsync(IServiceProvider provider, Guid id, params Action<AprovacaoDeAcao>[] alteracoes)
    {
        var lidas = new CountdownEvent(alteracoes.Length);
        var largada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tarefas = alteracoes.Select(alteracao => Task.Run(async () =>
        {
            await using var escopo = provider.CreateAsyncScope();
            var pedido = (await escopo.ServiceProvider.GetRequiredService<IAprovacaoDeAcaoRepository>().ObterPorIdAsync(id))!;
            lidas.Signal();
            await largada.Task;
            alteracao(pedido);
            try
            {
                await escopo.ServiceProvider.GetRequiredService<IUnitOfWorkDaOrquestracao>().SalvarAlteracoesAsync();
                return true;
            }
            catch (ConflitoDeConcorrenciaException)
            {
                return false;
            }
        })).ToArray();
        Assert.True(lidas.Wait(TimeSpan.FromSeconds(30)));
        largada.SetResult();
        return await Task.WhenAll(tarefas);
    }

    private static async Task<T> RepositorioAsync<T>(IServiceProvider provider, Func<IAprovacaoDeAcaoRepository, Task<T>> acao)
    {
        await using var escopo = provider.CreateAsyncScope();
        return await acao(escopo.ServiceProvider.GetRequiredService<IAprovacaoDeAcaoRepository>());
    }

    private static async Task<string?> ErroAsync(Banco banco, string sql)
    {
        try
        {
            await Executar(banco, sql);
            return null;
        }
        catch (PostgresException erro)
        {
            return erro.SqlState;
        }
    }
}
