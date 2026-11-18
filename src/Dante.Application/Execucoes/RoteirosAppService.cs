using System.Globalization;
using System.Text.Json;
using Dante.Application.Agentes;
using Dante.Application.Contextos;
using Dante.Application.Grupos;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Execucoes;
using Dante.Domain.Grupos;

namespace Dante.Application.Execucoes;

public sealed class RoteirosAppService(
    IFilaDeTrabalho fila,
    IExecucaoRepository execucoes,
    IEtapaDaExecucaoRepository etapas,
    IDependenciaDeEtapaRepository dependencias,
    IEventoDeOrquestracaoRepository eventos,
    IReservaDeWorktreeRepository reservas,
    IRoteiroDaExecucaoRepository roteiros,
    IParticipanteDaExecucaoRepository participantes,
    IPedidoDeAprovacaoRepository pedidos,
    IVersaoDoGrupoRepository versoesDeGrupo,
    IVersaoDoAgenteRepository versoesDeAgente,
    IFilaDeExecucoesAppService filaDeExecucoes,
    IVereditoRegistradoRepository vereditos,
    IUnitOfWorkDaOrquestracao unidade,
    Dante.Application.Mapeamento.IMapsterTypeAdapter mapper,
    IGerenciadorDeWorktrees gerenciador) : IRoteirosAppService
{
    public const string ObjetivoProtegido = "[conteúdo protegido]";

    public async Task<RoteiroCriadoDto> EnfileirarAsync(NovoRoteiroDto novo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(novo);
        ArgumentNullException.ThrowIfNull(novo.Grupo);
        ArgumentNullException.ThrowIfNull(novo.Contexto);
        if (string.IsNullOrWhiteSpace(novo.ChaveDeDeduplicacao) || novo.ChaveDeDeduplicacao.Length > TrabalhoDaFila.TamanhoMaximoDaChave)
            throw new ArgumentException("Chave de deduplicação inválida.");
        if (await fila.ObterPorChaveAsync(novo.ChaveDeDeduplicacao, cancellationToken) is { IdExecucao: { } idExistente })
        {
            var existente = await execucoes.ObterPorIdAsync(idExistente, cancellationToken) ??
                throw new InvalidOperationException("Execução do trabalho não registrada no ledger.");
            var roteiroExistente = await roteiros.ObterDaExecucaoAsync(existente.Id, cancellationToken);
            return new RoteiroCriadoDto(new ExecucaoNaFilaDto(existente.Numero, existente.Codigo, existente.Id, false),
                roteiroExistente?.ObjetivoAlteradoPelaRedacao ?? false);
        }
        var grupo = novo.Grupo;
        if (!grupo.Resolvido || grupo.Versao?.Configuracao is not { } configuracao)
            throw new InvalidOperationException(grupo.Erro ?? "O grupo não foi resolvido; nada foi criado.");
        ConferirContexto(grupo, configuracao, novo.Contexto);
        var objetivo = novo.Objetivo?.Trim() ?? "";
        if (objetivo.Length == 0) throw new ArgumentException("Informe a tarefa do grupo.");
        if (objetivo.Length > RoteiroDaExecucao.TamanhoMaximoDoObjetivo)
            throw new ArgumentException($"A tarefa passa de {RoteiroDaExecucao.TamanhoMaximoDoObjetivo} caracteres.");
        // Redação antes de qualquer gravação: o bruto nunca chega ao ledger (decisão B).
        var alterado = ProtecaoDeSegredos.ContemSegredo(objetivo);
        var redigido = alterado ? ObjetivoProtegido : objetivo;
        var agora = fila.Agora;
        var execucao = new Execucao(novo.Numero, TipoDeExecucao.Roteiro, novo.Canal, novo.IdDoUsuarioNoCanal,
            LedgerDeExecucoesAppService.SerializarContexto(novo.Contexto), Cortar(redigido), agora,
            idVersaoDoGrupo: grupo.Versao.Id);
        var snapshot = grupo.Participantes.Select(p => new ParticipanteDaExecucao(execucao, p.Papel, p.Handle,
            p.VersaoDoAgente.Id, p.VersaoDoAgente.Numero, p.VersaoDoAgente.HashDaConfiguracao, p.PodeEscrever,
            p.Responsabilidades)).ToArray();
        var roteiro = new RoteiroDaExecucao(execucao, grupo.Grupo!.Id, grupo.Grupo.Nome, grupo.Versao.Id, grupo.Versao.Numero,
            grupo.Versao.HashDaConfiguracao, configuracao.Roteiro, configuracao.Politica.RodadasDeRevisaoMaximas, snapshot,
            redigido, alterado, RoteiroDaExecucao.HashDoObjetivo(execucao.Id, objetivo));
        var inicio = ProgressaoDoRoteiro.Inicio();
        var primeira = Etapa(execucao, 1, inicio.Passo!.Value, inicio.Rodada, roteiro, snapshot, grupo, agora);
        await execucoes.AdicionarAsync(execucao, cancellationToken);
        foreach (var participante in snapshot) await participantes.AdicionarAsync(participante, cancellationToken);
        await roteiros.AdicionarAsync(roteiro, cancellationToken);
        await etapas.AdicionarAsync(primeira, cancellationToken);
        await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao, TipoDeEventoDeOrquestracao.ExecucaoCriada,
            Json(new()
            {
                ["tipo"] = execucao.Tipo.ToString(),
                ["grupo"] = roteiro.NomeDoGrupo,
                ["versao"] = Numero(roteiro.NumeroDaVersaoDoGrupo),
                ["snapshot"] = roteiro.HashDoSnapshot[..12],
                ["objetivoRedigido"] = alterado ? "sim" : "nao"
            }), agora), cancellationToken);
        await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(execucao, primeira, novo.ChaveDeDeduplicacao, agora),
            cancellationToken);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
        return new RoteiroCriadoDto(new ExecucaoNaFilaDto(execucao.Numero, execucao.Codigo, execucao.Id, true), alterado);
    }

    public async Task<RoteiroDaExecucaoDto?> ObterAsync(Guid idExecucao, CancellationToken cancellationToken = default)
    {
        var execucao = await execucoes.ObterPorIdAsync(idExecucao, cancellationToken);
        if (execucao is not { Tipo: TipoDeExecucao.Roteiro }) return null;
        var roteiro = await roteiros.ObterDaExecucaoAsync(idExecucao, cancellationToken);
        if (roteiro is null) return null;
        var lista = await etapas.ListarDaExecucaoAsync(idExecucao, cancellationToken);
        using var contexto = JsonDocument.Parse(execucao.Contexto);
        return new RoteiroDaExecucaoDto
        {
            IdExecucao = execucao.Id,
            Codigo = execucao.Codigo,
            Estado = execucao.Estado,
            AliasDoRepositorio = contexto.RootElement.TryGetProperty("alias", out var alias) ? alias.GetString() ?? "" : "",
            CheckoutDoRepositorio = contexto.RootElement.TryGetProperty("diretorio", out var diretorio)
                ? diretorio.GetString() ?? "" : "",
            Canal = execucao.Canal,
            IdDoUsuarioNoCanal = execucao.IdDoUsuarioNoCanal,
            NomeDoGrupo = roteiro.NomeDoGrupo,
            NumeroDaVersaoDoGrupo = roteiro.NumeroDaVersaoDoGrupo,
            HashDaVersaoDoGrupo = roteiro.HashDaVersaoDoGrupo,
            HashDoSnapshot = roteiro.HashDoSnapshot,
            Passos = roteiro.Passos,
            RodadasDeRevisaoMaximas = roteiro.RodadasDeRevisaoMaximas,
            ObjetivoRedigido = roteiro.ObjetivoRedigido,
            ObjetivoAlteradoPelaRedacao = roteiro.ObjetivoAlteradoPelaRedacao,
            HashDoObjetivoOriginal = roteiro.HashDoObjetivoOriginal,
            Participantes = (await participantes.ListarDaExecucaoAsync(idExecucao, cancellationToken))
                .Select(p => new ParticipanteDaExecucaoDto(p.Papel, p.HandleDoAgente, p.IdVersaoDoAgente,
                    p.NumeroDaVersaoDoAgente, p.HashDaConfiguracaoDoAgente, p.PodeEscrever, p.Responsabilidades)).ToArray(),
            Etapas = lista.Where(e => e.Passo is not null).OrderBy(e => e.Numero)
                .Select(e => new PassoExecutadoDto(e.Id, e.Numero, e.Passo!.Value, e.Rodada!.Value, e.Papel, e.Estado,
                    e.Tentativa)).ToArray(),
            Pedidos = (await pedidos.ListarDaExecucaoAsync(idExecucao, cancellationToken)).Select(Dto).ToArray()
        };
    }

    public async Task<RoteiroDaExecucaoDto?> ObterDoProprietarioAsync(ProprietarioDaExecucao proprietario, string codigo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proprietario);
        return await LocalizarAsync(proprietario, codigo, cancellationToken) is { } execucao
            ? await ObterAsync(execucao.Id, cancellationToken) : null;
    }

    public async Task<string?> ConferirIntegridadeAsync(Guid idExecucao, CancellationToken cancellationToken = default)
    {
        var roteiro = await roteiros.ObterDaExecucaoAsync(idExecucao, cancellationToken);
        if (roteiro is null) return "O roteiro da execução não está registrado.";
        var linhas = await participantes.ListarDaExecucaoAsync(idExecucao, cancellationToken);
        if (linhas.Count == 0 || RoteiroDaExecucao.CalcularHashDoSnapshot(roteiro.HashDaVersaoDoGrupo, linhas) != roteiro.HashDoSnapshot)
            return "O snapshot do grupo não confere com o gravado na criação; nada foi executado.";
        var versao = await versoesDeGrupo.ObterComParticipantesAsync(roteiro.IdVersaoDoGrupo, cancellationToken);
        if (versao is null || !versao.Integra || versao.HashDaConfiguracao != roteiro.HashDaVersaoDoGrupo)
            return $"A versão {roteiro.NumeroDaVersaoDoGrupo} do grupo {roteiro.NomeDoGrupo} não confere com o hash congelado.";
        foreach (var participante in linhas)
        {
            var agente = await versoesDeAgente.ObterPorIdAsync(participante.IdVersaoDoAgente, cancellationToken);
            if (agente is null || !agente.Integra || agente.HashDaConfiguracao != participante.HashDaConfiguracaoDoAgente)
                return $"A versão do agente {participante.HandleDoAgente} (papel {participante.Papel}) não confere com o hash congelado.";
        }
        return null;
    }

    public async Task<AvancoDoRoteiroDto> ConcluirPassoAsync(PosseDoTrabalho posse, Guid idEtapa,
        ConclusaoDoPassoDto conclusao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conclusao);
        var resultado = conclusao.Resultado;
        // Falha e cancelamento: a conclusão comum da fila encerra a execução e libera as reservas.
        if (resultado.EstadoFinal != EstadoDaEtapa.Concluida) return await Comum(posse, idEtapa, resultado, cancellationToken);
        AvancoDoRoteiroDto? avanco = null;
        var cancelar = false;
        await fila.GravarComPosseAsync(posse, async token =>
        {
            var etapa = await etapas.ObterPorIdAsync(idEtapa, token) ??
                throw new InvalidOperationException("Etapa não registrada no ledger.");
            var execucao = await execucoes.ObterPorIdAsync(etapa.IdExecucao, token) ??
                throw new InvalidOperationException("Execução não registrada no ledger.");
            if (execucao.Tipo != TipoDeExecucao.Roteiro || etapa.Passo is not { } passo || etapa.Rodada is not { } rodada ||
                passo == TipoDePassoDoRoteiro.AprovacaoHumana)
                throw new InvalidOperationException("Só um passo de agente de um Roteiro é concluído aqui.");
            if (etapa.Estado != EstadoDaEtapa.EmExecucao)
                throw new TransicaoInvalidaException("etapa", etapa.Estado.ToString(), EstadoDaEtapa.Concluida.ToString());
            // Pedido do dono registrado antes da conclusão (#305): a conclusão comum grava Cancelada.
            if ((await eventos.ConsultarDoTipoAsync([execucao.Id], TipoDeEventoDeOrquestracao.CancelamentoSolicitado,
                    token)).Count > 0)
            {
                cancelar = true;
                return;
            }
            if ((passo == TipoDePassoDoRoteiro.Revisar) != (conclusao.Veredito is not null))
                throw new ArgumentException("A revisão, e só ela, termina com um veredito estruturado.");
            if (passo == TipoDePassoDoRoteiro.Revisar && !HashValido(conclusao.HashDoArtefato))
                throw new ArgumentException("A revisão precisa do hash do artefato revisado.");
            var roteiro = await roteiros.ObterDaExecucaoAsync(execucao.Id, token) ??
                throw new InvalidOperationException("O roteiro da execução não está registrado.");
            var agora = fila.Agora;
            var etapaAntes = etapa.Estado;
            etapa.Concluir(resultado.CodigoDeSaida, agora);
            await Transicao(execucao, etapa, etapaAntes, etapa.Estado, agora, token);
            var proximo = ProgressaoDoRoteiro.AposPasso(passo, rodada, conclusao.Veredito, roteiro.TemCorrecao,
                roteiro.RodadasDeRevisaoMaximas);
            var execucaoAntes = execucao.Estado;
            if (proximo.Bloqueia)
            {
                execucao.Bloquear(proximo.MotivoDoBloqueio!);
                await Transicao(execucao, null, execucaoAntes, execucao.Estado, agora, token);
                avanco = new(execucao.Estado, etapa.Estado, Motivo: proximo.MotivoDoBloqueio);
            }
            else if (proximo.Passo == TipoDePassoDoRoteiro.AprovacaoHumana)
            {
                // O gate só abre para o conteúdo presente: o diff revisado tem de ser o da worktree agora.
                if (await ArtefatoAtualAsync(execucao.Id, token) != conclusao.HashDoArtefato)
                    throw new InvalidOperationException(
                        "O diff revisado não é o conteúdo atual da worktree; o gate não foi aberto.");
                var aprovacao = await NovaEtapaAsync(execucao, etapa, TipoDePassoDoRoteiro.AprovacaoHumana, proximo.Rodada,
                    roteiro, agora, token);
                aprovacao.TornarPronta([etapa]);
                aprovacao.Iniciar(agora);
                aprovacao.AguardarAprovacao();
                await Transicao(execucao, aprovacao, EstadoDaEtapa.Pendente, aprovacao.Estado, agora, token);
                var pedido = new PedidoDeAprovacao(execucao, aprovacao, proximo.Rodada, roteiro.HashDoSnapshot,
                    conclusao.HashDoArtefato!, agora);
                await pedidos.AdicionarAsync(pedido, token);
                // Decisão C: o slot de escrita fica livre, e a worktree continua da execução (não é limpa nem
                // reutilizada) até a decisão.
                foreach (var reserva in (await reservas.ListarDaExecucaoAsync(execucao.Id, token))
                             .Where(r => r.Estado == EstadoDaReservaDeWorktree.Pronta))
                {
                    reserva.AguardarDecisao();
                    await eventos.AdicionarAsync(LiberacaoDeWorktrees.Evento(execucao, reserva,
                        TipoDeEventoDeOrquestracao.WorktreeAguardandoDecisao, agora), token);
                }
                execucao.AguardarAprovacao($"Aguardando sua aprovação do resultado da rodada {proximo.Rodada}.");
                await Transicao(execucao, null, execucaoAntes, execucao.Estado, agora, token);
                await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao,
                    TipoDeEventoDeOrquestracao.AprovacaoSolicitada, Json(new()
                    {
                        ["rodada"] = Numero(proximo.Rodada),
                        ["artefato"] = pedido.HashDoArtefato[..12],
                        ["snapshot"] = pedido.HashDoSnapshot[..12]
                    }), agora, aprovacao), token);
                avanco = new(execucao.Estado, etapa.Estado, TipoDePassoDoRoteiro.AprovacaoHumana, proximo.Rodada,
                    pedido.HashDoArtefato);
            }
            else
            {
                var seguinte = await NovaEtapaAsync(execucao, etapa, proximo.Passo!.Value, proximo.Rodada, roteiro, agora, token);
                await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(execucao, seguinte, Chave(execucao, seguinte), agora),
                    token);
                avanco = new(execucao.Estado, etapa.Estado, proximo.Passo, proximo.Rodada);
            }
            await unidade.SalvarAlteracoesAsync(token);
        }, null, cancellationToken: cancellationToken);
        if (cancelar) return await Comum(posse, idEtapa, resultado, cancellationToken);
        // O trabalho termina numa segunda gravação sob posse só depois de o avanço estar durável.
        await fila.GravarComPosseAsync(posse, null, EstadoDoTrabalho.Concluido, cancellationToken: cancellationToken);
        return avanco!;
    }

    public Task SolicitarObjetivoAsync(PosseDoTrabalho posse, Guid idEtapa, CancellationToken cancellationToken = default) =>
        fila.GravarComPosseAsync(posse, async token =>
        {
            var etapa = await etapas.ObterPorIdAsync(idEtapa, token) ??
                throw new InvalidOperationException("Etapa não registrada no ledger.");
            var execucao = await execucoes.ObterPorIdAsync(etapa.IdExecucao, token) ??
                throw new InvalidOperationException("Execução não registrada no ledger.");
            var roteiro = await roteiros.ObterDaExecucaoAsync(execucao.Id, token);
            if (execucao.Tipo != TipoDeExecucao.Roteiro || roteiro is not { ObjetivoAlteradoPelaRedacao: true })
                throw new InvalidOperationException("Só um roteiro cujo objetivo foi alterado pela redação pede outro.");
            if (etapa.Estado is not (EstadoDaEtapa.Pendente or EstadoDaEtapa.Pronta))
                throw new InvalidOperationException("O objetivo é pedido antes de a etapa começar.");
            var agora = fila.Agora;
            var antes = execucao.Estado;
            if (execucao.Estado == EstadoDaExecucao.Pendente)
            {
                execucao.Iniciar(agora);
                await Transicao(execucao, null, antes, execucao.Estado, agora, token);
                antes = execucao.Estado;
            }
            execucao.AguardarEntrada("O objetivo guardado foi protegido pela redação; envie a tarefa de novo, sem " +
                "credenciais, para continuar.");
            await Transicao(execucao, null, antes, execucao.Estado, agora, token);
            await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao, TipoDeEventoDeOrquestracao.EntradaSolicitada,
                Json(new() { ["entrada"] = "objetivo" }), agora, etapa), token);
            await unidade.SalvarAlteracoesAsync(token);
        }, EstadoDoTrabalho.Concluido, cancellationToken: cancellationToken);

    public async Task<ResultadoDaDecisaoDto> DecidirAsync(ProprietarioDaExecucao proprietario, string codigo,
        DecisaoHumanaDto decisao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proprietario);
        ArgumentNullException.ThrowIfNull(decisao);
        var encontrada = await LocalizarAsync(proprietario, codigo, cancellationToken);
        if (encontrada is null) return new(ResultadoDaDecisao.NaoEncontrada);
        // Integridade antes de qualquer mudança: uma decisão nunca vale para um snapshot adulterado.
        if (await ConferirIntegridadeAsync(encontrada.Id, cancellationToken) is { } recusa)
            throw new InvalidOperationException(recusa);
        var instrucao = decisao.Texto?.Trim();
        if (decisao.Tipo == TipoDeDecisaoHumana.Corrigir)
        {
            if (string.IsNullOrWhiteSpace(instrucao)) throw new ArgumentException("Diga o que corrigir.");
            // A instrução vai ao corretor e ao ledger: nunca com credencial.
            if (ProtecaoDeSegredos.ContemSegredo(instrucao))
                throw new ArgumentException("A instrução parece conter uma credencial; reescreva sem ela.");
        }
        ResultadoDaDecisaoDto resultado = new(ResultadoDaDecisao.SemPedidoPendente, encontrada.Codigo);
        try
        {
            await fila.ExecutarEmTransacaoAsync(async token =>
            {
                // Mesma ordem das gravações sob posse: trabalhos (nenhum ativo num gate), depois a execução, travados.
                if ((await fila.TravarAtivosDaExecucaoAsync(encontrada.Id, token)).Count > 0) return;
                var execucao = await execucoes.ObterParaAtualizarAsync(encontrada.Id, token) ??
                    throw new InvalidOperationException("Execução não registrada no ledger.");
                if (execucao.Estado != EstadoDaExecucao.AguardandoAprovacao) return;
                var pedido = await pedidos.ObterPendenteAsync(execucao.Id, token);
                if (pedido is null) return;
                var roteiro = await roteiros.ObterDaExecucaoAsync(execucao.Id, token) ??
                    throw new InvalidOperationException("O roteiro da execução não está registrado.");
                if (pedido.HashDoSnapshot != roteiro.HashDoSnapshot)
                    throw new InvalidOperationException("O pedido de aprovação não é do snapshot desta execução.");
                var aprovacao = await etapas.ObterPorIdAsync(pedido.IdEtapa, token) ??
                    throw new InvalidOperationException("Etapa de aprovação não registrada.");
                // O artefato decidido é o da worktree agora, não o hash digitado: aprovar ou corrigir só vale se o conteúdo
                // presente ainda for exatamente o revisado. Recusar não aceita nada e não depende do conteúdo.
                var vinculaArtefato = decisao.Tipo != TipoDeDecisaoHumana.Recusar;
                var worktree = vinculaArtefato ? await WorktreeDoArtefatoAsync(execucao.Id, token) : null;
                if (worktree is not null && await HashAtualAsync(worktree, token) is var antes && antes != pedido.HashDoArtefato)
                {
                    resultado = new(ResultadoDaDecisao.ArtefatoDivergente, execucao.Codigo, pedido.Rodada, antes);
                    return;
                }
                var agora = fila.Agora;
                var execucaoAntes = execucao.Estado;
                var etapaAntes = aprovacao.Estado;
                switch (decisao.Tipo)
                {
                    case TipoDeDecisaoHumana.Aprovar:
                        pedido.Aprovar(proprietario.Canal, proprietario.IdDoUsuario, decisao.HashConfirmado, agora);
                        aprovacao.Continuar();
                        aprovacao.Concluir(null, agora);
                        execucao.Retomar();
                        execucao.Iniciar(agora);
                        execucao.Concluir(agora);
                        resultado = new(ResultadoDaDecisao.Aprovada, execucao.Codigo, pedido.Rodada);
                        break;
                    case TipoDeDecisaoHumana.Corrigir:
                    {
                        var proximo = ProgressaoDoRoteiro.CorrecaoPedidaNoGate(pedido.Rodada, roteiro.TemCorrecao,
                            roteiro.RodadasDeRevisaoMaximas);
                        // Sem rodada disponível, o gate continua aberto: aprovar ou recusar.
                        if (proximo.Bloqueia) throw new InvalidOperationException(proximo.MotivoDoBloqueio + " Aprove ou recuse.");
                        pedido.SolicitarCorrecao(proprietario.Canal, proprietario.IdDoUsuario, decisao.HashConfirmado,
                            instrucao!, agora);
                        aprovacao.Continuar();
                        aprovacao.Concluir(null, agora);
                        execucao.Retomar();
                        var corrigir = await NovaEtapaAsync(execucao, aprovacao, proximo.Passo!.Value, proximo.Rodada, roteiro,
                            agora, token);
                        // Nova tentativa de escrita: o slot é readquirido ao reservar, sob a trava de capacidade, sem contar
                        // tentativa enquanto não houver capacidade (decisão C).
                        await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(execucao, corrigir, Chave(execucao, corrigir),
                            agora), token);
                        resultado = new(ResultadoDaDecisao.CorrecaoAgendada, execucao.Codigo, proximo.Rodada);
                        break;
                    }
                    default:
                        pedido.Recusar(proprietario.Canal, proprietario.IdDoUsuario, PedidoDeCancelamento.MotivoSeguro(instrucao),
                            agora);
                        aprovacao.Cancelar("recusado na aprovação humana", null, agora);
                        execucao.Cancelar("Resultado recusado na aprovação humana.", agora);
                        resultado = new(ResultadoDaDecisao.Recusada, execucao.Codigo, pedido.Rodada);
                        break;
                }
                await Transicao(execucao, aprovacao, etapaAntes, aprovacao.Estado, agora, token);
                await Transicao(execucao, null, execucaoAntes, execucao.Estado, agora, token);
                await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao, TipoDeEventoDeOrquestracao.SolicitacaoResolvida,
                    Json(new()
                    {
                        ["decisao"] = pedido.Estado.ToString(),
                        ["autor"] = proprietario.Autor,
                        ["artefato"] = pedido.HashDoArtefato[..12],
                        ["rodada"] = Numero(pedido.Rodada)
                    }), agora, aprovacao), token);
                // Aprovado ou recusado: terminal, as reservas deixam de ser ativas; a limpeza preserva a worktree alterada.
                await LiberacaoDeWorktrees.LiberarSeEncerradaAsync(reservas, eventos, execucao, agora, token);
                await unidade.SalvarAlteracoesAsync(token);
                // Fecha a janela: a decisão já está gravada na transação, ainda sem commit. O conteúdo é conferido de novo; se
                // mudou desde a primeira conferência, a transação inteira é desfeita e o gate continua pendente.
                if (worktree is not null && await HashAtualAsync(worktree, token) is var depois && depois != pedido.HashDoArtefato)
                    throw new ArtefatoAlteradoDuranteADecisaoException(depois);
            }, cancellationToken);
        }
        catch (ArtefatoAlteradoDuranteADecisaoException divergencia)
        {
            // Nada foi gravado: o gate continua pendente e pede uma nova revisão do conteúdo atual.
            resultado = new(ResultadoDaDecisao.ArtefatoDivergente, encontrada.Codigo, resultado.Rodada, divergencia.HashAtual);
        }
        return resultado;
    }

    // Hash canônico do conteúdo atual da worktree principal reservada (o mesmo cálculo do revisor). Sem worktree
    // reservada ou ilegível: recusa, e nada é decidido.
    private async Task<string> ArtefatoAtualAsync(Guid idExecucao, CancellationToken cancellationToken) =>
        await HashAtualAsync(await WorktreeDoArtefatoAsync(idExecucao, cancellationToken), cancellationToken);

    // A worktree principal reservada da execução, lida uma vez: as conferências antes e depois de gravar a decisão usam
    // o mesmo diretório, mesmo que a decisão libere a reserva na mesma transação.
    private async Task<WorktreePlanejada> WorktreeDoArtefatoAsync(Guid idExecucao, CancellationToken cancellationToken)
    {
        var principal = (await reservas.ListarDaExecucaoAsync(idExecucao, cancellationToken))
            .SingleOrDefault(r => r.Principal && r.Ativa) ??
            throw new InvalidOperationException("A worktree do resultado não está reservada; nada foi decidido.");
        return WorktreeDaExecucaoDto.De(principal).Planejada;
    }

    private async Task<string> HashAtualAsync(WorktreePlanejada worktree, CancellationToken cancellationToken)
    {
        try
        {
            return (await gerenciador.ResumirAlteracoesAsync(worktree, 0, cancellationToken)).Hash;
        }
        catch (WorktreeInvalidaException exception)
        {
            throw new InvalidOperationException($"A worktree do resultado não pôde ser conferida ({exception.Message}); " +
                "nada foi decidido.");
        }
    }

    private sealed class ArtefatoAlteradoDuranteADecisaoException(string hashAtual)
        : Exception("O conteúdo da worktree mudou durante a decisão.")
    {
        public string HashAtual { get; } = hashAtual;
    }

    public async Task<ResultadoDaDecisaoDto> RevisarNovamenteAsync(ProprietarioDaExecucao proprietario, string codigo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proprietario);
        var encontrada = await LocalizarAsync(proprietario, codigo, cancellationToken);
        if (encontrada is null) return new(ResultadoDaDecisao.NaoEncontrada);
        if (await ConferirIntegridadeAsync(encontrada.Id, cancellationToken) is { } recusa)
            throw new InvalidOperationException(recusa);
        ResultadoDaDecisaoDto resultado = new(ResultadoDaDecisao.SemPedidoPendente, encontrada.Codigo);
        await fila.ExecutarEmTransacaoAsync(async token =>
        {
            if ((await fila.TravarAtivosDaExecucaoAsync(encontrada.Id, token)).Count > 0) return;
            var execucao = await execucoes.ObterParaAtualizarAsync(encontrada.Id, token) ??
                throw new InvalidOperationException("Execução não registrada no ledger.");
            if (execucao.Estado != EstadoDaExecucao.AguardandoAprovacao) return;
            var pedido = await pedidos.ObterPendenteAsync(execucao.Id, token);
            if (pedido is null) return;
            var roteiro = await roteiros.ObterDaExecucaoAsync(execucao.Id, token) ??
                throw new InvalidOperationException("O roteiro da execução não está registrado.");
            var aprovacao = await etapas.ObterPorIdAsync(pedido.IdEtapa, token) ??
                throw new InvalidOperationException("Etapa de aprovação não registrada.");
            var revisada = (await etapas.ListarDaExecucaoAsync(execucao.Id, token))
                .Where(e => e.Passo == TipoDePassoDoRoteiro.Revisar && e.Estado == EstadoDaEtapa.Concluida)
                .MaxBy(e => e.Numero) ?? throw new InvalidOperationException("Nenhuma revisão concluída a refazer.");
            // Só com o conteúdo de fato alterado: o pedido em vigor continua valendo para o artefato revisado.
            var atual = await ArtefatoAtualAsync(execucao.Id, token);
            var agora = fila.Agora;
            pedido.Substituir(proprietario.Canal, proprietario.IdDoUsuario, atual, agora);
            var etapaAntes = aprovacao.Estado;
            aprovacao.Cancelar("artefato alterado depois da revisão; nova revisão pedida", null, agora);
            await Transicao(execucao, aprovacao, etapaAntes, aprovacao.Estado, agora, token);
            var execucaoAntes = execucao.Estado;
            execucao.Retomar();
            await Transicao(execucao, null, execucaoAntes, execucao.Estado, agora, token);
            // A mesma rodada: rever o conteúdo atual não consome rodada de correção. A reserva continua aguardando a
            // decisão (a revisão só lê).
            var revisar = await NovaEtapaAsync(execucao, revisada, TipoDePassoDoRoteiro.Revisar, pedido.Rodada, roteiro,
                agora, token);
            await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(execucao, revisar, Chave(execucao, revisar), agora), token);
            await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao, TipoDeEventoDeOrquestracao.SolicitacaoResolvida,
                Json(new()
                {
                    ["decisao"] = pedido.Estado.ToString(),
                    ["autor"] = proprietario.Autor,
                    ["artefato"] = pedido.HashDoArtefato[..12],
                    ["atual"] = atual[..12],
                    ["rodada"] = Numero(pedido.Rodada)
                }), agora, aprovacao), token);
            await unidade.SalvarAlteracoesAsync(token);
            resultado = new(ResultadoDaDecisao.RevisaoAgendada, execucao.Codigo, pedido.Rodada, atual);
        }, cancellationToken);
        return resultado;
    }

    public async Task<RegistroDeVereditoDto> RegistrarVereditoAsync(EscopoDaRevisaoDto escopo, VereditoInformadoDto veredito,
        string? sessao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(escopo);
        ArgumentNullException.ThrowIfNull(veredito);
        var itens = veredito.Itens ?? [];
        // O que o revisor escreve vai ao ledger e à pessoa: nunca com credencial.
        if (ProtecaoDeSegredos.ContemSegredo(veredito.Resumo) || itens.Any(ProtecaoDeSegredos.ContemSegredo))
            throw new ArgumentException("O veredito parece conter uma credencial; descreva o problema sem ela.");
        var (execucao, etapa) = await EtapaDaRevisaoAsync(escopo, cancellationToken);
        var novo = new VereditoRegistrado(execucao, etapa, escopo.HashDoArtefato, veredito.Veredito, veredito.Resumo, itens,
            sessao, fila.Agora);
        if (await vereditos.ObterDaEtapaAsync(etapa.Id, cancellationToken) is { } existente)
            return Repetido(existente, novo);
        try
        {
            await vereditos.AdicionarAsync(novo, cancellationToken);
            await unidade.SalvarAlteracoesAsync(cancellationToken);
            return new RegistroDeVereditoDto(novo.Veredito, false);
        }
        catch (Dante.Application.Comum.ConflitoDeConcorrenciaException)
        {
            // Duas entregas simultâneas: o índice único deixou passar uma; esta só vale se for a mesma.
            var gravado = await vereditos.ObterDaEtapaAsync(etapa.Id, cancellationToken) ??
                throw new InvalidOperationException("O veredito não pôde ser registrado; tente de novo.");
            return Repetido(gravado, novo);
        }
    }

    public async Task<Dante.Application.Agentes.VersaoDoAgenteDto> ObterVersaoDoParticipanteAsync(Guid idExecucao,
        string papel, CancellationToken cancellationToken = default)
    {
        var participante = (await participantes.ListarDaExecucaoAsync(idExecucao, cancellationToken))
            .SingleOrDefault(p => p.Papel == papel) ??
            throw new InvalidOperationException($"O papel {papel} não está no snapshot da execução.");
        var versao = await versoesDeAgente.ObterPorIdAsync(participante.IdVersaoDoAgente, cancellationToken);
        if (versao is null || !versao.Integra || versao.HashDaConfiguracao != participante.HashDaConfiguracaoDoAgente)
            throw new InvalidOperationException(
                $"A versão do agente {participante.HandleDoAgente} (papel {papel}) não confere com o hash congelado.");
        return mapper.Mapear<Dante.Domain.Agentes.VersaoDoAgente, Dante.Application.Agentes.VersaoDoAgenteDto>(versao);
    }

    public Task<VereditoRegistrado?> ObterVereditoAsync(Guid idEtapa, CancellationToken cancellationToken = default) =>
        vereditos.ObterDaEtapaAsync(idEtapa, cancellationToken);

    public async Task<EstadoDaRevisaoDto> ObterEstadoDaRevisaoAsync(EscopoDaRevisaoDto escopo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(escopo);
        var (execucao, etapa) = await EtapaDaRevisaoAsync(escopo, cancellationToken);
        var roteiro = await roteiros.ObterDaExecucaoAsync(execucao.Id, cancellationToken) ??
            throw new InvalidOperationException("O roteiro da execução não está registrado.");
        var anteriores = (await vereditos.ListarDaExecucaoAsync(execucao.Id, cancellationToken))
            .Where(v => v.IdEtapa != etapa.Id).OrderBy(v => v.Rodada)
            .Select(v => new VereditoAnteriorDto(v.Rodada, v.Veredito, v.Resumo, v.Itens)).ToArray();
        var instrucao = (await pedidos.ListarDaExecucaoAsync(execucao.Id, cancellationToken))
            .LastOrDefault(p => p.Estado == EstadoDoPedidoDeAprovacao.CorrecaoSolicitada && p.Rodada + 1 == escopo.Rodada)?.Motivo;
        return new EstadoDaRevisaoDto(execucao.Codigo, roteiro.NomeDoGrupo, escopo.Rodada, roteiro.RodadasDeRevisaoMaximas,
            escopo.HashDoArtefato, anteriores, instrucao);
    }

    // A etapa do escopo, conferida: revisão em curso, da execução, na rodada do escopo, de um Roteiro não encerrado.
    private async Task<(Execucao Execucao, EtapaDaExecucao Etapa)> EtapaDaRevisaoAsync(EscopoDaRevisaoDto escopo,
        CancellationToken cancellationToken)
    {
        var etapa = await etapas.ObterPorIdAsync(escopo.IdEtapa, cancellationToken);
        var execucao = etapa is null ? null : await execucoes.ObterPorIdAsync(etapa.IdExecucao, cancellationToken);
        if (etapa is null || execucao is null || execucao.Id != escopo.IdExecucao || execucao.Tipo != TipoDeExecucao.Roteiro ||
            execucao.Terminal || etapa.Passo != TipoDePassoDoRoteiro.Revisar || etapa.Rodada != escopo.Rodada ||
            etapa.Estado != EstadoDaEtapa.EmExecucao || !HashValido(escopo.HashDoArtefato))
            throw new InvalidOperationException("Fora do escopo desta revisão: nada foi registrado.");
        return (execucao, etapa);
    }

    private static RegistroDeVereditoDto Repetido(VereditoRegistrado gravado, VereditoRegistrado novo) =>
        gravado.HashDoConteudo == novo.HashDoConteudo && gravado.HashDoArtefato == novo.HashDoArtefato
            ? new RegistroDeVereditoDto(gravado.Veredito, true)
            : throw new InvalidOperationException("O veredito desta revisão já foi registrado e não muda.");

    public async Task<bool> SubstituirObjetivoAsync(ProprietarioDaExecucao proprietario, string codigo, string objetivo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proprietario);
        var texto = objetivo?.Trim() ?? "";
        if (texto.Length == 0) throw new ArgumentException("Informe a tarefa de novo.");
        if (texto.Length > RoteiroDaExecucao.TamanhoMaximoDoObjetivo)
            throw new ArgumentException($"A tarefa passa de {RoteiroDaExecucao.TamanhoMaximoDoObjetivo} caracteres.");
        if (ProtecaoDeSegredos.ContemSegredo(texto))
            throw new ArgumentException("A tarefa ainda parece conter uma credencial; reescreva sem ela.");
        var encontrada = await LocalizarAsync(proprietario, codigo, cancellationToken);
        if (encontrada is null) return false;
        var substituido = false;
        await fila.ExecutarEmTransacaoAsync(async token =>
        {
            if ((await fila.TravarAtivosDaExecucaoAsync(encontrada.Id, token)).Count > 0) return;
            var execucao = await execucoes.ObterParaAtualizarAsync(encontrada.Id, token) ??
                throw new InvalidOperationException("Execução não registrada no ledger.");
            var roteiro = await roteiros.ObterDaExecucaoAsync(execucao.Id, token);
            if (execucao.Estado != EstadoDaExecucao.AguardandoEntrada || roteiro is not { ObjetivoAlteradoPelaRedacao: true })
                return;
            var proxima = (await etapas.ListarDaExecucaoAsync(execucao.Id, token)).FirstOrDefault(e => !e.Terminal) ??
                throw new InvalidOperationException("Nenhuma etapa a continuar.");
            var agora = fila.Agora;
            roteiro.SubstituirObjetivo(texto, RoteiroDaExecucao.HashDoObjetivo(execucao.Id, texto));
            roteiros.Atualizar(roteiro);
            var antes = execucao.Estado;
            execucao.Retomar();
            await Transicao(execucao, null, antes, execucao.Estado, agora, token);
            await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao, TipoDeEventoDeOrquestracao.SolicitacaoResolvida,
                Json(new() { ["entrada"] = "objetivo", ["autor"] = proprietario.Autor }), agora), token);
            await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(execucao, proxima,
                $"roteiro:{execucao.Id:N}:objetivo:{roteiro.HashDoObjetivoOriginal[..16]}", agora), token);
            await unidade.SalvarAlteracoesAsync(token);
            substituido = true;
        }, cancellationToken);
        return substituido;
    }

    // Repositórios: modo repositório (implementar escreve numa worktree), dentro da política do grupo e das concessões
    // de cada participante. Nada é inferido do texto da tarefa.
    private static void ConferirContexto(ResolucaoDeGrupoDto grupo, ConfiguracaoDoGrupo configuracao, JobExecutionContext contexto)
    {
        if (contexto.Mode != JobExecutionMode.Repository || contexto.RepositoryAlias is null)
            throw new ArgumentException("A tarefa de um grupo escreve numa worktree: informe o repositório (@alias).");
        // A revisão confinada lê uma worktree só; repositórios vinculados ficam para depois.
        if (contexto.RepositoriosVinculados.Count > 0)
            throw new ArgumentException("Nesta versão, o roteiro de um grupo trabalha num repositório só; " +
                $"{contexto.RepositoryAlias} tem vínculos (AD-58).");
        var aliases = contexto.RepositoriosVinculados.Select(v => v.Alias).Prepend(contexto.RepositoryAlias)
            .Select(a => a.ToLowerInvariant()).ToArray();
        var permitidos = configuracao.Politica.RepositoriosPermitidos!;
        if (permitidos.Count > 0 && aliases.FirstOrDefault(a => !permitidos.Contains(a)) is { } fora)
            throw new ArgumentException($"O grupo {grupo.Nome} não trabalha em {fora}. Repositórios do grupo: " +
                $"{string.Join(", ", permitidos)}.");
        foreach (var participante in grupo.Participantes)
            if (!MontagemDaTarefa.ReposPermitidos(participante.VersaoDoAgente.Configuracao, contexto))
                throw new ArgumentException(MontagemDaTarefa.RecusaDeRepositorio(participante.Handle,
                    participante.VersaoDoAgente.Configuracao, contexto));
    }

    private static EtapaDaExecucao Etapa(Execucao execucao, int numero, TipoDePassoDoRoteiro passo, int rodada,
        RoteiroDaExecucao roteiro, IReadOnlyList<ParticipanteDaExecucao> snapshot, ResolucaoDeGrupoDto grupo,
        DateTimeOffset agora)
    {
        var papel = roteiro.Passos.First(p => p.Tipo == passo).Papel!;
        var participante = snapshot.Single(p => p.Papel == papel);
        var configuracao = grupo.Participantes.Single(p => p.Papel == papel).VersaoDoAgente.Configuracao;
        return EtapaDaExecucao.DoRoteiro(execucao, numero, passo, rodada, agora, papel, participante.IdVersaoDoAgente,
            configuracao.Driver, configuracao.Modelo, configuracao.Esforco);
    }

    // Próxima etapa do roteiro, numerada depois de todas as existentes e dependente da anterior (ordem topológica).
    private async Task<EtapaDaExecucao> NovaEtapaAsync(Execucao execucao, EtapaDaExecucao anterior,
        TipoDePassoDoRoteiro passo, int rodada, RoteiroDaExecucao roteiro, DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        var numero = (await etapas.ListarDaExecucaoAsync(execucao.Id, cancellationToken)).Max(e => e.Numero) + 1;
        EtapaDaExecucao nova;
        if (passo == TipoDePassoDoRoteiro.AprovacaoHumana)
            nova = EtapaDaExecucao.DoRoteiro(execucao, numero, passo, rodada, agora);
        else
        {
            var papel = roteiro.Passos.First(p => p.Tipo == passo).Papel!;
            var participante = (await participantes.ListarDaExecucaoAsync(execucao.Id, cancellationToken))
                .Single(p => p.Papel == papel);
            var versao = await versoesDeAgente.ObterPorIdAsync(participante.IdVersaoDoAgente, cancellationToken) ??
                throw new InvalidOperationException($"Versão do agente {participante.HandleDoAgente} não registrada.");
            nova = EtapaDaExecucao.DoRoteiro(execucao, numero, passo, rodada, agora, papel, participante.IdVersaoDoAgente,
                versao.Driver, versao.Modelo, versao.Esforco);
        }
        await etapas.AdicionarAsync(nova, cancellationToken);
        await dependencias.AdicionarAsync(new DependenciaDeEtapa(nova, anterior), cancellationToken);
        return nova;
    }

    private async Task<Execucao?> LocalizarAsync(ProprietarioDaExecucao proprietario, string codigo,
        CancellationToken cancellationToken)
    {
        if (!Execucao.TentarInterpretarCodigo(codigo, out var numero, out var avulsa) || avulsa) return null;
        var encontrada = await execucoes.ConsultarPorNumeroAsync(numero, cancellationToken);
        return encontrada is { Tipo: TipoDeExecucao.Roteiro } &&
            proprietario.EDono(encontrada.Canal, encontrada.IdDoUsuarioNoCanal) ? encontrada : null;
    }

    private async Task<AvancoDoRoteiroDto> Comum(PosseDoTrabalho posse, Guid idEtapa, ResultadoDaEtapaDto resultado,
        CancellationToken cancellationToken)
    {
        var estadoDaEtapa = await filaDeExecucoes.ConcluirEtapaAsync(posse, idEtapa, resultado, cancellationToken);
        var etapa = await etapas.ObterPorIdAsync(idEtapa, cancellationToken);
        var execucao = etapa is null ? null : await execucoes.ObterPorIdAsync(etapa.IdExecucao, cancellationToken);
        return new AvancoDoRoteiroDto(execucao?.Estado ?? EstadoDaExecucao.Falhou, estadoDaEtapa, Motivo: resultado.Erro);
    }

    private Task Transicao(Execucao execucao, EtapaDaExecucao? etapa, Enum de, Enum para, DateTimeOffset quando,
        CancellationToken cancellationToken) =>
        Equals(de, para) ? Task.CompletedTask : eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao,
            etapa is null ? TipoDeEventoDeOrquestracao.ExecucaoTransitou : TipoDeEventoDeOrquestracao.EtapaTransitou,
            Json(new() { ["de"] = de.ToString(), ["para"] = para.ToString() }), quando, etapa), cancellationToken);

    private static PedidoDeAprovacaoDto Dto(PedidoDeAprovacao pedido) => new(pedido.Id, pedido.Rodada, pedido.HashDoArtefato,
        pedido.Estado, pedido.IdDoDecisor, pedido.DecididoEm, pedido.Motivo, pedido.CriadoEm);

    private static string Chave(Execucao execucao, EtapaDaExecucao etapa) =>
        $"roteiro:{execucao.Id:N}:{etapa.Numero}:{etapa.Tentativa}";

    private static bool HashValido(string? hash) =>
        hash is { Length: PedidoDeAprovacao.TamanhoDoHash } && hash.All(Uri.IsHexDigit) && hash == hash.ToLowerInvariant();

    private static string Cortar(string texto) =>
        texto.Length <= Execucao.TamanhoMaximoDoObjetivo ? texto : texto[..Execucao.TamanhoMaximoDoObjetivo];

    private static string Numero(int valor) => valor.ToString(CultureInfo.InvariantCulture);

    private static string Json(Dictionary<string, string?> dados) => JsonSerializer.Serialize(dados);
}
