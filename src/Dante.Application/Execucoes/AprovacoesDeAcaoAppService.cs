using System.Globalization;
using System.Text.Json;
using Dante.Application.Agentes;
using Dante.Application.Comum;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Caso de uso das aprovações de ação protegida (AD-72, #311, PR 2). O domínio (AprovacaoDeAcao, PayloadDaAcao) garante
// as transições, o hash e a decisão; o banco confere de novo (trigger). Aqui: catálogo e política, identidade do
// solicitante, dono da raiz, revogação, CAS da execução e o resumo redigido. Nenhuma interface decide nada disso.
public sealed class AprovacoesDeAcaoAppService(
    IAprovacaoDeAcaoRepository aprovacoes,
    IExecucaoRepository execucoes,
    IEtapaDaExecucaoRepository etapas,
    IVersaoDoAgenteRepository versoes,
    IAgenteRepository agentes,
    IUnitOfWorkDaOrquestracao unidade,
    TimeProvider? relogio = null,
    IEnumerable<IObservadorDeAcaoProtegida>? observadores = null) : IAprovacoesDeAcaoAppService
{
    public const int LimiteDaManutencao = 100;
    public const int MaximoDoValorExibido = 300;
    public const string MotivoDoReinicio =
        "O processo foi interrompido durante a execução; o efeito não é conhecido e a ação não é repetida.";
    public const string MotivoDaFalhaInesperada =
        "A execução foi interrompida por uma falha; o efeito não é conhecido e a ação não é repetida.";
    public const string MotivoDoPayloadIrreproduzivel =
        "Não foi possível remontar o payload atual para conferir o hash; nada foi executado.";

    private readonly TimeProvider relogio = relogio ?? TimeProvider.System;
    // Quem conhece o efeito de cada ação descreve o estado real de um Incerto (#311, PR 3); sem observador, nenhum.
    private readonly IReadOnlyList<IObservadorDeAcaoProtegida> observadores = observadores?.ToArray() ?? [];

    public async Task<PedidoDeAcaoDto> SolicitarAsync(SolicitacaoDeAcaoDto solicitacao,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solicitacao);
        // Catálogo fechado e política: o desconhecido não existe; o que não exige aprovação não tem pedido.
        var acao = CatalogoDeAcoes.Obter(solicitacao.Acao) ?? throw new InvalidOperationException("Ação fora do catálogo.");
        if (!acao.Protegida) throw new InvalidOperationException("Esta ação não exige aprovação; não há pedido a criar.");
        var execucao = await execucoes.ObterPorIdAsync(solicitacao.IdExecucao, cancellationToken) ??
            throw new InvalidOperationException("Execução não encontrada.");
        var raiz = execucao.Raiz ? execucao
            : await execucoes.ObterPorIdAsync(execucao.IdExecucaoRaiz, cancellationToken) ??
              throw new InvalidOperationException("Execução não encontrada.");
        EtapaDaExecucao? etapa = null;
        if (solicitacao.IdEtapa is { } idEtapa &&
            ((etapa = await etapas.ObterPorIdAsync(idEtapa, cancellationToken)) is null || etapa.IdExecucao != execucao.Id))
            throw new InvalidOperationException("Etapa não encontrada nesta execução.");

        SolicitanteDaAcao solicitante;
        if (solicitacao.Dono is { } dono)
        {
            // Execução de outro dono é indistinguível de inexistente.
            if (!dono.EDono(raiz.Canal, raiz.IdDoUsuarioNoCanal)) throw new InvalidOperationException("Execução não encontrada.");
            solicitante = SolicitanteDaAcao.Dono(dono.Canal, dono.IdDoUsuario);
        }
        else
        {
            // Agente: a versão congelada da etapa (passo de grupo) ou, sem ela, a da execução (Tarefa), nunca um
            // argumento; ativo e ainda com essa versão.
            if (etapa is null || (etapa.IdVersaoDoAgente ?? execucao.IdVersaoDoAgente) is not { } idVersao)
                throw new InvalidOperationException("O solicitante agente é identificado pela etapa da sessão.");
            var versao = await versoes.ObterPorIdAsync(idVersao, cancellationToken) ??
                throw new InvalidOperationException("Versão do agente não encontrada.");
            if (await RevogacaoAsync(versao.Id, cancellationToken) is { } motivo) throw new InvalidOperationException(motivo);
            solicitante = SolicitanteDaAcao.Agente(versao.Id, versao.HashDaConfiguracao);
        }

        // Montar recusa o que não confere (parâmetro, alvo, capacidade) com o motivo.
        var payload = PayloadDaAcao.Montar(acao, execucao.Id, etapa?.Id, solicitacao.Alvo, solicitacao.Capacidades ?? [],
            solicitante, solicitacao.ParametrosJson);
        // Replay: o mesmo payload vigente devolve o pedido existente, nunca um segundo.
        if (await aprovacoes.ObterVigentePorHashAsync(payload.Hash, cancellationToken) is { } existente)
            return new PedidoDeAcaoDto(await DtoAsync(existente, cancellationToken), Novo: false);
        var pedido = AprovacaoDeAcao.Solicitar(payload, execucao, raiz, etapa, relogio.GetUtcNow(), solicitacao.Validade);
        await aprovacoes.AdicionarAsync(pedido, cancellationToken);
        try
        {
            await unidade.SalvarAlteracoesAsync(cancellationToken);
        }
        catch (ConflitoDeConcorrenciaException)
        {
            // Outro pedido do mesmo payload gravou primeiro (índice único): devolve aquele.
            if (await aprovacoes.ObterVigentePorHashAsync(payload.Hash, cancellationToken) is { } vencedor)
                return new PedidoDeAcaoDto(await DtoAsync(vencedor, cancellationToken), Novo: false);
            throw;
        }
        return new PedidoDeAcaoDto(await DtoAsync(pedido, cancellationToken), Novo: true);
    }

    public async Task<IReadOnlyList<AprovacaoDeAcaoDto>> ListarEmAbertoAsync(ProprietarioDaExecucao dono,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dono);
        var lista = await aprovacoes.ListarEmAbertoDoDonoAsync(dono.Canal, dono.IdDoUsuario, cancellationToken);
        var dtos = new List<AprovacaoDeAcaoDto>(lista.Count);
        foreach (var aprovacao in lista) dtos.Add(await DtoAsync(aprovacao, cancellationToken));
        return dtos;
    }

    public async Task<AprovacaoDeAcaoDto?> ObterAsync(ProprietarioDaExecucao dono, Guid id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dono);
        return await DoDonoAsync(dono, id, cancellationToken) is { } aprovacao ? await DtoAsync(aprovacao, cancellationToken) : null;
    }

    public async Task<DecisaoDeAcaoDto> AprovarAsync(ProprietarioDaExecucao decisor, Guid id, string? hashConfirmado,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decisor);
        if (await DoDonoAsync(decisor, id, cancellationToken) is not { } aprovacao)
            return new DecisaoDeAcaoDto(DesfechoDaDecisaoDeAcao.NaoEncontrada, null);
        if (aprovacao.Estado != EstadoDaAprovacaoDeAcao.Pendente)
            return new DecisaoDeAcaoDto(DesfechoDaDecisaoDeAcao.JaDecidida, await DtoAsync(aprovacao, cancellationToken));
        var agora = relogio.GetUtcNow();
        DesfechoDaDecisaoDeAcao desfecho;
        if (agora >= aprovacao.ExpiraEm)
        {
            aprovacao.Expirar(agora);
            desfecho = DesfechoDaDecisaoDeAcao.Expirada;
        }
        else if (aprovacao.IdVersaoDoSolicitante is { } versao && await RevogacaoAsync(versao, cancellationToken) is { } motivo)
        {
            aprovacao.Cancelar(motivo, agora);
            desfecho = DesfechoDaDecisaoDeAcao.Cancelada;
        }
        else
        {
            // Hash diferente do exibido: InvalidOperationException do domínio, com a mensagem para a pessoa.
            aprovacao.Aprovar(decisor.Canal, decisor.IdDoUsuario, hashConfirmado, agora);
            desfecho = DesfechoDaDecisaoDeAcao.Aprovada;
        }
        return await GravarDecisaoAsync(aprovacao, desfecho, cancellationToken);
    }

    public async Task<DecisaoDeAcaoDto> RecusarAsync(ProprietarioDaExecucao decisor, Guid id, string? motivo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decisor);
        if (await DoDonoAsync(decisor, id, cancellationToken) is not { } aprovacao)
            return new DecisaoDeAcaoDto(DesfechoDaDecisaoDeAcao.NaoEncontrada, null);
        if (aprovacao.Estado != EstadoDaAprovacaoDeAcao.Pendente)
            return new DecisaoDeAcaoDto(DesfechoDaDecisaoDeAcao.JaDecidida, await DtoAsync(aprovacao, cancellationToken));
        aprovacao.Recusar(decisor.Canal, decisor.IdDoUsuario, ProtecaoDeSegredos.Redigir(motivo), relogio.GetUtcNow());
        return await GravarDecisaoAsync(aprovacao, DesfechoDaDecisaoDeAcao.Recusada, cancellationToken);
    }

    public async Task<ExecucaoDeAcaoDto> ExecutarAsync(Guid id, ExecucaoDaAcaoProtegida execucao,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        if (await aprovacoes.ObterPorIdAsync(id, cancellationToken) is not { } aprovacao)
            return new ExecucaoDeAcaoDto(ResultadoDaExecucaoDeAcao.NaoEncontrada, null);
        // Só a ação aprovada, e só pelo executor dela.
        if (!ReferenceEquals(CatalogoDeAcoes.Obter(aprovacao.Acao), execucao.Acao) ||
            aprovacao.Estado != EstadoDaAprovacaoDeAcao.Aprovada)
            return new ExecucaoDeAcaoDto(ResultadoDaExecucaoDeAcao.NaoAutorizada, await DtoAsync(aprovacao, cancellationToken));

        var agora = relogio.GetUtcNow();
        ResultadoDaExecucaoDeAcao? encerrada = null;
        if (agora >= aprovacao.ExpiraEm)
        {
            aprovacao.Expirar(agora);
            encerrada = ResultadoDaExecucaoDeAcao.Expirada;
        }
        else if (aprovacao.IdVersaoDoSolicitante is { } versao && await RevogacaoAsync(versao, cancellationToken) is { } motivo)
        {
            aprovacao.Cancelar(motivo, agora);
            encerrada = ResultadoDaExecucaoDeAcao.Revogada;
        }
        else
        {
            PayloadDaAcao? atual;
            try
            {
                atual = await execucao.RemontarPayload(aprovacao, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Sem payload atual não há conferência: cancela sem executar (fail-closed).
                atual = null;
            }
            if (atual is null)
            {
                aprovacao.Cancelar(MotivoDoPayloadIrreproduzivel, agora);
                encerrada = ResultadoDaExecucaoDeAcao.Revogada;
            }
            else
            {
                var inicio = aprovacao.IniciarExecucao(atual, agora);
                encerrada = inicio switch
                {
                    InicioDaExecucaoDaAcao.Expirada => ResultadoDaExecucaoDeAcao.Expirada,
                    InicioDaExecucaoDaAcao.PayloadAlterado => ResultadoDaExecucaoDeAcao.PayloadAlterado,
                    _ => null
                };
            }
        }
        // Gravar a transição é o CAS: quem perde não executa.
        try
        {
            await unidade.SalvarAlteracoesAsync(cancellationToken);
        }
        catch (ConflitoDeConcorrenciaException)
        {
            return new ExecucaoDeAcaoDto(ResultadoDaExecucaoDeAcao.JaIniciada, null);
        }
        if (encerrada is { } semExecutar)
            return new ExecucaoDeAcaoDto(semExecutar, await DtoAsync(aprovacao, cancellationToken));

        // Iniciada e gravada: daqui em diante, interrupção sem desfecho é Incerta (aqui ou na recuperação).
        DesfechoDaAcaoProtegida? desfecho;
        try
        {
            desfecho = await execucao.Executar(aprovacao, cancellationToken);
        }
        catch (Exception)
        {
            desfecho = null;
        }
        desfecho ??= new DesfechoDaAcaoProtegida.Incerta(MotivoDaFalhaInesperada, await ObservarAsync(aprovacao));
        var fim = relogio.GetUtcNow();
        var resultado = desfecho switch
        {
            DesfechoDaAcaoProtegida.Executada executada => Encerrar(() => aprovacao.Concluir(
                ProtecaoDeSegredos.Redigir(executada.Resultado), fim), ResultadoDaExecucaoDeAcao.Executada),
            DesfechoDaAcaoProtegida.SemEfeito semEfeito => Encerrar(() => aprovacao.Falhar(
                Motivo(semEfeito.Motivo, "A ação não teve efeito."), fim), ResultadoDaExecucaoDeAcao.Falhou),
            DesfechoDaAcaoProtegida.Incerta incerta => Encerrar(() => aprovacao.MarcarIncerta(
                Motivo(incerta.Motivo, MotivoDaFalhaInesperada), ProtecaoDeSegredos.Redigir(incerta.EstadoObservado), fim),
                ResultadoDaExecucaoDeAcao.Incerta),
            _ => Encerrar(() => aprovacao.MarcarIncerta(MotivoDaFalhaInesperada, null, fim), ResultadoDaExecucaoDeAcao.Incerta)
        };
        // O desfecho é gravado mesmo com o pedido cancelado: a ação já rodou. Falha aqui deixa EmExecucao, e a
        // recuperação a marca Incerta.
        await unidade.SalvarAlteracoesAsync(CancellationToken.None);
        return new ExecucaoDeAcaoDto(resultado, await DtoAsync(aprovacao, CancellationToken.None));
    }

    public async Task<IReadOnlyList<AprovacaoDeAcaoDto>> ExpirarVencidasAsync(CancellationToken cancellationToken = default)
    {
        var agora = relogio.GetUtcNow();
        var expiradas = new List<AprovacaoDeAcaoDto>();
        foreach (var aprovacao in await aprovacoes.ListarVencidasAsync(agora, LimiteDaManutencao, cancellationToken))
        {
            aprovacao.Expirar(agora);
            // Uma decisão concorrente venceu: o restante fica para a próxima rodada, em outro escopo.
            if (!await GravouAsync(cancellationToken)) break;
            expiradas.Add(await DtoAsync(aprovacao, cancellationToken));
        }
        return expiradas;
    }

    public async Task<ManutencaoDeAprovacoesDto> RecuperarAposReinicioAsync(CancellationToken cancellationToken = default)
    {
        var incertas = new List<AprovacaoDeAcaoDto>();
        foreach (var aprovacao in await aprovacoes.ListarEmExecucaoAsync(LimiteDaManutencao, cancellationToken))
        {
            aprovacao.MarcarIncerta(MotivoDoReinicio, await ObservarAsync(aprovacao), relogio.GetUtcNow());
            if (!await GravouAsync(cancellationToken)) break;
            incertas.Add(await DtoAsync(aprovacao, cancellationToken));
        }
        return new ManutencaoDeAprovacoesDto(await ExpirarVencidasAsync(cancellationToken), incertas);
    }

    // ---- apoio ----

    // Estado real observado de uma execução que ficou Incerta, redigido; nulo sem observador ou se a leitura falhar.
    private async Task<string?> ObservarAsync(AprovacaoDeAcao aprovacao)
    {
        var definicao = CatalogoDeAcoes.Obter(aprovacao.Acao);
        if (observadores.FirstOrDefault(o => ReferenceEquals(o.Acao, definicao)) is not { } observador) return null;
        try
        {
            return ProtecaoDeSegredos.Redigir(await observador.ObservarAsync(aprovacao, CancellationToken.None));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Motivo(string? motivo, string padrao) =>
        string.IsNullOrWhiteSpace(motivo) ? padrao : ProtecaoDeSegredos.Redigir(motivo)!;

    private static ResultadoDaExecucaoDeAcao Encerrar(Action transicao, ResultadoDaExecucaoDeAcao resultado)
    {
        transicao();
        return resultado;
    }

    private async Task<DecisaoDeAcaoDto> GravarDecisaoAsync(AprovacaoDeAcao aprovacao, DesfechoDaDecisaoDeAcao desfecho,
        CancellationToken cancellationToken)
    {
        // Duas decisões simultâneas: só uma grava (xmin); a outra vê o pedido já decidido.
        if (!await GravouAsync(cancellationToken)) return new DecisaoDeAcaoDto(DesfechoDaDecisaoDeAcao.JaDecidida, null);
        return new DecisaoDeAcaoDto(desfecho, await DtoAsync(aprovacao, cancellationToken));
    }

    private async Task<bool> GravouAsync(CancellationToken cancellationToken)
    {
        try
        {
            await unidade.SalvarAlteracoesAsync(cancellationToken);
            return true;
        }
        catch (ConflitoDeConcorrenciaException)
        {
            return false;
        }
    }

    private async Task<AprovacaoDeAcao?> DoDonoAsync(ProprietarioDaExecucao dono, Guid id, CancellationToken cancellationToken) =>
        await aprovacoes.ObterPorIdAsync(id, cancellationToken) is { } aprovacao &&
        dono.EDono(aprovacao.CanalDoDono, aprovacao.IdDoDono)
            ? aprovacao : null;

    // Concessão revogada: o agente não está ativo, ou a versão atual dele não é mais a que pediu (uma versão nova muda
    // o que foi concedido). Nulo quando continua valendo.
    private async Task<string?> RevogacaoAsync(Guid idVersao, CancellationToken cancellationToken)
    {
        if (await versoes.ObterPorIdAsync(idVersao, cancellationToken) is not { } versao ||
            await agentes.ObterPorIdAsync(versao.IdAgente, cancellationToken) is not { } agente ||
            agente.Estado != EstadoDoAgente.Ativo)
            return "O agente solicitante foi desativado ou removido; a ação não é autorizada.";
        return agente.IdVersaoAtual == versao.Id ? null
            : "O agente solicitante tem outra versão (concessões alteradas); a ação não é autorizada.";
    }

    private async Task<AprovacaoDeAcaoDto> DtoAsync(AprovacaoDeAcao aprovacao, CancellationToken cancellationToken)
    {
        var definicao = CatalogoDeAcoes.Obter(aprovacao.Acao);
        var execucao = await execucoes.ObterPorIdAsync(aprovacao.IdExecucao, cancellationToken);
        var solicitante = aprovacao.IdVersaoDoSolicitante is { } versao
            ? $"agente (versão {versao.ToString("N", CultureInfo.InvariantCulture)[..8]})" : "dono";
        return new AprovacaoDeAcaoDto(aprovacao.Id, aprovacao.Acao, definicao?.Descricao ?? aprovacao.Acao, aprovacao.Risco,
            aprovacao.Alvo, execucao?.Codigo, solicitante, aprovacao.CanalDoDono, aprovacao.IdDoDono, aprovacao.Hash, aprovacao.Estado, aprovacao.CriadaEm,
            aprovacao.ExpiraEm, aprovacao.DecididaEm, aprovacao.Motivo, aprovacao.Resultado, Parametros(aprovacao.Payload));
    }

    // Resumo redigido: credencial vira marcador e o valor é cortado. O payload bruto nunca sai daqui.
    internal static IReadOnlyList<ParametroExibidoDto> Parametros(string payload)
    {
        using var documento = JsonDocument.Parse(payload);
        if (!documento.RootElement.TryGetProperty("parametros", out var parametros) ||
            parametros.ValueKind != JsonValueKind.Object)
            return [];
        var lista = new List<ParametroExibidoDto>();
        foreach (var parametro in parametros.EnumerateObject())
        {
            var valor = ProtecaoDeSegredos.Redigir(parametro.Value.GetString()) ?? "";
            lista.Add(new ParametroExibidoDto(parametro.Name,
                valor.Length <= MaximoDoValorExibido ? valor : valor[..MaximoDoValorExibido] + "…"));
        }
        return lista;
    }
}
