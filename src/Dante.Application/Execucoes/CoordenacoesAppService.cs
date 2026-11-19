using System.Globalization;
using System.Text.Json;
using Dante.Application.Agentes;
using Dante.Application.Contextos;
using Dante.Application.Grupos;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Domain.Grupos;

namespace Dante.Application.Execucoes;

// Etapas de uma Coordenada (AD-75), todas no ledger e numeradas na ordem em que nascem:
// * planejamento: turno do coordenador (TurnoDeAgente com o driver da versão congelada dele), de leitura;
// * gate: AprovacaoHumana com o pedido do plano (finalidade PlanoSomenteLeitura);
// * unidades: UnidadeDoPlano materializadas do plano aprovado, cada uma executada por uma delegação consulta.v1;
// * avanço: etapa mecânica (TurnoDeAgente sem agente, sem sessão) que encerra as unidades pelo desfecho das delegações
//   e despacha a próxima. É a retomada da origem do ciclo das delegações (#309) e a etapa aberta pela aprovação.
public sealed class CoordenacoesAppService(
    IFilaDeTrabalho fila,
    IExecucaoRepository execucoes,
    IEtapaDaExecucaoRepository etapas,
    IDependenciaDeEtapaRepository dependencias,
    IEventoDeOrquestracaoRepository eventos,
    IParticipanteDaExecucaoRepository participantes,
    ICoordenacaoDaExecucaoRepository coordenacoes,
    IPlanoDaCoordenacaoRepository planos,
    IPedidoDeAprovacaoRepository pedidos,
    IDelegacaoRepository delegacoes,
    ISessaoDeAgenteRepository sessoes,
    IAgenteRepository agentes,
    IVersaoDoAgenteRepository versoesDeAgente,
    IVersaoDoGrupoRepository versoesDeGrupo,
    IFilaDeExecucoesAppService filaDeExecucoes,
    IUnitOfWorkDaOrquestracao unidade,
    Dante.Application.Mapeamento.IMapsterTypeAdapter mapper) : ICoordenacoesAppService
{
    public const string SemProposta = "O coordenador terminou sem entregar o plano num bloco JSON.";

    private CicloDaDelegacao.Servicos Ciclo => new(delegacoes, execucoes, etapas, eventos, fila);

    // Planejamento: turno do coordenador; avanço: a etapa mecânica, sem agente.
    public static bool EhPlanejamento(EtapaDaExecucao etapa) => etapa.Tipo == TipoDeEtapa.TurnoDeAgente && etapa.Agente is not null;

    public static bool EhAvanco(EtapaDaExecucao etapa) => etapa.Tipo == TipoDeEtapa.TurnoDeAgente && etapa.Agente is null;

    public static PassoDaCoordenacao Passo(EtapaDaExecucao etapa) => etapa.Tipo switch
    {
        TipoDeEtapa.AprovacaoHumana => PassoDaCoordenacao.Gate,
        TipoDeEtapa.UnidadeDoPlano => PassoDaCoordenacao.Unidade,
        _ => EhPlanejamento(etapa) ? PassoDaCoordenacao.Planejamento : PassoDaCoordenacao.Avanco
    };

    public async Task<ExecucaoNaFilaDto> EnfileirarAsync(NovaCoordenacaoDto nova, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nova);
        ArgumentNullException.ThrowIfNull(nova.Grupo);
        ArgumentNullException.ThrowIfNull(nova.Contexto);
        if (string.IsNullOrWhiteSpace(nova.ChaveDeDeduplicacao) || nova.ChaveDeDeduplicacao.Length > TrabalhoDaFila.TamanhoMaximoDaChave)
            throw new ArgumentException("Chave de deduplicação inválida.");
        if (await fila.ObterPorChaveAsync(nova.ChaveDeDeduplicacao, cancellationToken) is { IdExecucao: { } idExistente })
        {
            var existente = await execucoes.ObterPorIdAsync(idExistente, cancellationToken) ??
                throw new InvalidOperationException("Execução do trabalho não registrada no ledger.");
            return new ExecucaoNaFilaDto(existente.Numero, existente.Codigo, existente.Id, false);
        }
        var grupo = nova.Grupo;
        if (!grupo.Resolvido || grupo.Versao?.Configuracao is not { } configuracao)
            throw new InvalidOperationException(grupo.Erro ?? "O grupo não foi resolvido; nada foi criado.");
        if (configuracao.Modo != ModoDeCoordenacao.Coordenador || configuracao.PapelDoCoordenador is not { } papelDoCoordenador)
            throw new ArgumentException($"O grupo {grupo.Nome} não está no modo coordenador.");
        ConferirContexto(grupo, configuracao, nova.Contexto);
        var objetivo = nova.Objetivo?.Trim() ?? "";
        if (objetivo.Length == 0) throw new ArgumentException("Informe a tarefa do grupo.");
        if (objetivo.Length > CoordenacaoDaExecucao.TamanhoMaximoDoObjetivo)
            throw new ArgumentException($"A tarefa passa de {CoordenacaoDaExecucao.TamanhoMaximoDoObjetivo} caracteres.");
        // O objetivo é relido do ledger pelo coordenador em qualquer processo (e repassado às unidades): com aparência de
        // credencial, nada é gravado.
        if (ProtecaoDeSegredos.ContemSegredo(objetivo))
            throw new ArgumentException("A tarefa parece conter uma credencial; reescreva sem ela.");
        var coordenador = grupo.Participantes.Single(p => p.Papel == papelDoCoordenador);
        var agora = fila.Agora;
        var execucao = new Execucao(nova.Numero, TipoDeExecucao.Coordenada, nova.Canal, nova.IdDoUsuarioNoCanal,
            LedgerDeExecucoesAppService.SerializarContexto(nova.Contexto),
            objetivo.Length <= Execucao.TamanhoMaximoDoObjetivo ? objetivo : objetivo[..Execucao.TamanhoMaximoDoObjetivo], agora,
            coordenador.VersaoDoAgente.Id, grupo.Versao.Id);
        var snapshot = grupo.Participantes.Select(p => new ParticipanteDaExecucao(execucao, p.Papel, p.Handle,
            p.VersaoDoAgente.Id, p.VersaoDoAgente.Numero, p.VersaoDoAgente.HashDaConfiguracao, p.PodeEscrever,
            p.Responsabilidades)).ToArray();
        var coordenacao = new CoordenacaoDaExecucao(execucao, grupo.Grupo!.Id, grupo.Grupo.Nome, grupo.Versao.Id,
            grupo.Versao.Numero, grupo.Versao.HashDaConfiguracao, papelDoCoordenador, snapshot, objetivo, false,
            RoteiroDaExecucao.HashDoObjetivo(execucao.Id, objetivo));
        var planejamento = Planejamento(execucao, 1, coordenador.VersaoDoAgente.Configuracao, agora);
        await execucoes.AdicionarAsync(execucao, cancellationToken);
        foreach (var participante in snapshot) await participantes.AdicionarAsync(participante, cancellationToken);
        await coordenacoes.AdicionarAsync(coordenacao, cancellationToken);
        await etapas.AdicionarAsync(planejamento, cancellationToken);
        await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao, TipoDeEventoDeOrquestracao.ExecucaoCriada,
            Json(new()
            {
                ["tipo"] = execucao.Tipo.ToString(),
                ["grupo"] = coordenacao.NomeDoGrupo,
                ["versao"] = Numero(coordenacao.NumeroDaVersaoDoGrupo),
                ["coordenador"] = coordenador.Handle,
                ["snapshot"] = coordenacao.HashDoSnapshot[..12]
            }), agora), cancellationToken);
        await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(execucao, planejamento, nova.ChaveDeDeduplicacao, agora),
            cancellationToken);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
        return new ExecucaoNaFilaDto(execucao.Numero, execucao.Codigo, execucao.Id, true);
    }

    public async Task<CoordenacaoDaExecucaoDto?> ObterAsync(Guid idExecucao, CancellationToken cancellationToken = default)
    {
        var execucao = await execucoes.ObterPorIdAsync(idExecucao, cancellationToken);
        if (execucao is not { Tipo: TipoDeExecucao.Coordenada }) return null;
        var coordenacao = await coordenacoes.ObterDaExecucaoAsync(idExecucao, cancellationToken);
        if (coordenacao is null) return null;
        var revisoes = await planos.ListarDaExecucaoAsync(idExecucao, cancellationToken);
        var todosOsPedidos = await pedidos.ListarDaExecucaoAsync(idExecucao, cancellationToken);
        var lista = await etapas.ListarDaExecucaoAsync(idExecucao, cancellationToken);
        var revisaoDoPlano = revisoes.ToDictionary(p => p.Id, p => p.Revisao);
        var etapasDto = new List<EtapaDaCoordenacaoDto>();
        foreach (var etapa in lista)
        {
            var revisao = etapa.IdPlano is { } idPlano ? revisaoDoPlano[idPlano]
                : todosOsPedidos.FirstOrDefault(p => p.IdEtapa == etapa.Id)?.Rodada;
            etapasDto.Add(new EtapaDaCoordenacaoDto(etapa.Id, etapa.Numero, Passo(etapa), etapa.Estado, etapa.Tentativa,
                revisao, etapa.Unidade, etapa.Papel, etapa.Motivo, etapa.Erro,
                await CodigoDaSessaoAsync(etapa.IdSessaoDeAgente, cancellationToken)));
        }
        return new CoordenacaoDaExecucaoDto
        {
            IdExecucao = execucao.Id,
            Codigo = execucao.Codigo,
            Estado = execucao.Estado,
            Motivo = execucao.Motivo,
            Erro = execucao.Erro,
            Canal = execucao.Canal,
            IdDoUsuarioNoCanal = execucao.IdDoUsuarioNoCanal,
            NomeDoGrupo = coordenacao.NomeDoGrupo,
            NumeroDaVersaoDoGrupo = coordenacao.NumeroDaVersaoDoGrupo,
            HashDaVersaoDoGrupo = coordenacao.HashDaVersaoDoGrupo,
            HashDoSnapshot = coordenacao.HashDoSnapshot,
            PapelDoCoordenador = coordenacao.PapelDoCoordenador,
            Objetivo = coordenacao.ObjetivoRedigido,
            Participantes = (await participantes.ListarDaExecucaoAsync(idExecucao, cancellationToken))
                .Select(p => new ParticipanteDaExecucaoDto(p.Papel, p.HandleDoAgente, p.IdVersaoDoAgente,
                    p.NumeroDaVersaoDoAgente, p.HashDaConfiguracaoDoAgente, p.PodeEscrever, p.Responsabilidades)).ToArray(),
            Planos = revisoes.Select(p => new PlanoDaCoordenacaoDto(p.Id, p.Revisao, p.Motivo, p.Estado, p.Hash,
                p.QuantidadeDeUnidades, p.Conteudo, p.CriadoEm, p.DecididoEm,
                todosOsPedidos.LastOrDefault(x => x.IdPlano == p.Id) is { } pedido ? Dto(pedido) : null)).ToArray(),
            Etapas = etapasDto
        };
    }

    public async Task<CoordenacaoDaExecucaoDto?> ObterDoProprietarioAsync(ProprietarioDaExecucao proprietario, string codigo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proprietario);
        return await LocalizarAsync(proprietario, codigo, cancellationToken) is { } execucao
            ? await ObterAsync(execucao.Id, cancellationToken) : null;
    }

    public async Task<string?> ConferirIntegridadeAsync(Guid idExecucao, CancellationToken cancellationToken = default)
    {
        var coordenacao = await coordenacoes.ObterDaExecucaoAsync(idExecucao, cancellationToken);
        if (coordenacao is null) return "A coordenação da execução não está registrada.";
        var linhas = await participantes.ListarDaExecucaoAsync(idExecucao, cancellationToken);
        if (linhas.Count == 0 || !coordenacao.SnapshotConfere(linhas))
            return "O snapshot do grupo não confere com o gravado na criação; nada foi executado.";
        var versao = await versoesDeGrupo.ObterComParticipantesAsync(coordenacao.IdVersaoDoGrupo, cancellationToken);
        if (versao is null || !versao.Integra || versao.HashDaConfiguracao != coordenacao.HashDaVersaoDoGrupo)
            return $"A versão {coordenacao.NumeroDaVersaoDoGrupo} do grupo {coordenacao.NomeDoGrupo} não confere com o hash congelado.";
        foreach (var participante in linhas)
        {
            var agente = await versoesDeAgente.ObterPorIdAsync(participante.IdVersaoDoAgente, cancellationToken);
            if (agente is null || !agente.Integra || agente.HashDaConfiguracao != participante.HashDaConfiguracaoDoAgente)
                return $"A versão do agente {participante.HandleDoAgente} (papel {participante.Papel}) não confere com o hash congelado.";
        }
        return null;
    }

    public async Task<PlanejamentoDaCoordenacaoDto> ObterPlanejamentoAsync(Guid idExecucao, Guid idEtapa,
        CancellationToken cancellationToken = default)
    {
        var execucao = await execucoes.ObterPorIdAsync(idExecucao, cancellationToken);
        var etapa = await etapas.ObterPorIdAsync(idEtapa, cancellationToken);
        if (execucao is not { Tipo: TipoDeExecucao.Coordenada } || etapa is null || etapa.IdExecucao != execucao.Id ||
            !EhPlanejamento(etapa) || etapa.Terminal)
            throw new InvalidOperationException("A etapa não é um planejamento em aberto desta coordenação.");
        var coordenacao = await coordenacoes.ObterDaExecucaoAsync(execucao.Id, cancellationToken) ??
            throw new InvalidOperationException("A coordenação da execução não está registrada.");
        var linhas = await participantes.ListarDaExecucaoAsync(execucao.Id, cancellationToken);
        var versoes = await VersoesAsync(linhas, cancellationToken);
        var contexto = ContextoDoPlano.DoSnapshot(coordenacao, linhas, versoes,
            await delegacoes.ContarDaRaizAsync(execucao.IdExecucaoRaiz, cancellationToken));
        var participanteCoordenador = linhas.Single(p => p.Papel == coordenacao.PapelDoCoordenador);
        var versaoDoCoordenador = versoes.Single(v => v.Id == participanteCoordenador.IdVersaoDoAgente);
        var anterior = await planos.ObterUltimaRevisaoAsync(execucao.Id, cancellationToken);
        var (motivo, pedidoHumano) = await MotivoDaRevisaoAsync(anterior, cancellationToken);
        var anteriores = new List<UnidadeAnteriorDto>();
        if (motivo == MotivoDoPlano.UnidadeSemResposta)
        {
            var daOrigem = await delegacoes.ListarDaOrigemAsync(execucao.Id, cancellationToken);
            foreach (var unidadeAnterior in await etapas.ListarDoPlanoAsync(anterior!.Id, cancellationToken))
            {
                var delegacao = daOrigem.Where(d => d.IdEtapaOrigem == unidadeAnterior.Id).MaxBy(d => d.CriadaEm);
                anteriores.Add(new UnidadeAnteriorDto(unidadeAnterior.Unidade!, unidadeAnterior.Papel!, unidadeAnterior.Estado,
                    unidadeAnterior.Erro ?? unidadeAnterior.Motivo ?? delegacao?.Motivo, delegacao?.Resultado));
            }
        }
        var recusadas = (await eventos.ConsultarDoTipoAsync([execucao.Id], TipoDeEventoDeOrquestracao.PlanoRecusado,
            cancellationToken)).Count(e => e.IdEtapa == etapa.Id);
        return new PlanejamentoDaCoordenacaoDto(execucao.Id, execucao.Codigo, execucao.Canal, execucao.IdDoUsuarioNoCanal,
            execucao.Contexto, coordenacao.ObjetivoRedigido, coordenacao.NomeDoGrupo, coordenacao.NumeroDaVersaoDoGrupo,
            coordenacao.PapelDoCoordenador, participanteCoordenador.HandleDoAgente,
            mapper.Mapear<VersaoDoAgente, VersaoDoAgenteDto>(versaoDoCoordenador),
            linhas.Where(p => p.Papel != coordenacao.PapelDoCoordenador).Select(p => new PapelParaPlanejarDto(p.Papel,
                p.HandleDoAgente, p.NumeroDaVersaoDoAgente, p.Responsabilidades,
                versoes.Single(v => v.Id == p.IdVersaoDoAgente).Capacidades)).ToArray(),
            contexto.UnidadesDisponiveis, (anterior?.Revisao ?? 0) + 1, motivo, anterior?.Conteudo, anteriores,
            pedidoHumano?.Motivo, recusadas);
    }

    public async Task<RegistroDaPropostaDto> RegistrarPropostaAsync(PosseDoTrabalho posse, Guid idEtapa, string? proposta,
        CancellationToken cancellationToken = default)
    {
        RegistroDaPropostaDto? registro = null;
        var cancelar = false;
        await fila.GravarComPosseAsync(posse, async token =>
        {
            var (execucao, etapa) = await CarregarAsync(idEtapa, token);
            if (!EhPlanejamento(etapa) || etapa.Estado != EstadoDaEtapa.EmExecucao)
                throw new InvalidOperationException("Só o planejamento em curso recebe a proposta do plano.");
            // Pedido do dono registrado antes (#305): a conclusão comum grava Cancelada.
            if (await CancelamentoPedidoAsync(execucao.Id, token))
            {
                cancelar = true;
                return;
            }
            var coordenacao = await coordenacoes.ObterDaExecucaoAsync(execucao.Id, token) ??
                throw new InvalidOperationException("A coordenação da execução não está registrada.");
            var linhas = await participantes.ListarDaExecucaoAsync(execucao.Id, token);
            // A validação é a pura do domínio, contra o snapshot congelado, inteira antes de qualquer gravação de plano.
            var contexto = ContextoDoPlano.DoSnapshot(coordenacao, linhas, await VersoesAsync(linhas, token),
                await delegacoes.ContarDaRaizAsync(execucao.IdExecucaoRaiz, token));
            var validacao = proposta is null
                ? new ResultadoDaValidacaoDoPlano(null, [new RecusaDoPlano(MotivoDaRecusaDoPlano.FormaInvalida, null, SemProposta)])
                : ValidacaoDoPlano.Validar(proposta, contexto);
            var agora = fila.Agora;
            if (!validacao.Valido)
            {
                registro = await RecusarPropostaAsync(execucao, etapa, validacao.Recusas, agora, token);
                return;
            }
            var anterior = await planos.ObterUltimaRevisaoAsync(execucao.Id, token);
            var (motivo, pedidoHumano) = await MotivoDaRevisaoAsync(anterior, token);
            var plano = motivo == MotivoDoPlano.Inicial
                ? PlanoDaCoordenacao.Inicial(execucao, coordenacao, validacao.Plano!, agora)
                : PlanoDaCoordenacao.Revisar(execucao, anterior!, motivo, validacao.Plano!,
                    await etapas.ListarDoPlanoAsync(anterior!.Id, token), pedidoHumano, agora);
            await planos.AdicionarAsync(plano, token);
            var etapaAntes = etapa.Estado;
            etapa.Concluir(null, agora);
            await Transicao(execucao, etapa, etapaAntes, etapa.Estado, agora, token);
            var gate = new EtapaDaExecucao(execucao, await ProximoNumeroAsync(execucao.Id, token), TipoDeEtapa.AprovacaoHumana,
                false, PoliticaDeRetomada.Automatica, agora);
            await etapas.AdicionarAsync(gate, token);
            await dependencias.AdicionarAsync(new DependenciaDeEtapa(gate, etapa), token);
            gate.TornarPronta([etapa]);
            gate.Iniciar(agora);
            gate.AguardarAprovacao();
            await Transicao(execucao, gate, EstadoDaEtapa.Pendente, gate.Estado, agora, token);
            // O hash do pedido é o calculado pelo plano a partir da forma canônica, nunca um informado.
            var pedido = PedidoDeAprovacao.DoPlano(execucao, gate, plano, coordenacao, agora);
            await pedidos.AdicionarAsync(pedido, token);
            var execucaoAntes = execucao.Estado;
            execucao.AguardarAprovacao($"Aguardando sua aprovação do plano (revisão {plano.Revisao}, " +
                $"{plano.QuantidadeDeUnidades} unidade(s), hash {plano.Hash[..12]}). Aprovar autoriza só este plano " +
                "somente leitura.");
            await Transicao(execucao, null, execucaoAntes, execucao.Estado, agora, token);
            await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao, TipoDeEventoDeOrquestracao.AprovacaoSolicitada,
                Json(new()
                {
                    ["finalidade"] = pedido.Finalidade.ToString(),
                    ["revisao"] = Numero(plano.Revisao),
                    ["motivo"] = plano.Motivo.ToString(),
                    ["unidades"] = Numero(plano.QuantidadeDeUnidades),
                    ["plano"] = plano.Hash[..12],
                    ["snapshot"] = pedido.HashDoSnapshot[..12]
                }), agora, gate), token);
            await unidade.SalvarAlteracoesAsync(token);
            registro = new(DesfechoDaProposta.GateAberto, execucao.Estado, [], plano.Revisao, plano.Hash);
        }, null, cancellationToken: cancellationToken);
        if (cancelar)
        {
            var estado = await filaDeExecucoes.ConcluirEtapaAsync(posse, idEtapa,
                new ResultadoDaEtapaDto(EstadoDaEtapa.Cancelada), cancellationToken);
            return new(DesfechoDaProposta.Cancelada, estado == EstadoDaEtapa.Cancelada ? EstadoDaExecucao.Cancelada
                : EstadoDaExecucao.Falhou, []);
        }
        // Gate aberto ou execução encerrada: o trabalho termina numa segunda gravação sob posse, com o desfecho já durável.
        if (registro!.Desfecho != DesfechoDaProposta.CorrecaoPedida)
            await fila.GravarComPosseAsync(posse, null, EstadoDoTrabalho.Concluido, cancellationToken: cancellationToken);
        return registro;
    }

    public async Task<RegistroDaPropostaDto> FalharPlanejamentoAsync(PosseDoTrabalho posse, Guid idEtapa, string motivo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        // A conclusão comum: etapa e execução Falhou (ou Cancelada, com pedido do dono), e o encerramento da coordenação.
        var estado = await filaDeExecucoes.ConcluirEtapaAsync(posse, idEtapa,
            new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou, Erro: motivo), cancellationToken);
        return estado == EstadoDaEtapa.Cancelada
            ? new(DesfechoDaProposta.Cancelada, EstadoDaExecucao.Cancelada, [])
            : new(DesfechoDaProposta.Encerrada, EstadoDaExecucao.Falhou, []);
    }

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
            if (string.IsNullOrWhiteSpace(instrucao)) throw new ArgumentException("Diga o que corrigir no plano.");
            // A instrução vai ao coordenador e ao ledger: nunca com credencial.
            if (ProtecaoDeSegredos.ContemSegredo(instrucao))
                throw new ArgumentException("A instrução parece conter uma credencial; reescreva sem ela.");
        }
        ResultadoDaDecisaoDto resultado = new(ResultadoDaDecisao.SemPedidoPendente, encontrada.Codigo);
        await fila.ExecutarEmTransacaoAsync(async token =>
        {
            // Mesma ordem das gravações sob posse: trabalhos (nenhum ativo num gate), depois a execução, travados.
            if ((await fila.TravarAtivosDaExecucaoAsync(encontrada.Id, token)).Count > 0) return;
            var execucao = await execucoes.ObterParaAtualizarAsync(encontrada.Id, token) ??
                throw new InvalidOperationException("Execução não registrada no ledger.");
            if (execucao.Estado != EstadoDaExecucao.AguardandoAprovacao) return;
            var pedido = await pedidos.ObterPendenteAsync(execucao.Id, token);
            if (pedido is not { Finalidade: FinalidadeDaAprovacao.PlanoSomenteLeitura, IdPlano: { } idPlano }) return;
            var coordenacao = await coordenacoes.ObterDaExecucaoAsync(execucao.Id, token) ??
                throw new InvalidOperationException("A coordenação da execução não está registrada.");
            if (pedido.HashDoSnapshot != coordenacao.HashDoSnapshot)
                throw new InvalidOperationException("O pedido de aprovação não é do snapshot desta execução.");
            var plano = await planos.ObterPorIdAsync(idPlano, token) ??
                throw new InvalidOperationException("Plano do pedido não registrado.");
            var gate = await etapas.ObterPorIdAsync(pedido.IdEtapa, token) ??
                throw new InvalidOperationException("Etapa de aprovação não registrada.");
            var agora = fila.Agora;
            var execucaoAntes = execucao.Estado;
            var gateAntes = gate.Estado;
            switch (decisao.Tipo)
            {
                case TipoDeDecisaoHumana.Aprovar:
                {
                    pedido.Aprovar(proprietario.Canal, proprietario.IdDoUsuario, decisao.HashConfirmado, agora);
                    plano.Aprovar(pedido);
                    gate.Continuar();
                    gate.Concluir(null, agora);
                    execucao.Retomar();
                    // O grafo do plano aprovado no ledger, numerado depois de todas as etapas existentes, e a etapa de
                    // avanço que despacha a primeira unidade (a fila, sob posse, nunca esta transação).
                    var numero = await ProximoNumeroAsync(execucao.Id, token);
                    var grafo = plano.Materializar(execucao, coordenacao,
                        await participantes.ListarDaExecucaoAsync(execucao.Id, token), numero, agora);
                    foreach (var etapaDaUnidade in grafo.Etapas) await etapas.AdicionarAsync(etapaDaUnidade, token);
                    foreach (var dependencia in grafo.Dependencias) await dependencias.AdicionarAsync(dependencia, token);
                    var avanco = Avanco(execucao, numero + grafo.Etapas.Count, agora);
                    await etapas.AdicionarAsync(avanco, token);
                    await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(execucao, avanco, ChaveDoAvanco(execucao, avanco),
                        agora), token);
                    resultado = new(ResultadoDaDecisao.Aprovada, execucao.Codigo, plano.Revisao);
                    break;
                }
                case TipoDeDecisaoHumana.Corrigir:
                {
                    // Outra versão do plano só dentro do limite (D6); sem ela, o gate continua aberto.
                    if (plano.Revisao + 1 > PlanoDaCoordenacao.RevisoesMaximas)
                        throw new InvalidOperationException($"Limite de {PlanoDaCoordenacao.RevisoesMaximas} versões do " +
                            "plano atingido. Aprove ou recuse.");
                    pedido.SolicitarCorrecao(proprietario.Canal, proprietario.IdDoUsuario, decisao.HashConfirmado,
                        instrucao!, agora);
                    gate.Continuar();
                    gate.Concluir(null, agora);
                    execucao.Retomar();
                    // O plano continua aguardando: a revisão PedidoHumano o substitui quando o coordenador propuser outro.
                    var planejamento = Planejamento(execucao, await ProximoNumeroAsync(execucao.Id, token),
                        await ConfiguracaoDoCoordenadorAsync(execucao, token), agora);
                    await etapas.AdicionarAsync(planejamento, token);
                    await dependencias.AdicionarAsync(new DependenciaDeEtapa(planejamento, gate), token);
                    await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(execucao, planejamento,
                        ChaveDoAvanco(execucao, planejamento), agora), token);
                    resultado = new(ResultadoDaDecisao.CorrecaoAgendada, execucao.Codigo, plano.Revisao + 1);
                    break;
                }
                default:
                    pedido.Recusar(proprietario.Canal, proprietario.IdDoUsuario, PedidoDeCancelamento.MotivoSeguro(instrucao),
                        agora);
                    plano.Recusar(pedido);
                    gate.Cancelar("plano recusado na aprovação humana", null, agora);
                    execucao.Cancelar("Plano recusado na aprovação humana.", agora);
                    resultado = new(ResultadoDaDecisao.Recusada, execucao.Codigo, plano.Revisao);
                    break;
            }
            await Transicao(execucao, gate, gateAntes, gate.Estado, agora, token);
            await Transicao(execucao, null, execucaoAntes, execucao.Estado, agora, token);
            await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao, TipoDeEventoDeOrquestracao.SolicitacaoResolvida,
                Json(new()
                {
                    ["decisao"] = pedido.Estado.ToString(),
                    ["finalidade"] = pedido.Finalidade.ToString(),
                    ["autor"] = proprietario.Autor,
                    ["plano"] = pedido.HashDoArtefato[..12],
                    ["revisao"] = Numero(plano.Revisao)
                }), agora, gate), token);
            if (execucao.Terminal) await EncerramentoDaCoordenacao.AoEncerrarAsync(planos, etapas, eventos, execucao, agora, token);
            await unidade.SalvarAlteracoesAsync(token);
        }, cancellationToken);
        return resultado;
    }

    public async Task<AvancoDaCoordenacaoDto> AvancarAsync(PosseDoTrabalho posse, Guid idEtapa,
        CancellationToken cancellationToken = default)
    {
        // Número da subordinada reservado antes, na transação curta da própria sequência (como na delegação da #309), e
        // só se ainda houver unidade a despachar: reservar dentro da gravação prenderia a trava global da sequência até
        // o commit. Sem despacho, fica só uma lacuna na numeração.
        var etapaLida = await etapas.ObterPorIdAsync(idEtapa, cancellationToken) ??
            throw new InvalidOperationException("Etapa não registrada no ledger.");
        var numeroDaSubordinada = 0L;
        if (await planos.ObterUltimaRevisaoAsync(etapaLida.IdExecucao, cancellationToken) is { Estado: EstadoDoPlano.Aprovado } aprovado &&
            (await etapas.ListarDoPlanoAsync(aprovado.Id, cancellationToken)).Any(u => u.Estado == EstadoDaEtapa.Pendente))
            numeroDaSubordinada = await execucoes.ReservarNumeroAsync(cancellationToken: cancellationToken);
        AvancoDaCoordenacaoDto? avanco = null;
        var cancelar = false;
        await fila.GravarComPosseAsync(posse, async token =>
        {
            var (execucao, etapa) = await CarregarAsync(idEtapa, token);
            if (!EhAvanco(etapa) || etapa.Estado is not (EstadoDaEtapa.Pendente or EstadoDaEtapa.Pronta))
                throw new InvalidOperationException("Só uma etapa de avanço a executar avança a coordenação.");
            if (await CancelamentoPedidoAsync(execucao.Id, token))
            {
                cancelar = true;
                return;
            }
            var agora = fila.Agora;
            await IniciarAsync(execucao, etapa, agora, token);
            var plano = await planos.ObterUltimaRevisaoAsync(execucao.Id, token);
            if (plano is not { Estado: EstadoDoPlano.Aprovado })
                throw new InvalidOperationException("A coordenação não tem plano aprovado a executar.");
            var unidades = await etapas.ListarDoPlanoAsync(plano.Id, token);
            var daOrigem = await delegacoes.ListarDaOrigemAsync(execucao.Id, token);

            // 1. Unidades em curso cujas delegações terminaram: o desfecho da delegação é o da unidade.
            var encerradas = 0;
            foreach (var emCurso in unidades.Where(u => u.Estado == EstadoDaEtapa.EmExecucao))
            {
                var daUnidade = daOrigem.Where(d => d.IdEtapaOrigem == emCurso.Id).ToList();
                if (daUnidade.Count == 0 || daUnidade.Any(d => !d.Terminal))
                    throw new InvalidOperationException($"A unidade {emCurso.Unidade} ainda está em curso.");
                var delegacao = daUnidade.MaxBy(d => d.CriadaEm)!;
                var antes = emCurso.Estado;
                switch (delegacao.Estado)
                {
                    case EstadoDaDelegacao.Respondida: emCurso.Concluir(null, agora); break;
                    case EstadoDaDelegacao.Cancelada: emCurso.Cancelar(delegacao.Motivo, null, agora); break;
                    default:
                        emCurso.Falhar(delegacao.Motivo ?? $"Delegação {delegacao.Estado}.", null, agora);
                        break;
                }
                await Transicao(execucao, emCurso, antes, emCurso.Estado, agora, token);
                encerradas++;
            }

            // 2. A próxima unidade pronta, na ordem topológica gravada; uma por vez (paralelismo 1).
            var requeridasPor = (await dependencias.ListarDaExecucaoAsync(execucao.Id, token))
                .GroupBy(d => d.IdEtapa).ToDictionary(g => g.Key, g => g.Select(d => d.IdEtapaRequerida).ToHashSet());
            string? despachada = null;
            foreach (var candidata in unidades.Where(u => u.Estado == EstadoDaEtapa.Pendente && numeroDaSubordinada > 0)
                         .OrderBy(u => u.Numero))
            {
                var requeridas = unidades.Where(u => requeridasPor.TryGetValue(candidata.Id, out var ids) && ids.Contains(u.Id))
                    .ToList();
                if (requeridas.Any(r => r.Estado != EstadoDaEtapa.Concluida)) continue;
                candidata.TornarPronta(requeridas);
                candidata.Iniciar(agora);
                await Transicao(execucao, candidata, EstadoDaEtapa.Pendente, candidata.Estado, agora, token);
                try
                {
                    despachada = await DespacharAsync(execucao, candidata, plano, numeroDaSubordinada, agora, token);
                    break;
                }
                catch (DelegacaoRecusadaException recusa)
                {
                    // Recusa do domínio (agente desativado, orçamento, snapshot): a unidade termina sem delegação.
                    candidata.Falhar($"Despacho recusado: {recusa.Message}", null, agora);
                    await Transicao(execucao, candidata, EstadoDaEtapa.EmExecucao, candidata.Estado, agora, token);
                }
            }

            var etapaAntes = etapa.Estado;
            etapa.Concluir(null, agora);
            await Transicao(execucao, etapa, etapaAntes, etapa.Estado, agora, token);
            var execucaoAntes = execucao.Estado;
            var replanejar = false;
            string? motivo = null;
            if (despachada is not null) execucao.AguardarDelegacoes();
            else if (unidades.All(u => u.Estado == EstadoDaEtapa.Concluida)) execucao.Concluir(agora);
            else
            {
                var semResposta = unidades.Count(u => u.Estado is EstadoDaEtapa.Falhou or EstadoDaEtapa.Cancelada);
                var restantes = LimitesDeDelegacao.Teto.DelegacoesPorExecucao -
                    await delegacoes.ContarDaRaizAsync(execucao.IdExecucaoRaiz, token);
                // D6: replanejar só por unidade sem resposta, com revisão e orçamento disponíveis; a nova revisão passa
                // pela validação e por um gate próprio.
                if (semResposta > 0 && plano.Revisao < PlanoDaCoordenacao.RevisoesMaximas && restantes > 0)
                {
                    var planejamento = Planejamento(execucao, await ProximoNumeroAsync(execucao.Id, token),
                        await ConfiguracaoDoCoordenadorAsync(execucao, token), agora);
                    await etapas.AdicionarAsync(planejamento, token);
                    await dependencias.AdicionarAsync(new DependenciaDeEtapa(planejamento, etapa), token);
                    await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(execucao, planejamento,
                        ChaveDoAvanco(execucao, planejamento), agora), token);
                    replanejar = true;
                }
                else
                {
                    foreach (var pendente in unidades.Where(u => u.Estado is EstadoDaEtapa.Pendente or EstadoDaEtapa.Pronta))
                    {
                        var antes = pendente.Estado;
                        pendente.Ignorar("Depende de unidade sem resposta.");
                        await Transicao(execucao, pendente, antes, pendente.Estado, agora, token);
                    }
                    motivo = $"{semResposta} unidade(s) do plano sem resposta" +
                        (plano.Revisao >= PlanoDaCoordenacao.RevisoesMaximas
                            ? $" e o limite de {PlanoDaCoordenacao.RevisoesMaximas} versões do plano atingido"
                            : restantes <= 0 ? " e o orçamento de delegações esgotado" : "") +
                        "; a consolidação mostra cada desfecho.";
                    execucao.Falhar(motivo, agora);
                }
            }
            await Transicao(execucao, null, execucaoAntes, execucao.Estado, agora, token);
            if (execucao.Terminal) await EncerramentoDaCoordenacao.AoEncerrarAsync(planos, etapas, eventos, execucao, agora, token);
            await unidade.SalvarAlteracoesAsync(token);
            avanco = new(execucao.Estado, encerradas, despachada, replanejar, motivo);
        }, null, cancellationToken: cancellationToken);
        if (cancelar)
        {
            await filaDeExecucoes.ConcluirEtapaAsync(posse, idEtapa, new ResultadoDaEtapaDto(EstadoDaEtapa.Cancelada),
                cancellationToken);
            return new(EstadoDaExecucao.Cancelada, 0, null, false);
        }
        await fila.GravarComPosseAsync(posse, null, EstadoDoTrabalho.Concluido, cancellationToken: cancellationToken);
        return avanco!;
    }

    public async Task<ConsolidacaoMecanicaDto?> ConsolidarAsync(Guid idExecucao, CancellationToken cancellationToken = default)
    {
        var execucao = await execucoes.ObterPorIdAsync(idExecucao, cancellationToken);
        if (execucao is not { Tipo: TipoDeExecucao.Coordenada }) return null;
        var coordenacao = await coordenacoes.ObterDaExecucaoAsync(idExecucao, cancellationToken);
        if (coordenacao is null) return null;
        var linhas = await participantes.ListarDaExecucaoAsync(idExecucao, cancellationToken);
        var todas = await etapas.ListarDaExecucaoAsync(idExecucao, cancellationToken);
        var todosOsPedidos = await pedidos.ListarDaExecucaoAsync(idExecucao, cancellationToken);
        var daOrigem = await delegacoes.ListarDaOrigemAsync(idExecucao, cancellationToken);
        // Cada revisão nasce de um planejamento concluído, na mesma ordem: a sessão daquele planejamento é a do plano.
        var planejamentosConcluidos = todas.Where(e => EhPlanejamento(e) && e.Estado == EstadoDaEtapa.Concluida)
            .OrderBy(e => e.Numero).ToList();
        var revisoes = new List<RevisaoNaTrilhaDto>();
        var unidadesNaTrilha = new List<UnidadeNaTrilhaDto>();
        foreach (var plano in await planos.ListarDaExecucaoAsync(idExecucao, cancellationToken))
        {
            var pedido = todosOsPedidos.LastOrDefault(p => p.IdPlano == plano.Id);
            var planejamento = plano.Revisao <= planejamentosConcluidos.Count ? planejamentosConcluidos[plano.Revisao - 1] : null;
            revisoes.Add(new RevisaoNaTrilhaDto(plano.Revisao, plano.Motivo, plano.Estado, plano.Hash,
                pedido?.Estado is null or EstadoDoPedidoDeAprovacao.Pendente ? null : pedido.Estado,
                pedido?.IdDoDecisor is { } decisor ? $"{pedido.CanalDoDecisor}:{decisor.ToString(CultureInfo.InvariantCulture)}" : null,
                pedido?.DecididoEm, await CodigoDaSessaoAsync(planejamento?.IdSessaoDeAgente, cancellationToken)));
            foreach (var etapa in todas.Where(e => e.IdPlano == plano.Id))
            {
                var participante = linhas.Single(p => p.Papel == etapa.Papel);
                var delegacao = daOrigem.Where(d => d.IdEtapaOrigem == etapa.Id).MaxBy(d => d.CriadaEm);
                var subordinada = delegacao is null ? null
                    : await execucoes.ObterPorIdAsync(delegacao.IdExecucaoSubordinada, cancellationToken);
                var situacao = etapa.Estado switch
                {
                    EstadoDaEtapa.Concluida when delegacao?.Estado == EstadoDaDelegacao.Respondida => SituacaoDaUnidade.Respondida,
                    // Cancelada antes de ser despachada (a execução terminou antes): não foi executada.
                    EstadoDaEtapa.Ignorada or EstadoDaEtapa.Cancelada when delegacao is null => SituacaoDaUnidade.Ignorada,
                    EstadoDaEtapa.Concluida or EstadoDaEtapa.Falhou or EstadoDaEtapa.Cancelada => SituacaoDaUnidade.SemResposta,
                    EstadoDaEtapa.Pendente or EstadoDaEtapa.Pronta => SituacaoDaUnidade.Pendente,
                    _ => SituacaoDaUnidade.EmCurso
                };
                unidadesNaTrilha.Add(new UnidadeNaTrilhaDto(plano.Revisao, etapa.Unidade!, participante.Papel,
                    participante.HandleDoAgente, participante.NumeroDaVersaoDoAgente, participante.HashDaConfiguracaoDoAgente,
                    etapa.Numero, etapa.Estado, situacao, etapa.Erro ?? etapa.Motivo ?? delegacao?.Motivo, subordinada?.Codigo,
                    delegacao?.Estado, delegacao?.Resultado, delegacao?.HashDoResultado,
                    await CodigoDaSessaoAsync(delegacao?.IdSessaoDoResultado, cancellationToken),
                    etapa.EncerradaEm ?? delegacao?.EncerradaEm));
            }
        }
        return new ConsolidacaoMecanicaDto(execucao.Id, execucao.Codigo, execucao.Estado, execucao.Erro ?? execucao.Motivo,
            coordenacao.NomeDoGrupo, coordenacao.NumeroDaVersaoDoGrupo, coordenacao.HashDoSnapshot, revisoes, unidadesNaTrilha);
    }

    public async Task<ConsolidacaoMecanicaDto?> ConsolidarDoProprietarioAsync(ProprietarioDaExecucao proprietario,
        string codigo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proprietario);
        return await LocalizarAsync(proprietario, codigo, cancellationToken) is { } execucao
            ? await ConsolidarAsync(execucao.Id, cancellationToken) : null;
    }

    // A proposta inválida: as recusas (só motivo e unidade) no ledger, contadas por etapa. Dentro da correção única, a
    // etapa continua; além dela, etapa e execução falham e a coordenação é encerrada (sem plano novo, filhos nem trabalhos).
    private async Task<RegistroDaPropostaDto> RecusarPropostaAsync(Execucao execucao, EtapaDaExecucao etapa,
        IReadOnlyList<RecusaDoPlano> recusas, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var anteriores = (await eventos.ConsultarDoTipoAsync([execucao.Id], TipoDeEventoDeOrquestracao.PlanoRecusado,
            cancellationToken)).Count(e => e.IdEtapa == etapa.Id);
        var tentativa = anteriores + 1;
        await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao, TipoDeEventoDeOrquestracao.PlanoRecusado,
            Json(new()
            {
                ["tentativa"] = Numero(tentativa),
                ["recusas"] = string.Join("; ", recusas.Select(r => r.Unidade is null ? r.Motivo.ToString() : $"{r.Motivo}:{r.Unidade}"))
            }), agora, etapa), cancellationToken);
        if (tentativa <= ValidacaoDoPlano.CorrecoesPermitidas)
        {
            await unidade.SalvarAlteracoesAsync(cancellationToken);
            return new(DesfechoDaProposta.CorrecaoPedida, execucao.Estado, recusas);
        }
        var erro = "Plano inválido depois da correção permitida: " + string.Join("; ", recusas.Select(r => r.Mensagem));
        if (erro.Length > Execucao.TamanhoMaximoDoTexto) erro = erro[..Execucao.TamanhoMaximoDoTexto];
        var etapaAntes = etapa.Estado;
        var execucaoAntes = execucao.Estado;
        etapa.Falhar(erro, null, agora);
        execucao.Falhar(erro, agora);
        await Transicao(execucao, etapa, etapaAntes, etapa.Estado, agora, cancellationToken);
        await Transicao(execucao, null, execucaoAntes, execucao.Estado, agora, cancellationToken);
        await EncerramentoDaCoordenacao.AoEncerrarAsync(planos, etapas, eventos, execucao, agora, cancellationToken);
        await CicloDaDelegacao.AoEncerrarAsync(Ciclo, execucao, agora, cancellationToken);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
        return new(DesfechoDaProposta.Encerrada, execucao.Estado, recusas);
    }

    // Despacho de uma unidade pelo domínio (Delegacao.CriarDoPlano): tudo vem do ledger e do snapshot, nada do agente.
    // Mesma montagem da delegação pedida por uma Tarefa (#309): subordinada com a etapa de leitura, delegação Solicitada
    // → Aceita, eventos e o trabalho da fila, na transação de quem avança.
    private async Task<string> DespacharAsync(Execucao origem, EtapaDaExecucao etapaDaUnidade, PlanoDaCoordenacao plano,
        long numero, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var coordenacao = await coordenacoes.ObterDaExecucaoAsync(origem.Id, cancellationToken) ??
            throw new InvalidOperationException("A coordenação da execução não está registrada.");
        var linhas = await participantes.ListarDaExecucaoAsync(origem.Id, cancellationToken);
        var participanteCoordenador = linhas.Single(p => p.Papel == coordenacao.PapelDoCoordenador);
        var participanteDestinatario = linhas.Single(p => p.Papel == etapaDaUnidade.Papel);
        var versaoDoCoordenador = await VersaoAsync(participanteCoordenador, cancellationToken);
        var versaoDestinataria = await VersaoAsync(participanteDestinatario, cancellationToken);
        var coordenador = await agentes.ObterPorIdAsync(versaoDoCoordenador.IdAgente, cancellationToken) ??
            throw new InvalidOperationException("Agente coordenador não registrado.");
        var destinatario = await agentes.ObterPorIdAsync(versaoDestinataria.IdAgente, cancellationToken) ??
            throw new InvalidOperationException($"Agente {participanteDestinatario.HandleDoAgente} não registrado.");
        var (delegacao, subordinada) = Delegacao.CriarDoPlano(new PedidoDeDelegacaoDoPlano(origem, etapaDaUnidade, plano,
            coordenacao, participanteCoordenador, coordenador, versaoDoCoordenador, participanteDestinatario, destinatario,
            versaoDestinataria, DelegacoesAppService.RepositoriosDoContexto(origem.Contexto),
            await delegacoes.ContarDaOrigemAsync(origem.Id, cancellationToken),
            await delegacoes.ContarDaRaizAsync(origem.IdExecucaoRaiz, cancellationToken)), numero, agora);
        var configuracao = versaoDestinataria.Configuracao;
        var etapaDaSubordinada = new EtapaDaExecucao(subordinada, 1, TipoDeEtapa.TurnoDeAgente, false,
            PoliticaDeRetomada.Automatica, agora, configuracao.Driver, configuracao.Modelo, configuracao.Esforco);
        await execucoes.AdicionarAsync(subordinada, cancellationToken);
        await etapas.AdicionarAsync(etapaDaSubordinada, cancellationToken);
        await delegacoes.AdicionarAsync(delegacao, cancellationToken);
        await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(subordinada, TipoDeEventoDeOrquestracao.ExecucaoCriada,
            Json(new()
            {
                ["tipo"] = subordinada.Tipo.ToString(),
                ["agente"] = destinatario.Handle,
                ["versao"] = Numero(versaoDestinataria.Numero),
                ["origem"] = origem.Codigo,
                ["unidade"] = etapaDaUnidade.Unidade
            }), agora), cancellationToken);
        await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(origem, TipoDeEventoDeOrquestracao.DelegacaoSolicitada,
            CicloDaDelegacao.Dados(delegacao, new() { ["codigo"] = subordinada.Codigo, ["unidade"] = etapaDaUnidade.Unidade }),
            agora, etapaDaUnidade), cancellationToken);
        // A delegação nasce Solicitada no banco; aceitar é gravar o trabalho da fila, na mesma transação.
        await unidade.SalvarAlteracoesAsync(cancellationToken);
        delegacao.Aceitar(agora);
        await CicloDaDelegacao.TransicaoDaDelegacaoAsync(Ciclo, delegacao, EstadoDaDelegacao.Solicitada, agora, cancellationToken);
        await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(subordinada, etapaDaSubordinada,
            $"delegacao:{delegacao.Id:N}", agora), cancellationToken);
        return subordinada.Codigo;
    }

    // Por que a próxima revisão existiria: nenhuma ainda (inicial); a última aprovada (unidade sem resposta); a última
    // aguardando com a correção pedida no gate (pedido humano). Qualquer outro estado não admite revisão.
    private async Task<(MotivoDoPlano Motivo, PedidoDeAprovacao? PedidoHumano)> MotivoDaRevisaoAsync(
        PlanoDaCoordenacao? anterior, CancellationToken cancellationToken)
    {
        if (anterior is null) return (MotivoDoPlano.Inicial, null);
        if (anterior.Estado == EstadoDoPlano.Aprovado) return (MotivoDoPlano.UnidadeSemResposta, null);
        var pedido = (await pedidos.ListarDaExecucaoAsync(anterior.IdExecucao, cancellationToken))
            .LastOrDefault(p => p.IdPlano == anterior.Id);
        if (anterior.Estado == EstadoDoPlano.AguardandoAprovacao && pedido?.Estado == EstadoDoPedidoDeAprovacao.CorrecaoSolicitada)
            return (MotivoDoPlano.PedidoHumano, pedido);
        throw new InvalidOperationException($"O plano da revisão {anterior.Revisao} ({anterior.Estado}) não admite outra revisão.");
    }

    private async Task<IReadOnlyList<VersaoDoAgente>> VersoesAsync(IReadOnlyList<ParticipanteDaExecucao> linhas,
        CancellationToken cancellationToken)
    {
        var versoes = new List<VersaoDoAgente>();
        foreach (var participante in linhas)
            if (await versoesDeAgente.ObterPorIdAsync(participante.IdVersaoDoAgente, cancellationToken) is { } versao)
                versoes.Add(versao);
        return versoes;
    }

    private async Task<VersaoDoAgente> VersaoAsync(ParticipanteDaExecucao participante, CancellationToken cancellationToken) =>
        await versoesDeAgente.ObterPorIdAsync(participante.IdVersaoDoAgente, cancellationToken) ??
        throw new InvalidOperationException($"Versão do agente {participante.HandleDoAgente} não registrada.");

    private async Task<ConfiguracaoDoAgente> ConfiguracaoDoCoordenadorAsync(Execucao execucao,
        CancellationToken cancellationToken) =>
        (await versoesDeAgente.ObterPorIdAsync(execucao.IdVersaoDoAgente!.Value, cancellationToken) ??
         throw new InvalidOperationException("Versão do coordenador não registrada.")).Configuracao;

    private static EtapaDaExecucao Planejamento(Execucao execucao, int numero, ConfiguracaoDoAgente coordenador,
        DateTimeOffset agora) =>
        new(execucao, numero, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica, agora, coordenador.Driver,
            coordenador.Modelo, coordenador.Esforco);

    private static EtapaDaExecucao Avanco(Execucao execucao, int numero, DateTimeOffset agora) =>
        new(execucao, numero, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica, agora);

    private async Task<int> ProximoNumeroAsync(Guid idExecucao, CancellationToken cancellationToken) =>
        (await etapas.ListarDaExecucaoAsync(idExecucao, cancellationToken)).Max(e => e.Numero) + 1;

    private async Task IniciarAsync(Execucao execucao, EtapaDaExecucao etapa, DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        if (execucao.Estado == EstadoDaExecucao.Pendente)
        {
            execucao.Iniciar(agora);
            await Transicao(execucao, null, EstadoDaExecucao.Pendente, execucao.Estado, agora, cancellationToken);
        }
        var antes = etapa.Estado;
        if (etapa.Estado == EstadoDaEtapa.Pendente) etapa.TornarPronta([]);
        etapa.Iniciar(agora);
        await Transicao(execucao, etapa, antes, etapa.Estado, agora, cancellationToken);
    }

    // Execução travada com os ancestrais (a mesma ordem das delegações) e a etapa dela, de uma Coordenada.
    private async Task<(Execucao Execucao, EtapaDaExecucao Etapa)> CarregarAsync(Guid idEtapa,
        CancellationToken cancellationToken)
    {
        var idExecucao = (await etapas.ObterPorIdAsync(idEtapa, cancellationToken) ??
            throw new InvalidOperationException("Etapa não registrada no ledger.")).IdExecucao;
        var execucao = await execucoes.TravarComAncestraisAsync(idExecucao, cancellationToken) ??
            throw new InvalidOperationException("Execução não registrada no ledger.");
        if (execucao.Tipo != TipoDeExecucao.Coordenada)
            throw new InvalidOperationException("Só uma etapa de Coordenada é conduzida aqui.");
        var etapa = await etapas.ObterPorIdAsync(idEtapa, cancellationToken) ??
            throw new InvalidOperationException("Etapa não registrada no ledger.");
        return (execucao, etapa);
    }

    private async Task<Execucao?> LocalizarAsync(ProprietarioDaExecucao proprietario, string codigo,
        CancellationToken cancellationToken)
    {
        if (!Execucao.TentarInterpretarCodigo(codigo, out var numero, out var avulsa) || avulsa) return null;
        var encontrada = await execucoes.ConsultarPorNumeroAsync(numero, cancellationToken);
        return encontrada is { Tipo: TipoDeExecucao.Coordenada } &&
            proprietario.EDono(encontrada.Canal, encontrada.IdDoUsuarioNoCanal) ? encontrada : null;
    }

    private async Task<string?> CodigoDaSessaoAsync(Guid? idSessao, CancellationToken cancellationToken) =>
        idSessao is { } id && await sessoes.ObterPorIdAsync(id, cancellationToken) is { } sessao ? sessao.Codigo : null;

    private async Task<bool> CancelamentoPedidoAsync(Guid idExecucao, CancellationToken cancellationToken) =>
        (await eventos.ConsultarDoTipoAsync([idExecucao], TipoDeEventoDeOrquestracao.CancelamentoSolicitado,
            cancellationToken)).Count > 0;

    // Repositórios: os do contexto congelado, dentro da política do grupo e das concessões de cada participante. Nada é
    // inferido do texto da tarefa. O workspace geral também vale (as unidades só leem).
    private static void ConferirContexto(ResolucaoDeGrupoDto grupo, ConfiguracaoDoGrupo configuracao, JobExecutionContext contexto)
    {
        if (contexto.Mode != JobExecutionMode.Repository || contexto.RepositoryAlias is null) return;
        var aliases = contexto.RepositoriosVinculados.Select(v => v.Alias).Prepend(contexto.RepositoryAlias)
            .Select(a => a.ToLowerInvariant()).ToArray();
        var permitidos = configuracao.Politica.RepositoriosPermitidos ?? [];
        if (permitidos.Count > 0 && aliases.FirstOrDefault(a => !permitidos.Contains(a)) is { } fora)
            throw new ArgumentException($"O grupo {grupo.Nome} não trabalha em {fora}. Repositórios do grupo: " +
                $"{string.Join(", ", permitidos)}.");
        foreach (var participante in grupo.Participantes)
            if (!MontagemDaTarefa.ReposPermitidos(participante.VersaoDoAgente.Configuracao, contexto))
                throw new ArgumentException(MontagemDaTarefa.RecusaDeRepositorio(participante.Handle,
                    participante.VersaoDoAgente.Configuracao, contexto));
    }

    private Task Transicao(Execucao execucao, EtapaDaExecucao? etapa, Enum de, Enum para, DateTimeOffset quando,
        CancellationToken cancellationToken) =>
        Equals(de, para) ? Task.CompletedTask : eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao,
            etapa is null ? TipoDeEventoDeOrquestracao.ExecucaoTransitou : TipoDeEventoDeOrquestracao.EtapaTransitou,
            Json(new() { ["de"] = de.ToString(), ["para"] = para.ToString() }), quando, etapa), cancellationToken);

    private static PedidoDeAprovacaoDto Dto(PedidoDeAprovacao pedido) => new(pedido.Id, pedido.Rodada, pedido.HashDoArtefato,
        pedido.Estado, pedido.IdDoDecisor, pedido.DecididoEm, pedido.Motivo, pedido.CriadoEm);

    private static string ChaveDoAvanco(Execucao execucao, EtapaDaExecucao etapa) =>
        $"coordenada:{execucao.Id:N}:{Numero(etapa.Numero)}";

    private static string Numero(int valor) => valor.ToString(CultureInfo.InvariantCulture);

    private static string Json(Dictionary<string, string?> dados) => JsonSerializer.Serialize(dados);
}
