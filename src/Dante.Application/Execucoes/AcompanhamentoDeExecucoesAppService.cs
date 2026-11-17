using System.Text.Json;
using Dante.Application.Agentes;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Leitura do ledger e da fila para o proprietário (#305). Só consulta: nada aqui grava, trava ou inicia processo.
public sealed class AcompanhamentoDeExecucoesAppService(
    IFilaDeTrabalho fila,
    IExecucaoRepository execucoes,
    IEtapaDaExecucaoRepository etapas,
    IEventoDeOrquestracaoRepository eventos,
    ISessaoDeAgenteRepository sessoes,
    IVersaoDoAgenteRepository versoes,
    IAgenteRepository agentes) : IAcompanhamentoDeExecucoesAppService
{
    public const int EncerradasRecentes = 10;
    public const int EncerradasEmTodas = 50;
    public const int TamanhoMaximoDoObjetivo = 200;
    public const int TamanhoMaximoDoTexto = 300;

    public async Task<IReadOnlyList<ResumoDaExecucaoDto>> ListarAsync(ProprietarioDaExecucao proprietario, bool todas,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proprietario);
        var lista = await execucoes.ListarDoProprietarioAsync(proprietario.Canal, proprietario.IdDoUsuario,
            todas ? EncerradasEmTodas : EncerradasRecentes, cancellationToken);
        return await ResumirAsync(lista, cancellationToken);
    }

    public async Task<IReadOnlyList<ResumoDaExecucaoDto>> ListarAguardandoAsync(ProprietarioDaExecucao proprietario,
        CancellationToken cancellationToken = default) =>
        (await Ativas(proprietario, cancellationToken)).Where(resumo => resumo.Espera is not null).ToArray();

    public async Task<PainelDeExecucoesDto> ResumirAsync(ProprietarioDaExecucao proprietario,
        CancellationToken cancellationToken = default)
    {
        var ativas = await Ativas(proprietario, cancellationToken);
        var aguardando = ativas.Count(resumo => resumo.Espera is not null);
        return new PainelDeExecucoesDto(ativas.Count - aguardando, aguardando);
    }

    public async Task<DetalheDaExecucaoDto?> ObterAsync(ProprietarioDaExecucao proprietario, string codigo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proprietario);
        if (!Execucao.TentarInterpretarCodigo(codigo, out var numero, out var avulsa)) return null;
        var execucao = await execucoes.ConsultarPorNumeroAsync(numero, cancellationToken);
        if (execucao is null || !proprietario.EDono(execucao.Canal, execucao.IdDoUsuarioNoCanal) ||
            (execucao.Tipo == TipoDeExecucao.Avulsa) != avulsa)
            return null;
        var resumo = (await ResumirAsync([execucao], cancellationToken)).Single();
        var lista = await etapas.ConsultarDasExecucoesAsync([execucao.Id], cancellationToken);
        var pedido = (await eventos.ConsultarDoTipoAsync([execucao.Id], TipoDeEventoDeOrquestracao.CancelamentoSolicitado,
            cancellationToken)).FirstOrDefault();
        var (modo, alias, vinculados) = Contexto(execucao.Contexto);
        var etapasExibidas = new List<EtapaExibidaDto>();
        foreach (var etapa in lista)
            etapasExibidas.Add(new EtapaExibidaDto
            {
                Numero = etapa.Numero,
                Tipo = etapa.Tipo,
                Estado = etapa.Estado,
                Escrita = etapa.Escrita,
                Tentativa = etapa.Tentativa,
                Agente = etapa.Agente,
                Modelo = etapa.Modelo,
                Esforco = etapa.Esforco,
                Sessao = etapa.IdSessaoDeAgente is { } idSessao
                    ? (await sessoes.ObterPorIdAsync(idSessao, cancellationToken))?.Codigo : null,
                CodigoDeSaida = etapa.CodigoDeSaida,
                Erro = Seguro(etapa.Erro, TamanhoMaximoDoTexto),
                Motivo = Seguro(etapa.Motivo, TamanhoMaximoDoTexto),
                IniciadaEm = etapa.IniciadaEm,
                EncerradaEm = etapa.EncerradaEm
            });
        return new DetalheDaExecucaoDto
        {
            Resumo = resumo,
            ModoDoContexto = modo,
            AliasDoContexto = alias,
            AliasesVinculados = vinculados,
            Objetivo = Seguro(execucao.Objetivo, TamanhoMaximoDoObjetivo),
            Motivo = Seguro(execucao.Motivo, TamanhoMaximoDoTexto),
            Erro = Seguro(execucao.Erro, TamanhoMaximoDoTexto),
            IniciadaEm = execucao.IniciadaEm,
            CancelamentoSolicitadoEm = pedido?.OcorridoEm,
            MotivoDoCancelamento = pedido is null ? null : Seguro(PedidoDeCancelamento.Motivo(pedido.Dados), TamanhoMaximoDoTexto),
            Etapas = etapasExibidas
        };
    }

    private async Task<IReadOnlyList<ResumoDaExecucaoDto>> Ativas(ProprietarioDaExecucao proprietario,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proprietario);
        var lista = await execucoes.ListarDoProprietarioAsync(proprietario.Canal, proprietario.IdDoUsuario, 0,
            cancellationToken);
        return await ResumirAsync(lista, cancellationToken);
    }

    private async Task<IReadOnlyList<ResumoDaExecucaoDto>> ResumirAsync(IReadOnlyList<Execucao> lista,
        CancellationToken cancellationToken)
    {
        if (lista.Count == 0) return [];
        var ids = lista.Select(e => e.Id).ToArray();
        var etapasPorExecucao = (await etapas.ConsultarDasExecucoesAsync(ids, cancellationToken))
            .ToLookup(e => e.IdExecucao);
        var trabalhosPorExecucao = (await fila.ListarAtivosAsync(ids, cancellationToken)).ToLookup(t => t.IdExecucao!.Value);
        var comPedido = (await eventos.ConsultarDoTipoAsync(ids, TipoDeEventoDeOrquestracao.CancelamentoSolicitado,
            cancellationToken)).Select(e => e.IdExecucao!.Value).ToHashSet();
        var agentesPorVersao = new Dictionary<Guid, string?>();
        var agora = fila.Agora;
        var resumos = new List<ResumoDaExecucaoDto>(lista.Count);
        foreach (var execucao in lista)
        {
            var etapasDela = etapasPorExecucao[execucao.Id].OrderBy(e => e.Numero).ToArray();
            var atual = etapasDela.FirstOrDefault(e => !e.Terminal) ?? etapasDela.LastOrDefault();
            string? agente;
            if (execucao.IdVersaoDoAgente is { } idVersao)
            {
                if (!agentesPorVersao.TryGetValue(idVersao, out agente))
                    agentesPorVersao[idVersao] = agente = await AgenteAsync(idVersao, cancellationToken);
            }
            else agente = atual?.Agente?.ToString().ToLowerInvariant();
            resumos.Add(new ResumoDaExecucaoDto
            {
                Codigo = execucao.Codigo,
                Numero = execucao.Numero,
                Tipo = execucao.Tipo,
                Estado = execucao.Estado,
                Terminal = execucao.Terminal,
                Agente = agente,
                EtapaAtual = atual?.Numero,
                TotalDeEtapas = etapasDela.Length,
                EstadoDaEtapaAtual = atual?.Estado,
                Tentativa = atual?.Tentativa,
                Espera = Espera(execucao, trabalhosPorExecucao[execucao.Id].ToArray(), agora),
                CancelamentoSolicitado = comPedido.Contains(execucao.Id),
                CriadaEm = execucao.CriadaEm,
                EncerradaEm = execucao.EncerradaEm
            });
        }
        return resumos;
    }

    // Por que a execução não está rodando; nulo quando roda (ou foi reivindicada) ou terminou. Sem posição na fila:
    // com SKIP LOCKED, prioridade, backoff e reivindicações concorrentes não há posição estável a mostrar.
    internal static MotivoDaEspera? Espera(Execucao execucao, IReadOnlyList<TrabalhoDaFila> ativos, DateTimeOffset agora) =>
        execucao.Estado switch
        {
            EstadoDaExecucao.Pendente when ativos.Any(t => t.Estado == EstadoDoTrabalho.Reivindicado) => null,
            EstadoDaExecucao.Pendente when ativos.Any(t => t.Estado == EstadoDoTrabalho.Pendente && t.Tentativas > 0 &&
                                                           t.DisponivelEm > agora) => MotivoDaEspera.AguardandoNovaTentativa,
            EstadoDaExecucao.Pendente => MotivoDaEspera.NaFila,
            EstadoDaExecucao.Interrompida => MotivoDaEspera.Interrompida,
            EstadoDaExecucao.Bloqueada => MotivoDaEspera.Bloqueada,
            EstadoDaExecucao.AguardandoAprovacao => MotivoDaEspera.AguardandoAprovacao,
            EstadoDaExecucao.AguardandoEntrada => MotivoDaEspera.AguardandoEntrada,
            EstadoDaExecucao.AguardandoDelegacoes => MotivoDaEspera.AguardandoDelegacoes,
            _ => null
        };

    private async Task<string?> AgenteAsync(Guid idVersao, CancellationToken cancellationToken)
    {
        var versao = await versoes.ObterPorIdAsync(idVersao, cancellationToken);
        var agente = versao is null ? null : await agentes.ObterPorIdAsync(versao.IdAgente, cancellationToken);
        return agente is null ? null : $"{agente.Handle} v{versao!.Numero}";
    }

    // Texto vindo de fora (erro de processo, motivo, pedido do usuário): trocado quando parece segredo, depois uma
    // linha e cortado. A proteção vale para o texto inteiro antes do corte, para um segredo não escapar pela metade; o
    // corte vale também para o texto já protegido, que nunca passa do tamanho pedido.
    internal static string? Seguro(string? texto, int tamanhoMaximo)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tamanhoMaximo, 2);
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var redigido = ProtecaoDeSegredos.Redigir(texto)!;
        var linha = string.Join(' ', redigido.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return linha.Length <= tamanhoMaximo ? linha : linha[..(tamanhoMaximo - 1)] + "…";
    }

    // Contexto congelado (AD-11/58) → só modo e aliases; o diretório gravado nunca sai daqui.
    internal static (string Modo, string? Alias, IReadOnlyList<string> Vinculados) Contexto(string contexto)
    {
        try
        {
            using var json = JsonDocument.Parse(contexto);
            var raiz = json.RootElement;
            var modo = raiz.TryGetProperty("modo", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "";
            var alias = raiz.TryGetProperty("alias", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            var vinculados = raiz.TryGetProperty("vinculados", out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(item => item.TryGetProperty("alias", out var va) && va.ValueKind == JsonValueKind.String
                    ? va.GetString() : null).OfType<string>().ToArray()
                : [];
            return (modo, alias, vinculados);
        }
        catch (JsonException)
        {
            return ("", null, []);
        }
    }
}
