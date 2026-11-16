using System.Text.Json;
using Dante.Application.Contextos;
using Dante.Application.Mapeamento;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Espelho dos jobs e das sessões no ledger (fase 1a da AD-66): o runtime continua em memória e decide; aqui cada
// fato vira transição de domínio + evento-chave numa única transação.
public sealed class LedgerDeExecucoesAppService(
    IExecucaoRepository execucoes,
    IEtapaDaExecucaoRepository etapas,
    IDependenciaDeEtapaRepository dependencias,
    ISessaoDeAgenteRepository sessoes,
    IEventoDeOrquestracaoRepository eventos,
    IUnitOfWorkDaOrquestracao unidade,
    IMapsterTypeAdapter mapper) : ILedgerDeExecucoesAppService
{
    public Task<long> ReservarNumeroDeExecucaoAsync(CancellationToken cancellationToken = default) =>
        execucoes.ReservarNumeroAsync(cancellationToken);

    public Task<long> ReservarNumeroDeSessaoAsync(CancellationToken cancellationToken = default) =>
        sessoes.ReservarNumeroAsync(cancellationToken);

    public async Task RegistrarJobCriadoAsync(JobRegistradoDto job, CancellationToken cancellationToken = default)
    {
        LedgerDeExecucoesValidator.Validar(job);
        var execucao = new Execucao(job.Numero, TipoDeExecucao.Avulsa, job.Canal, job.IdDoUsuarioNoCanal,
            SerializarContexto(job.Contexto), null, job.CriadoEm);
        // O job legado roda no checkout principal, como hoje; escreve quando o contexto é um repositório. Uma
        // retomada nunca o repete sem confirmação.
        var etapa = new EtapaDaExecucao(execucao, 1, TipoDeEtapa.ProcessoAvulso,
            job.Contexto.Mode == JobExecutionMode.Repository, PoliticaDeRetomada.ComConfirmacao, job.CriadoEm,
            job.Agente, job.Modelo, job.Esforco);
        await execucoes.AdicionarAsync(execucao, cancellationToken);
        await etapas.AdicionarAsync(etapa, cancellationToken);
        await eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao, TipoDeEventoDeOrquestracao.ExecucaoCriada,
            Json(new Dictionary<string, string?> { ["tipo"] = execucao.Tipo.ToString() }), job.CriadoEm), cancellationToken);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
    }

    public async Task RegistrarJobIniciadoAsync(long numero, DateTimeOffset iniciadoEm,
        CancellationToken cancellationToken = default)
    {
        LedgerDeExecucoesValidator.ValidarNumero(numero);
        var (execucao, etapa) = await CarregarJobAsync(numero, cancellationToken);
        await IniciarAsync(execucao, etapa, iniciadoEm, cancellationToken);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
    }

    public async Task RegistrarJobEncerradoAsync(JobEncerradoDto fim, CancellationToken cancellationToken = default)
    {
        LedgerDeExecucoesValidator.Validar(fim);
        var (execucao, etapa) = await CarregarJobAsync(fim.Numero, cancellationToken);
        var quando = fim.EncerradoEm;
        // O JobRegistry pode falhar um job ainda na fila (anexos indisponíveis, início não confirmado). Pendente → Falhou
        // não existe: a tentativa conta como iniciada e falha no mesmo instante.
        if (fim.EstadoFinal == EstadoDaExecucao.Falhou && execucao.Estado == EstadoDaExecucao.Pendente)
            await IniciarAsync(execucao, etapa, quando, cancellationToken);
        var execucaoAntes = execucao.Estado;
        var etapaAntes = etapa.Estado;
        switch (fim.EstadoFinal)
        {
            case EstadoDaExecucao.Concluida:
                etapa.Concluir(fim.CodigoDeSaida, quando);
                execucao.Concluir(quando);
                break;
            case EstadoDaExecucao.Falhou:
                var erro = string.IsNullOrWhiteSpace(fim.Erro) ? "Falha sem detalhe informado." : fim.Erro;
                etapa.Falhar(erro, fim.CodigoDeSaida, quando);
                execucao.Falhar(erro, quando);
                break;
            default:
                etapa.Cancelar(null, fim.CodigoDeSaida, quando);
                execucao.Cancelar(null, quando);
                break;
        }
        await RegistrarTransicaoAsync(execucao, etapa, etapaAntes, quando, cancellationToken);
        await RegistrarTransicaoAsync(execucao, null, execucaoAntes, quando, cancellationToken);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
    }

    public async Task RegistrarSessaoCriadaAsync(SessaoRegistradaDto sessao, CancellationToken cancellationToken = default)
    {
        LedgerDeExecucoesValidator.Validar(sessao);
        var registro = new SessaoDeAgente(sessao.Numero, sessao.Agente, sessao.Canal, sessao.IdDoDono,
            SerializarContexto(sessao.Contexto), sessao.Perfil, sessao.CriadaEm, sessao.Modelo, sessao.Esforco);
        await sessoes.AdicionarAsync(registro, cancellationToken);
        await eventos.AdicionarAsync(EventoDeOrquestracao.DaSessao(registro, TipoDeEventoDeOrquestracao.SessaoCriada,
            null, Json(new Dictionary<string, string?> { ["agente"] = registro.Agente.ToString() }), sessao.CriadaEm),
            cancellationToken);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
    }

    public async Task RegistrarSessaoIniciadaAsync(long numero, string? idUpstream, string? modeloInformado,
        DateTimeOffset iniciadaEm, CancellationToken cancellationToken = default)
    {
        LedgerDeExecucoesValidator.ValidarNumero(numero);
        var sessao = await CarregarSessaoAsync(numero, cancellationToken);
        var antes = sessao.Estado;
        sessao.MarcarIniciada(idUpstream, modeloInformado, iniciadaEm);
        await RegistrarTransicaoAsync(sessao, antes, iniciadaEm, cancellationToken);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
    }

    public async Task RegistrarSessaoEncerradaAsync(SessaoEncerradaDto fim, CancellationToken cancellationToken = default)
    {
        LedgerDeExecucoesValidator.Validar(fim);
        var sessao = await CarregarSessaoAsync(fim.Numero, cancellationToken);
        var antes = sessao.Estado;
        var motivo = string.IsNullOrWhiteSpace(fim.Motivo) ? "Motivo não informado." : fim.Motivo;
        switch (fim.EstadoFinal)
        {
            case EstadoDaSessaoDeAgente.Encerrada:
                sessao.Encerrar(fim.IdUpstream, fim.EncerradaEm);
                break;
            case EstadoDaSessaoDeAgente.Falhou:
                sessao.Falhar(motivo, fim.IdUpstream, fim.EncerradaEm);
                break;
            default:
                sessao.Interromper(motivo, fim.IdUpstream, fim.EncerradaEm);
                break;
        }
        await RegistrarTransicaoAsync(sessao, antes, fim.EncerradaEm, cancellationToken);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
    }

    public async Task RegistrarEventoDaSessaoAsync(EventoDeSessaoDto evento, CancellationToken cancellationToken = default)
    {
        LedgerDeExecucoesValidator.Validar(evento);
        var sessao = await CarregarSessaoAsync(evento.NumeroDaSessao, cancellationToken);
        await eventos.AdicionarAsync(EventoDeOrquestracao.DaSessao(sessao, evento.Tipo, evento.IdTurno,
            evento.Dados is null ? null : Json(evento.Dados), evento.OcorridoEm), cancellationToken);
        await unidade.SalvarAlteracoesAsync(cancellationToken);
    }

    public async Task<ExecucaoDto?> ObterExecucaoAsync(long numero, CancellationToken cancellationToken = default)
    {
        LedgerDeExecucoesValidator.ValidarNumero(numero);
        if (await execucoes.ObterPorNumeroAsync(numero, cancellationToken) is not { } execucao) return null;
        return mapper.Mapear<Execucao, ExecucaoDto>(execucao) with
        {
            Etapas = (await etapas.ListarDaExecucaoAsync(execucao.Id, cancellationToken))
                .Select(mapper.Mapear<EtapaDaExecucao, EtapaDaExecucaoDto>).ToArray(),
            Dependencias = (await dependencias.ListarDaExecucaoAsync(execucao.Id, cancellationToken))
                .Select(mapper.Mapear<DependenciaDeEtapa, DependenciaDeEtapaDto>).ToArray(),
            Eventos = (await eventos.ListarDaExecucaoAsync(execucao.Id, cancellationToken))
                .Select(mapper.Mapear<EventoDeOrquestracao, EventoDeOrquestracaoDto>).ToArray()
        };
    }

    public async Task<SessaoDeAgenteDto?> ObterSessaoAsync(long numero, CancellationToken cancellationToken = default)
    {
        LedgerDeExecucoesValidator.ValidarNumero(numero);
        if (await sessoes.ObterPorNumeroAsync(numero, cancellationToken) is not { } sessao) return null;
        return mapper.Mapear<SessaoDeAgente, SessaoDeAgenteDto>(sessao) with
        {
            Eventos = (await eventos.ListarDaSessaoAsync(sessao.Id, cancellationToken))
                .Select(mapper.Mapear<EventoDeOrquestracao, EventoDeOrquestracaoDto>).ToArray()
        };
    }

    private async Task IniciarAsync(Execucao execucao, EtapaDaExecucao etapa, DateTimeOffset quando,
        CancellationToken cancellationToken)
    {
        var execucaoAntes = execucao.Estado;
        var etapaAntes = etapa.Estado;
        execucao.Iniciar(quando);
        // O job tem uma única etapa, sem dependências.
        etapa.TornarPronta([]);
        etapa.Iniciar(quando);
        await RegistrarTransicaoAsync(execucao, null, execucaoAntes, quando, cancellationToken);
        await RegistrarTransicaoAsync(execucao, etapa, etapaAntes, quando, cancellationToken);
    }

    private async Task<(Execucao Execucao, EtapaDaExecucao Etapa)> CarregarJobAsync(long numero,
        CancellationToken cancellationToken)
    {
        var execucao = await execucoes.ObterPorNumeroAsync(numero, cancellationToken) ??
            throw new InvalidOperationException("Execução não registrada no ledger.");
        if (execucao.Tipo != TipoDeExecucao.Avulsa) throw new InvalidOperationException("A execução não é um job avulso.");
        var etapa = (await etapas.ListarDaExecucaoAsync(execucao.Id, cancellationToken)).SingleOrDefault(e => e.Numero == 1) ??
            throw new InvalidOperationException("Etapa do job não registrada no ledger.");
        return (execucao, etapa);
    }

    private async Task<SessaoDeAgente> CarregarSessaoAsync(long numero, CancellationToken cancellationToken) =>
        await sessoes.ObterPorNumeroAsync(numero, cancellationToken) ??
        throw new InvalidOperationException("Sessão não registrada no ledger.");

    // Etapa nula: transição da própria execução.
    private Task RegistrarTransicaoAsync(Execucao execucao, EtapaDaExecucao? etapa, Enum de, DateTimeOffset quando,
        CancellationToken cancellationToken) =>
        eventos.AdicionarAsync(EventoDeOrquestracao.DaExecucao(execucao,
            etapa is null ? TipoDeEventoDeOrquestracao.ExecucaoTransitou : TipoDeEventoDeOrquestracao.EtapaTransitou,
            Transicao(de, etapa is null ? execucao.Estado : etapa.Estado), quando, etapa), cancellationToken);

    private Task RegistrarTransicaoAsync(SessaoDeAgente sessao, EstadoDaSessaoDeAgente de, DateTimeOffset quando,
        CancellationToken cancellationToken) =>
        eventos.AdicionarAsync(EventoDeOrquestracao.DaSessao(sessao, TipoDeEventoDeOrquestracao.SessaoTransitou, null,
            Transicao(de, sessao.Estado), quando), cancellationToken);

    private static string Transicao(Enum de, Enum para) =>
        Json(new Dictionary<string, string?> { ["de"] = de.ToString(), ["para"] = para.ToString() });

    private static string Json(IReadOnlyDictionary<string, string?> dados) => JsonSerializer.Serialize(dados);

    // Contexto congelado (AD-11/58): modo, alias e diretório do principal e dos vinculados, sem ambiente nem segredos.
    private static string SerializarContexto(JobExecutionContext contexto) => JsonSerializer.Serialize(new
    {
        modo = contexto.Mode.ToString(),
        alias = contexto.RepositoryAlias,
        diretorio = contexto.WorkingDirectory,
        vinculados = contexto.RepositoriosVinculados.Select(vinculado => new { alias = vinculado.Alias, caminho = vinculado.Caminho })
    });
}
