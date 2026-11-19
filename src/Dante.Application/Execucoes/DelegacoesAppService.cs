using System.Text.Json;
using Dante.Application.Agentes;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

public sealed class DelegacoesAppService(
    IFilaDeTrabalho fila,
    IDelegacaoRepository delegacoes,
    IExecucaoRepository execucoes,
    IEtapaDaExecucaoRepository etapas,
    IEventoDeOrquestracaoRepository eventos,
    ISessaoDeAgenteRepository sessoes,
    IAgenteRepository agentes,
    IVersaoDoAgenteRepository versoes,
    IControleDeExecucoesAppService controle,
    IUnitOfWorkDaOrquestracao unidade,
    Dante.Application.Mapeamento.IMapsterTypeAdapter mapper,
    ICoordenacaoDaExecucaoRepository coordenacoes,
    IParticipanteDaExecucaoRepository participantes,
    IPlanoDaCoordenacaoRepository planos) : IDelegacoesAppService
{
    public const string OrigemNaoEncontrada = "Execução de origem não encontrada.";
    public const string RevisaoSemArtefatoVerificavel =
        "revisao.v1 exige um artefato que o executor confira pelo hash, o que ainda não existe para uma delegação; " +
        "use consulta.v1.";

    private CicloDaDelegacao.Servicos Ciclo => new(delegacoes, execucoes, etapas, eventos, fila);

    public const string SessaoNaoAutorizada = "A sessão não está autorizada a delegar por esta etapa.";

    public Task<DelegacaoSolicitadaDto> SolicitarAsync(SolicitacaoDeDelegacaoDto solicitacao,
        CancellationToken cancellationToken = default) =>
        SolicitarAsync(solicitacao, null, null, cancellationToken);

    public Task<DelegacaoSolicitadaDto> SolicitarSobPosseAsync(PosseDoTrabalho posse, long numeroDaSessao,
        SolicitacaoDeDelegacaoDto solicitacao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(posse);
        return SolicitarAsync(solicitacao, posse, numeroDaSessao, cancellationToken);
    }

    // Sem posse: transação comum (caso de uso interno). Com posse (dante_coordenacao): a gravação é a da posse do
    // trabalho da origem, na ordem trabalho → ancestrais → execução, e só da sessão vinculada à etapa de origem.
    private async Task<DelegacaoSolicitadaDto> SolicitarAsync(SolicitacaoDeDelegacaoDto solicitacao, PosseDoTrabalho? posse,
        long? numeroDaSessao, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solicitacao);
        ArgumentNullException.ThrowIfNull(solicitacao.Proprietario);
        if (solicitacao.IdExecucaoOrigem == Guid.Empty || solicitacao.IdEtapaOrigem == Guid.Empty)
            throw new ArgumentException("Execução e etapa de origem são obrigatórias.");
        if (string.IsNullOrWhiteSpace(solicitacao.Destinatario) || string.IsNullOrWhiteSpace(solicitacao.Entrada))
            throw new ArgumentException("Destinatário e entrada são obrigatórios.");
        if (solicitacao.Contrato == ContratosDeDelegacao.Revisao.Identificador)
            throw Recusa(MotivoDaRecusaDeDelegacao.ContratoInvalido, RevisaoSemArtefatoVerificavel);
        // O texto que o agente manda é dado: com aparência de credencial, a entrada é recusada (nunca gravada) e o
        // objetivo é gravado só redigido, como o da origem.
        if (ProtecaoDeSegredos.ContemSegredo(solicitacao.Entrada) || ProtecaoDeSegredos.ContemSegredo(solicitacao.Criterios))
            throw Recusa(MotivoDaRecusaDeDelegacao.ContratoInvalido,
                "Entrada ou critérios com aparência de credencial; use uma referência, nunca o valor.");
        // Número reservado antes, na transação curta da própria sequência: reservar dentro desta prenderia a trava global
        // da sequência até o commit. Repetição ou recusa deixam só uma lacuna na numeração.
        var numero = await execucoes.ReservarNumeroAsync(cancellationToken: cancellationToken);
        DelegacaoSolicitadaDto? resultado = null;
        async Task Gravar(CancellationToken token)
        {
            // Serialização por raiz: a raiz (e a origem, abaixo dela) fica travada até o commit, antes de ler a unidade,
            // contar e inserir. Nada da origem é lido antes desta trava (o contexto não guarda uma cópia antiga).
            var origem = await execucoes.TravarComAncestraisAsync(solicitacao.IdExecucaoOrigem, token);
            if (origem is null || !solicitacao.Proprietario.EDono(origem.Canal, origem.IdDoUsuarioNoCanal))
                throw Recusa(MotivoDaRecusaDeDelegacao.OrigemInvalida, OrigemNaoEncontrada);
            var etapa = await etapas.ObterPorIdAsync(solicitacao.IdEtapaOrigem, token);
            if (etapa is null || etapa.IdExecucao != origem.Id)
                throw Recusa(MotivoDaRecusaDeDelegacao.OrigemInvalida, OrigemNaoEncontrada);
            // Pela sessão: só a vinculada sob posse a esta etapa (#305), do mesmo dono e canal da origem.
            if (numeroDaSessao is { } numeroInformado &&
                (await sessoes.ObterPorNumeroAsync(numeroInformado, token) is not { } sessao ||
                 etapa.IdSessaoDeAgente != sessao.Id || sessao.Canal != origem.Canal ||
                 sessao.IdDoDono != origem.IdDoUsuarioNoCanal))
                throw Recusa(MotivoDaRecusaDeDelegacao.OrigemInvalida, SessaoNaoAutorizada);
            var (solicitante, versaoSolicitante) = await SolicitanteAsync(origem, token);
            var destinatario = await agentes.ObterPorHandleAsync(solicitacao.Destinatario.Trim().ToLowerInvariant(), token) ??
                throw Recusa(MotivoDaRecusaDeDelegacao.DestinatarioIndisponivel,
                    $"O agente {solicitacao.Destinatario.Trim()} não existe.");
            if (Chave(origem, etapa, solicitacao) is { } chave &&
                await delegacoes.ObterPorChaveAsync(origem.IdExecucaoRaiz, chave, token) is { } existente)
            {
                existente.ConferirRepeticao(destinatario.Id);
                resultado = new(await DtoAsync(existente, token), false);
                return;
            }
            var versaoDestinataria = destinatario.IdVersaoAtual is { } idAtual
                ? await versoes.ObterPorIdAsync(idAtual, token) : null;
            if (versaoDestinataria is null)
                throw Recusa(MotivoDaRecusaDeDelegacao.DestinatarioIndisponivel,
                    $"O agente {destinatario.Handle} não está ativo com uma versão íntegra.");
            var objetivo = solicitacao.Objetivo?.Trim() ?? "";
            var protegido = ProtecaoDeSegredos.ContemSegredo(objetivo);
            var redigido = protegido ? RoteirosAppService.ObjetivoProtegido
                : objetivo.Length <= Execucao.TamanhoMaximoDoObjetivo ? objetivo : objetivo[..Execucao.TamanhoMaximoDoObjetivo];
            var agora = fila.Agora;
            // As regras são todas do domínio, com as contagens lidas sob a trava da raiz.
            var nova = Delegacao.Criar(new PedidoDeDelegacao(origem, etapa, solicitante, versaoSolicitante, destinatario,
                versaoDestinataria, await CadeiaAsync(origem, token), RepositoriosDoContexto(origem.Contexto),
                await delegacoes.ContarDaOrigemAsync(origem.Id, token), await delegacoes.ContarDaRaizAsync(origem.IdExecucaoRaiz, token),
                solicitacao.Contrato, solicitacao.Entrada, objetivo, redigido, protegido, solicitacao.Criterios,
                solicitacao.MinutosParaIniciar, solicitacao.MinutosDeDuracao, solicitacao.Escrita), numero, agora);
            var (delegacao, subordinada) = nova;
            var configuracao = versaoDestinataria.Configuracao;
            // Só leitura (o domínio e o banco recusam escrita): uma etapa de turno, retomável automaticamente.
            var etapaDaSubordinada = new EtapaDaExecucao(subordinada, 1, TipoDeEtapa.TurnoDeAgente, false,
                PoliticaDeRetomada.Automatica, agora, configuracao.Driver, configuracao.Modelo, configuracao.Esforco);
            await execucoes.AdicionarAsync(subordinada, token);
            await etapas.AdicionarAsync(etapaDaSubordinada, token);
            await delegacoes.AdicionarAsync(delegacao, token);
            await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(subordinada, TipoDeEventoDeOrquestracao.ExecucaoCriada,
                JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["tipo"] = subordinada.Tipo.ToString(),
                    ["agente"] = destinatario.Handle,
                    ["versao"] = versaoDestinataria.Numero.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["origem"] = origem.Codigo
                }), agora), token);
            await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(origem, TipoDeEventoDeOrquestracao.DelegacaoSolicitada,
                CicloDaDelegacao.Dados(delegacao, new() { ["codigo"] = subordinada.Codigo }), agora, etapa), token);
            // A delegação nasce Solicitada no banco; aceitar é gravar o trabalho da fila, na mesma transação.
            await unidade.SalvarAlteracoesAsync(token);
            delegacao.Aceitar(agora);
            await CicloDaDelegacao.TransicaoDaDelegacaoAsync(Ciclo, delegacao, EstadoDaDelegacao.Solicitada, agora, token);
            await fila.EnfileirarAsync(TrabalhoDaFila.AvancarExecucao(subordinada, etapaDaSubordinada,
                $"delegacao:{delegacao.Id:N}", agora), token);
            await unidade.SalvarAlteracoesAsync(token);
            resultado = new(Dto(delegacao, subordinada), true);
        }
        if (posse is null) await fila.ExecutarEmTransacaoAsync(Gravar, cancellationToken);
        // O trabalho da origem continua reivindicado (encerrarComo nulo): só a conclusão da etapa o termina.
        else await fila.GravarComPosseAsync(posse, Gravar, null, cancellationToken: cancellationToken);
        return resultado!;
    }

    public async Task<IReadOnlyList<AcompanhamentoDaDelegacaoDto>> AcompanharDaOrigemAsync(
        ProprietarioDaExecucao proprietario, Guid idExecucaoOrigem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proprietario);
        var origem = await execucoes.ObterPorIdAsync(idExecucaoOrigem, cancellationToken);
        if (origem is null || !proprietario.EDono(origem.Canal, origem.IdDoUsuarioNoCanal)) return [];
        // O último marco de cada delegação, pelos eventos DelegacaoProgrediu gravados na origem (só metadados).
        var marcos = new Dictionary<string, MarcoDaDelegacao>();
        foreach (var evento in (await eventos.ConsultarDoTipoAsync([origem.Id], TipoDeEventoDeOrquestracao.DelegacaoProgrediu,
                     cancellationToken)).OrderBy(e => e.Sequencia))
        {
            var dados = JsonSerializer.Deserialize<Dictionary<string, string?>>(evento.Dados ?? "{}");
            if (dados is not null && dados.TryGetValue("delegacao", out var id) && id is not null &&
                dados.TryGetValue("marco", out var marco) && Enum.TryParse<MarcoDaDelegacao>(marco, out var valor))
                marcos[id] = valor;
        }
        var lista = new List<AcompanhamentoDaDelegacaoDto>();
        foreach (var delegacao in (await delegacoes.ListarDaOrigemAsync(origem.Id, cancellationToken)).OrderBy(d => d.CriadaEm))
            lista.Add(new(await DtoAsync(delegacao, cancellationToken),
                marcos.TryGetValue(delegacao.Id.ToString("N"), out var ultimo) ? ultimo : null));
        return lista;
    }

    public async Task<DelegacaoDto?> ObterAsync(ProprietarioDaExecucao proprietario, Guid idDelegacao,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proprietario);
        var delegacao = await delegacoes.ObterPorIdAsync(idDelegacao, cancellationToken);
        if (delegacao is null) return null;
        var subordinada = await execucoes.ObterPorIdAsync(delegacao.IdExecucaoSubordinada, cancellationToken);
        return subordinada is not null && proprietario.EDono(subordinada.Canal, subordinada.IdDoUsuarioNoCanal)
            ? Dto(delegacao, subordinada) : null;
    }

    public async Task<IReadOnlyList<DelegacaoDto>> ListarDaRaizAsync(ProprietarioDaExecucao proprietario,
        Guid idExecucaoRaiz, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proprietario);
        var raiz = await execucoes.ObterPorIdAsync(idExecucaoRaiz, cancellationToken);
        if (raiz is null || !raiz.Raiz || !proprietario.EDono(raiz.Canal, raiz.IdDoUsuarioNoCanal)) return [];
        var lista = new List<DelegacaoDto>();
        foreach (var delegacao in await delegacoes.ListarDaRaizAsync(idExecucaoRaiz, cancellationToken))
            lista.Add(await DtoAsync(delegacao, cancellationToken));
        return lista;
    }

    public async Task<CancelamentoDeExecucaoDto> CancelarAsync(ProprietarioDaExecucao proprietario, Guid idDelegacao,
        string? motivo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proprietario);
        if (await ObterAsync(proprietario, idDelegacao, cancellationToken) is not { } delegacao)
            return new CancelamentoDeExecucaoDto(ResultadoDoCancelamento.NaoEncontrada);
        // O mesmo cancelamento durável de qualquer execução: a delegação acompanha a subordinada (CicloDaDelegacao).
        return await controle.SolicitarCancelamentoAsync(proprietario, delegacao.CodigoDaSubordinada, motivo,
            cancellationToken);
    }

    public async Task<ConferenciaDaDelegacaoDto> ConferirAsync(Guid idExecucaoSubordinada,
        CancellationToken cancellationToken = default)
    {
        var subordinada = await execucoes.ObterPorIdAsync(idExecucaoSubordinada, cancellationToken);
        var delegacao = await delegacoes.ObterDaSubordinadaAsync(idExecucaoSubordinada, cancellationToken);
        if (subordinada is not { Tipo: TipoDeExecucao.Delegada } || delegacao is null)
            return new(null, "Execução delegada sem delegação registrada.");
        if (delegacao.Terminal) return new(null, $"A delegação já terminou ({delegacao.Estado}).");
        var origem = await execucoes.ObterPorIdAsync(delegacao.IdExecucaoOrigem, cancellationToken);
        var raiz = await execucoes.ObterPorIdAsync(delegacao.IdExecucaoRaiz, cancellationToken);
        // Autoridade e hierarquia: a subordinada é da origem e da raiz da delegação, no mesmo canal e com a mesma pessoa.
        if (origem is null || raiz is null || subordinada.IdExecucaoPai != origem.Id ||
            subordinada.IdExecucaoRaiz != delegacao.IdExecucaoRaiz || origem.IdExecucaoRaiz != raiz.Id ||
            subordinada.Canal != origem.Canal || subordinada.IdDoUsuarioNoCanal != origem.IdDoUsuarioNoCanal ||
            raiz.Canal != origem.Canal || raiz.IdDoUsuarioNoCanal != origem.IdDoUsuarioNoCanal ||
            subordinada.IdVersaoDoAgente != delegacao.IdVersaoDestinataria)
            return new(null, "A execução subordinada não confere com a origem, a raiz ou o proprietário da delegação.");
        var agora = fila.Agora;
        if (delegacao.Estado == EstadoDaDelegacao.Aceita && agora > delegacao.CriadaEm.AddMinutes(delegacao.MinutosParaIniciar))
            return new(null, $"Prazo para iniciar excedido ({delegacao.MinutosParaIniciar} min).", Expirada: true);
        if (delegacao.IniciadaEm is { } iniciada && agora > iniciada.AddMinutes(delegacao.MinutosDeDuracao))
            return new(null, $"Duração máxima excedida ({delegacao.MinutosDeDuracao} min).", Expirada: true);
        if (delegacao.Escrita) return new(null, "Delegação com escrita não é permitida.");
        // Destinatário: ativo, ainda na versão congelada, íntegra, com o hash do pedido e a capacidade do contrato. Numa
        // unidade de Coordenada (#310, D2) a autoridade é o snapshot: a versão congelada nele é a que roda, mesmo que o
        // agente tenha sido editado depois (editar não amplia; desativar restringe).
        var coordenada = origem.Tipo == TipoDeExecucao.Coordenada;
        var destinatario = await agentes.ObterPorIdAsync(delegacao.IdAgenteDestinatario, cancellationToken);
        var versao = await versoes.ObterPorIdAsync(delegacao.IdVersaoDestinataria, cancellationToken);
        if (destinatario is not { Estado: EstadoDoAgente.Ativo } ||
            (!coordenada && destinatario.IdVersaoAtual != delegacao.IdVersaoDestinataria))
            return new(null, $"O agente {delegacao.HandleDoDestinatario} não está mais ativo na versão da delegação.");
        if (versao is null || versao.IdAgente != destinatario.Id || !versao.Integra ||
            versao.HashDaConfiguracao != delegacao.HashDaVersaoDestinataria)
            return new(null, $"A versão do agente {delegacao.HandleDoDestinatario} não confere com o hash congelado.");
        if (!ContratosDeDelegacao.TentarObter(delegacao.Contrato, out var contrato) ||
            delegacao.Contrato == ContratosDeDelegacao.Revisao.Identificador)
            return new(null, "Contrato da delegação indisponível para execução.");
        if (!contrato.Aceita(versao.Configuracao.Capacidades))
            return new(null, $"O agente {delegacao.HandleDoDestinatario} não tem capacidade para {delegacao.Contrato}.");
        // Solicitante: a concessão de coordenação e o destinatário em PodeConsultar, na versão congelada e íntegra. Numa
        // unidade de Coordenada, a autorização é só a do plano aprovado e do snapshot (D2), nunca uma concessão do agente.
        var versaoSolicitante = await versoes.ObterPorIdAsync(delegacao.IdVersaoSolicitante, cancellationToken);
        if (versaoSolicitante is null || !versaoSolicitante.Integra ||
            versaoSolicitante.HashDaConfiguracao != delegacao.HashDaVersaoSolicitante ||
            (!coordenada && !versaoSolicitante.Configuracao.PodeDelegarPara(delegacao.HandleDoDestinatario)))
            return new(null, "A concessão de coordenação do solicitante não confere com a da delegação.");
        if (coordenada && await RecusaDoPlanoAsync(origem, delegacao, cancellationToken) is { } foraDoPlano)
            return new(null, foraDoPlano);
        // Entrada: a gravada é a canônica e tem o hash gravado (nada a adulterou).
        try
        {
            if (contrato.CanonizarEntrada(delegacao.Entrada) != delegacao.Entrada ||
                ContratoDeDelegacao.Hash(delegacao.Entrada) != delegacao.HashDaEntrada)
                return new(null, "A entrada da delegação não confere com o hash gravado.");
        }
        catch (ArgumentException)
        {
            return new(null, "A entrada da delegação não é válida pelo contrato.");
        }
        // Repositórios do contexto herdado ainda permitidos aos dois agentes (a interseção não amplia nada).
        foreach (var alias in RepositoriosDoContexto(subordinada.Contexto))
            if (!versao.Configuracao.PermiteRepositorio(alias) || !versaoSolicitante.Configuracao.PermiteRepositorio(alias))
                return new(null, $"O repositório {alias} não é permitido aos dois agentes.");
        return new(new ExecucaoDelegadaDto(Dto(delegacao, subordinada), origem.Codigo, subordinada.Canal,
            subordinada.IdDoUsuarioNoCanal, subordinada.Contexto, delegacao.ObjetivoRedigido, delegacao.Criterios,
            delegacao.Entrada, contrato.SchemaDeSaida().ToJsonString(), delegacao.MinutosDeDuracao,
            mapper.Mapear<VersaoDoAgente, VersaoDoAgenteDto>(versao)), null);
    }

    public async Task<DateTimeOffset> IniciarAsync(PosseDoTrabalho posse, Guid idEtapa,
        CancellationToken cancellationToken = default)
    {
        var limite = DateTimeOffset.MinValue;
        await fila.GravarComPosseAsync(posse, async token =>
        {
            var (subordinada, etapa, delegacao) = await CarregarAsync(idEtapa, token);
            if (await CancelamentoPedidoAsync(subordinada.Id, token)) throw new CancelamentoSolicitadoException();
            if (delegacao.Terminal) throw new InvalidOperationException("A delegação já terminou.");
            var agora = fila.Agora;
            if (delegacao.Estado == EstadoDaDelegacao.Aceita && agora > delegacao.CriadaEm.AddMinutes(delegacao.MinutosParaIniciar))
                throw new PrazoDaDelegacaoExcedidoException($"Prazo para iniciar excedido ({delegacao.MinutosParaIniciar} min).");
            if (delegacao.IniciadaEm is { } iniciada && agora > iniciada.AddMinutes(delegacao.MinutosDeDuracao))
                throw new PrazoDaDelegacaoExcedidoException($"Duração máxima excedida ({delegacao.MinutosDeDuracao} min).");
            if (etapa.Estado is not (EstadoDaEtapa.Pendente or EstadoDaEtapa.Pronta))
                throw new TransicaoInvalidaException("etapa", etapa.Estado.ToString(), EstadoDaEtapa.EmExecucao.ToString());
            await IniciarSubordinadaAsync(subordinada, etapa, agora, token);
            if (delegacao.Estado == EstadoDaDelegacao.Aceita)
            {
                delegacao.Iniciar(agora);
                await CicloDaDelegacao.TransicaoDaDelegacaoAsync(Ciclo, delegacao, EstadoDaDelegacao.Aceita, agora, token);
            }
            await unidade.SalvarAlteracoesAsync(token);
            limite = delegacao.IniciadaEm!.Value.AddMinutes(delegacao.MinutosDeDuracao);
        }, null, cancellationToken: cancellationToken);
        return limite;
    }

    public Task RegistrarProgressoAsync(PosseDoTrabalho posse, Guid idEtapa, MarcoDaDelegacao marco,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(marco)) throw new ArgumentOutOfRangeException(nameof(marco), "Marco inválido.");
        return fila.GravarComPosseAsync(posse, async token =>
        {
            var (_, etapa, delegacao) = await CarregarAsync(idEtapa, token);
            if (delegacao.Estado != EstadoDaDelegacao.EmAndamento || etapa.Estado != EstadoDaEtapa.EmExecucao)
                throw new InvalidOperationException("Só uma delegação em andamento registra progresso.");
            var origem = await execucoes.ObterPorIdAsync(delegacao.IdExecucaoOrigem, token) ??
                throw new InvalidOperationException("Execução de origem não registrada no ledger.");
            await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(origem, TipoDeEventoDeOrquestracao.DelegacaoProgrediu,
                CicloDaDelegacao.Dados(delegacao, new() { ["marco"] = marco.ToString() }), fila.Agora), token);
            await unidade.SalvarAlteracoesAsync(token);
        }, null, cancellationToken: cancellationToken);
    }

    public async Task<string?> ValidarResultadoAsync(Guid idExecucaoSubordinada, string resultado,
        CancellationToken cancellationToken = default)
    {
        var delegacao = await delegacoes.ObterDaSubordinadaAsync(idExecucaoSubordinada, cancellationToken) ??
            throw new InvalidOperationException("Execução delegada sem delegação registrada.");
        return Invalido(delegacao, resultado);
    }

    public async Task<DesfechoDaDelegacaoDto> ConcluirAsync(PosseDoTrabalho posse, Guid idEtapa,
        EntregaDaDelegacaoDto entrega, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrega);
        DesfechoDaDelegacaoDto? desfecho = null;
        await fila.GravarComPosseAsync(posse, async token =>
        {
            var (subordinada, etapa, delegacao) = await CarregarAsync(idEtapa, token);
            // Entrega repetida do mesmo conteúdo depois de gravado: nada muda. Outro conteúdo, ou qualquer resultado
            // depois de um estado terminal sem ele, é recusado: o original é imutável.
            if (delegacao.Terminal)
            {
                if (delegacao.Estado == EstadoDaDelegacao.Respondida && delegacao.MesmoResultado(entrega.Resultado))
                {
                    desfecho = new(delegacao.Estado, delegacao.Motivo, false);
                    return;
                }
                throw new InvalidOperationException("A delegação já terminou; o resultado não é aceito.");
            }
            var agora = fila.Agora;
            if (await CancelamentoPedidoAsync(subordinada.Id, token))
            {
                desfecho = await EncerrarAsync(subordinada, etapa, delegacao, EstadoDaDelegacao.Cancelada,
                    await MotivoDoPedidoAsync(subordinada.Id, token), agora, token);
                return;
            }
            if (delegacao.Estado != EstadoDaDelegacao.EmAndamento || etapa.Estado != EstadoDaEtapa.EmExecucao)
                throw new InvalidOperationException("Só uma delegação em andamento recebe o resultado.");
            if (agora > delegacao.IniciadaEm!.Value.AddMinutes(delegacao.MinutosDeDuracao))
            {
                desfecho = await EncerrarAsync(subordinada, etapa, delegacao, EstadoDaDelegacao.Expirada,
                    $"Duração máxima excedida ({delegacao.MinutosDeDuracao} min); o resultado chegou depois.", agora, token);
                return;
            }
            // Só a sessão vinculada sob posse a esta etapa (#305), do mesmo dono e canal, entrega o resultado.
            var sessao = await sessoes.ObterPorNumeroAsync(entrega.NumeroDaSessao, token);
            if (sessao is null || etapa.IdSessaoDeAgente != sessao.Id || sessao.Canal != subordinada.Canal ||
                sessao.IdDoDono != subordinada.IdDoUsuarioNoCanal)
                throw new InvalidOperationException("O resultado não veio da sessão autorizada para esta delegação.");
            if (Invalido(delegacao, entrega.Resultado) is { } erro) throw new ArgumentException(erro);
            delegacao.Responder(entrega.Resultado, delegacao.IdVersaoDestinataria, sessao.Id, entrega.HashDoArtefatoLido, agora);
            await CicloDaDelegacao.TransicaoDaDelegacaoAsync(Ciclo, delegacao, EstadoDaDelegacao.EmAndamento, agora, token);
            var etapaAntes = etapa.Estado;
            var subordinadaAntes = subordinada.Estado;
            etapa.Concluir(null, agora);
            subordinada.Concluir(agora);
            await TransicaoAsync(subordinada, etapa, etapaAntes, agora, token);
            await TransicaoAsync(subordinada, null, subordinadaAntes, agora, token);
            var retomada = await CicloDaDelegacao.RetomarSeProntaAsync(Ciclo, delegacao, agora, token);
            await unidade.SalvarAlteracoesAsync(token);
            desfecho = new(delegacao.Estado, null, retomada);
        }, EstadoDoTrabalho.Concluido, cancellationToken: cancellationToken);
        return desfecho!;
    }

    public Task<DesfechoDaDelegacaoDto> FalharAsync(PosseDoTrabalho posse, Guid idEtapa, string motivo,
        CancellationToken cancellationToken = default) =>
        EncerrarSobPosseAsync(posse, idEtapa, EstadoDaDelegacao.Falhou, motivo, cancellationToken);

    public Task<DesfechoDaDelegacaoDto> ExpirarAsync(PosseDoTrabalho posse, Guid idEtapa, string motivo,
        CancellationToken cancellationToken = default) =>
        EncerrarSobPosseAsync(posse, idEtapa, EstadoDaDelegacao.Expirada, motivo, cancellationToken);

    public async Task<RetomadaDaOrigemDto?> ObterRetomadaAsync(Guid idExecucao, Guid idEtapa,
        CancellationToken cancellationToken = default)
    {
        var origem = await execucoes.ObterPorIdAsync(idExecucao, cancellationToken);
        if (origem is not { Tipo: TipoDeExecucao.Tarefa or TipoDeExecucao.Delegada, IdVersaoDoAgente: { } idVersao })
            return null;
        var lista = await etapas.ListarDaExecucaoAsync(idExecucao, cancellationToken);
        var etapa = lista.SingleOrDefault(e => e.Id == idEtapa);
        var anterior = etapa is null ? null : lista.SingleOrDefault(e => e.Numero == etapa.Numero - 1);
        if (anterior is null) return null;
        var daAnterior = (await delegacoes.ListarDaOrigemAsync(idExecucao, cancellationToken))
            .Where(d => d.IdEtapaOrigem == anterior.Id).ToList();
        if (daAnterior.Count == 0) return null;
        var versao = await versoes.ObterPorIdAsync(idVersao, cancellationToken);
        // A versão congelada é a que roda a retomada: adulterada, nada é entregue ao agente.
        if (versao is null || !versao.Integra)
            throw new InvalidOperationException("A versão congelada da origem não confere com o hash gravado.");
        var agente = await agentes.ObterPorIdAsync(versao.IdAgente, cancellationToken) ??
            throw new InvalidOperationException("Agente da origem não registrado.");
        var desfechos = new List<DesfechoEntregueDto>();
        foreach (var delegacao in daAnterior)
        {
            var subordinada = await execucoes.ObterPorIdAsync(delegacao.IdExecucaoSubordinada, cancellationToken);
            desfechos.Add(new DesfechoEntregueDto(subordinada?.Codigo ?? "?", delegacao.HandleDoDestinatario,
                delegacao.Contrato, delegacao.Estado, delegacao.Resultado, delegacao.Motivo));
        }
        return new RetomadaDaOrigemDto(origem.Id, origem.Codigo, origem.Canal, origem.IdDoUsuarioNoCanal, origem.Contexto,
            origem.Objetivo, agente.Handle, mapper.Mapear<VersaoDoAgente, VersaoDoAgenteDto>(versao), desfechos);
    }

    public async Task<int> RetomarOrigensProntasAsync(CancellationToken cancellationToken = default)
    {
        var retomadas = 0;
        foreach (var idOrigem in await delegacoes.ListarOrigensProntasParaRetomarAsync(cancellationToken))
        {
            var retomou = false;
            await fila.ExecutarEmTransacaoAsync(async token =>
            {
                var origem = await execucoes.TravarComAncestraisAsync(idOrigem, token);
                if (origem is not { Estado: EstadoDaExecucao.AguardandoDelegacoes }) return;
                // A etapa que delegou é a última: a espera começa quando ela conclui. Numa Coordenada (#310), é a unidade
                // da delegação mais recente (as unidades do plano são materializadas juntas, antes de rodar).
                var daOrigem = await delegacoes.ListarDaOrigemAsync(origem.Id, token);
                var ultima = origem.Tipo == TipoDeExecucao.Coordenada
                    ? daOrigem.MaxBy(d => d.CriadaEm)?.IdEtapaOrigem
                    : (await etapas.ListarDaExecucaoAsync(origem.Id, token)).MaxBy(e => e.Numero)!.Id;
                var daEtapa = daOrigem.Where(d => d.IdEtapaOrigem == ultima).ToList();
                if (daEtapa.Count == 0 || daEtapa.Any(d => !d.Terminal)) return;
                retomou = await CicloDaDelegacao.RetomarSeProntaAsync(Ciclo, daEtapa[0], fila.Agora, token);
                await unidade.SalvarAlteracoesAsync(token);
            }, cancellationToken);
            if (retomou) retomadas++;
        }
        return retomadas;
    }

    private async Task<DesfechoDaDelegacaoDto> EncerrarSobPosseAsync(PosseDoTrabalho posse, Guid idEtapa,
        EstadoDaDelegacao estado, string motivo, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        DesfechoDaDelegacaoDto? desfecho = null;
        await fila.GravarComPosseAsync(posse, async token =>
        {
            var (subordinada, etapa, delegacao) = await CarregarAsync(idEtapa, token);
            if (delegacao.Terminal)
            {
                desfecho = new(delegacao.Estado, delegacao.Motivo, false);
                return;
            }
            // Pedido de cancelamento registrado vence: a delegação termina Cancelada com o motivo do pedido.
            var final = await CancelamentoPedidoAsync(subordinada.Id, token) ? EstadoDaDelegacao.Cancelada : estado;
            desfecho = await EncerrarAsync(subordinada, etapa, delegacao, final,
                final == EstadoDaDelegacao.Cancelada ? await MotivoDoPedidoAsync(subordinada.Id, token) : motivo,
                fila.Agora, token);
        }, EstadoDoTrabalho.Concluido, Corte(motivo), cancellationToken: cancellationToken);
        return desfecho!;
    }

    // Delegação, etapa e subordinada terminais juntas, com os eventos e a retomada da origem. Falhou: a subordinada
    // falha (a tentativa conta como iniciada, como na fila); Expirada e Cancelada: a subordinada é cancelada.
    private async Task<DesfechoDaDelegacaoDto> EncerrarAsync(Execucao subordinada, EtapaDaExecucao etapa,
        Delegacao delegacao, EstadoDaDelegacao estado, string motivo, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var antes = delegacao.Estado;
        switch (estado)
        {
            case EstadoDaDelegacao.Falhou: delegacao.Falhar(motivo, agora); break;
            case EstadoDaDelegacao.Expirada: delegacao.Expirar(motivo, agora); break;
            default: delegacao.Cancelar(motivo, agora); break;
        }
        await CicloDaDelegacao.TransicaoDaDelegacaoAsync(Ciclo, delegacao, antes, agora, cancellationToken);
        var subordinadaAntes = subordinada.Estado;
        if (estado == EstadoDaDelegacao.Falhou)
        {
            if (etapa.Estado is EstadoDaEtapa.Pendente or EstadoDaEtapa.Pronta)
                await IniciarSubordinadaAsync(subordinada, etapa, agora, cancellationToken);
            var etapaAntes = etapa.Estado;
            if (!etapa.Terminal)
            {
                etapa.Falhar(motivo, null, agora);
                await TransicaoAsync(subordinada, etapa, etapaAntes, agora, cancellationToken);
            }
            var iniciada = subordinada.Estado;
            subordinada.Falhar(motivo, agora);
            await TransicaoAsync(subordinada, null, iniciada, agora, cancellationToken);
        }
        else
        {
            var etapaAntes = etapa.Estado;
            if (!etapa.Terminal)
            {
                etapa.Cancelar(motivo, null, agora);
                await TransicaoAsync(subordinada, etapa, etapaAntes, agora, cancellationToken);
            }
            subordinada.Cancelar(motivo, agora);
            await TransicaoAsync(subordinada, null, subordinadaAntes, agora, cancellationToken);
        }
        var retomada = await CicloDaDelegacao.RetomarSeProntaAsync(Ciclo, delegacao, agora, cancellationToken);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
        return new(delegacao.Estado, delegacao.Motivo, retomada);
    }

    private async Task IniciarSubordinadaAsync(Execucao subordinada, EtapaDaExecucao etapa, DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        if (subordinada.Estado == EstadoDaExecucao.Pendente)
        {
            subordinada.Iniciar(agora);
            await TransicaoAsync(subordinada, null, EstadoDaExecucao.Pendente, agora, cancellationToken);
        }
        var etapaAntes = etapa.Estado;
        if (etapa.Estado == EstadoDaEtapa.Pendente) etapa.TornarPronta([]);
        etapa.Iniciar(agora);
        await TransicaoAsync(subordinada, etapa, etapaAntes, agora, cancellationToken);
    }

    // Unidade de Coordenada (#310, D2): a etapa de origem é unidade de um plano aprovado da própria execução, o snapshot
    // reproduz o hash e o solicitante e o destinatário da delegação são o coordenador e o participante do papel da
    // unidade, nas versões e hashes congelados. Nulo quando confere.
    private async Task<string?> RecusaDoPlanoAsync(Execucao origem, Delegacao delegacao, CancellationToken cancellationToken)
    {
        var coordenacao = await coordenacoes.ObterDaExecucaoAsync(origem.Id, cancellationToken);
        var linhas = await participantes.ListarDaExecucaoAsync(origem.Id, cancellationToken);
        var etapa = await etapas.ObterPorIdAsync(delegacao.IdEtapaOrigem, cancellationToken);
        var plano = etapa?.IdPlano is { } idPlano ? await planos.ObterPorIdAsync(idPlano, cancellationToken) : null;
        if (coordenacao is null || !coordenacao.SnapshotConfere(linhas) || etapa is not { Tipo: TipoDeEtapa.UnidadeDoPlano } ||
            plano is not { Estado: EstadoDoPlano.Aprovado } || plano.IdExecucao != origem.Id)
            return "A unidade da delegação não é de um plano aprovado desta coordenação com o snapshot íntegro.";
        var coordenador = linhas.SingleOrDefault(p => p.Papel == coordenacao.PapelDoCoordenador);
        var participante = linhas.SingleOrDefault(p => p.Papel == etapa.Papel);
        return coordenador is null || participante is null || participante.Papel == coordenacao.PapelDoCoordenador ||
            coordenador.IdVersaoDoAgente != delegacao.IdVersaoSolicitante ||
            coordenador.HashDaConfiguracaoDoAgente != delegacao.HashDaVersaoSolicitante ||
            participante.IdVersaoDoAgente != delegacao.IdVersaoDestinataria ||
            participante.HashDaConfiguracaoDoAgente != delegacao.HashDaVersaoDestinataria
            ? "A delegação não confere com o snapshot da coordenação." : null;
    }

    // Carrega sob a ordem das travas: a origem (pai) e então a subordinada, ambas FOR UPDATE, depois a etapa e a delegação.
    private async Task<(Execucao Subordinada, EtapaDaExecucao Etapa, Delegacao Delegacao)> CarregarAsync(Guid idEtapa,
        CancellationToken cancellationToken)
    {
        var idExecucao = (await etapas.ObterPorIdAsync(idEtapa, cancellationToken) ??
            throw new InvalidOperationException("Etapa não registrada no ledger.")).IdExecucao;
        var subordinada = await execucoes.TravarComAncestraisAsync(idExecucao, cancellationToken) ??
            throw new InvalidOperationException("Execução não registrada no ledger.");
        if (subordinada.Tipo != TipoDeExecucao.Delegada)
            throw new InvalidOperationException("Só a etapa de uma execução delegada é conduzida aqui.");
        var etapa = await etapas.ObterPorIdAsync(idEtapa, cancellationToken) ??
            throw new InvalidOperationException("Etapa não registrada no ledger.");
        var delegacao = await delegacoes.ObterDaSubordinadaAsync(subordinada.Id, cancellationToken) ??
            throw new InvalidOperationException("Execução delegada sem delegação registrada.");
        return (subordinada, etapa, delegacao);
    }

    // A identidade da unidade (AD-70) calculada como o domínio a calcula; nula quando o pedido nem forma uma unidade
    // válida (o domínio o recusa com o motivo certo ao criar).
    private static string? Chave(Execucao origem, EtapaDaExecucao etapa, SolicitacaoDeDelegacaoDto solicitacao)
    {
        if (!ContratosDeDelegacao.TentarObter(solicitacao.Contrato, out var contrato)) return null;
        var normalizado = Delegacao.NormalizarObjetivo(solicitacao.Objetivo);
        if (normalizado.Length == 0) return null;
        try
        {
            var entrada = contrato.CanonizarEntrada(solicitacao.Entrada);
            return Delegacao.CalcularChave(origem.IdExecucaoRaiz, etapa.Numero, contrato.Identificador,
                Delegacao.HashDoObjetivoNormalizado(origem.IdExecucaoRaiz, normalizado), ContratoDeDelegacao.Hash(entrada));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<(Agente Agente, VersaoDoAgente Versao)> SolicitanteAsync(Execucao origem,
        CancellationToken cancellationToken)
    {
        var versao = origem.IdVersaoDoAgente is { } id ? await versoes.ObterPorIdAsync(id, cancellationToken) : null;
        var agente = versao is null ? null : await agentes.ObterPorIdAsync(versao.IdAgente, cancellationToken);
        if (versao is null || agente is null)
            throw Recusa(MotivoDaRecusaDeDelegacao.OrigemInvalida, "A origem não tem um agente congelado que possa delegar.");
        return (agente, versao);
    }

    // Agentes da raiz até a origem, pela hierarquia gravada (o domínio confere tamanho e último elemento).
    private async Task<IReadOnlyList<Guid>> CadeiaAsync(Execucao origem, CancellationToken cancellationToken)
    {
        var cadeia = new List<Guid>();
        for (var atual = origem; atual is not null;
             atual = atual.IdExecucaoPai is { } pai ? await execucoes.ObterPorIdAsync(pai, cancellationToken) : null)
        {
            var versao = atual.IdVersaoDoAgente is { } id ? await versoes.ObterPorIdAsync(id, cancellationToken) : null;
            if (versao is null) throw Recusa(MotivoDaRecusaDeDelegacao.OrigemInvalida, "A cadeia de agentes está incompleta.");
            cadeia.Insert(0, versao.IdAgente);
        }
        return cadeia;
    }

    // Repositórios do contexto congelado (AD-11/58): o principal e os vinculados; nenhum no workspace geral.
    internal static IReadOnlyList<string> RepositoriosDoContexto(string contexto)
    {
        using var json = JsonDocument.Parse(contexto);
        var raiz = json.RootElement;
        if (raiz.ValueKind != JsonValueKind.Object || !raiz.TryGetProperty("alias", out var alias) ||
            alias.ValueKind != JsonValueKind.String)
            return [];
        var lista = new List<string> { alias.GetString()! };
        if (raiz.TryGetProperty("vinculados", out var vinculados) && vinculados.ValueKind == JsonValueKind.Array)
            foreach (var vinculado in vinculados.EnumerateArray())
                if (vinculado.TryGetProperty("alias", out var outro) && outro.ValueKind == JsonValueKind.String)
                    lista.Add(outro.GetString()!);
        return lista;
    }

    // Resultado pelo contrato (forma canônica, campos, tamanhos) e sem aparência de credencial. Nulo quando vale.
    private static string? Invalido(Delegacao delegacao, string resultado)
    {
        if (string.IsNullOrWhiteSpace(resultado)) return "Resultado vazio.";
        if (!ContratosDeDelegacao.TentarObter(delegacao.Contrato, out var contrato)) return "Contrato desconhecido.";
        try
        {
            contrato.CanonizarSaida(resultado);
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }
        return ProtecaoDeSegredos.ContemSegredo(resultado)
            ? "Resultado com aparência de credencial; use uma referência, nunca o valor." : null;
    }

    private async Task<bool> CancelamentoPedidoAsync(Guid idExecucao, CancellationToken cancellationToken) =>
        (await eventos.ConsultarDoTipoAsync([idExecucao], TipoDeEventoDeOrquestracao.CancelamentoSolicitado,
            cancellationToken)).Count > 0;

    private async Task<string> MotivoDoPedidoAsync(Guid idExecucao, CancellationToken cancellationToken) =>
        PedidoDeCancelamento.MotivoDaTransicao(PedidoDeCancelamento.Motivo((await eventos.ConsultarDoTipoAsync([idExecucao],
            TipoDeEventoDeOrquestracao.CancelamentoSolicitado, cancellationToken)).FirstOrDefault()?.Dados));

    private Task TransicaoAsync(Execucao execucao, EtapaDaExecucao? etapa, Enum de, DateTimeOffset quando,
        CancellationToken cancellationToken) =>
        eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao,
            etapa is null ? TipoDeEventoDeOrquestracao.ExecucaoTransitou : TipoDeEventoDeOrquestracao.EtapaTransitou,
            JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["de"] = de.ToString(),
                ["para"] = (etapa is null ? (Enum)execucao.Estado : etapa.Estado).ToString()
            }), quando, etapa), cancellationToken);

    private async Task<DelegacaoDto> DtoAsync(Delegacao delegacao, CancellationToken cancellationToken)
    {
        var subordinada = await execucoes.ObterPorIdAsync(delegacao.IdExecucaoSubordinada, cancellationToken) ??
            throw new InvalidOperationException("Execução subordinada não registrada no ledger.");
        return Dto(delegacao, subordinada);
    }

    private static DelegacaoDto Dto(Delegacao d, Execucao subordinada) => new(d.Id, d.IdExecucaoRaiz, d.IdExecucaoOrigem,
        d.IdEtapaOrigem, d.IdExecucaoSubordinada, subordinada.Codigo, d.HandleDoDestinatario, d.IdVersaoDestinataria,
        d.Contrato, d.Estado, d.Motivo, d.Resultado, d.HashDoResultado, d.IdSessaoDoResultado, d.CriadaEm, d.AceitaEm,
        d.IniciadaEm, d.EncerradaEm);

    private static DelegacaoRecusadaException Recusa(MotivoDaRecusaDeDelegacao motivo, string mensagem) => new(motivo, mensagem);

    private static string Corte(string texto) =>
        texto.Length <= TrabalhoDaFila.TamanhoMaximoDoErro ? texto : texto[..TrabalhoDaFila.TamanhoMaximoDoErro];
}
