using System.Text;
using System.Text.Json;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Entrega do artefato aprovado de um roteiro (#311, PR 3): o caso de uso monta o pedido do gateway com os fatos do ledger e
// da worktree, e fornece ao gateway a remontagem do payload e o commit. Quem autoriza é o gateway
// (IAprovacoesDeAcaoAppService: dono, hash, prazo, revogação e execução única por CAS); o commit é do Git fechado
// (ICommitDeWorktree), que reconfere o conteúdo imediatamente antes de gravar.
public sealed class EntregaDoArtefatoAppService(
    IFilaDeTrabalho fila,
    IExecucaoRepository execucoes,
    IPedidoDeAprovacaoRepository pedidos,
    IReservaDeWorktreeRepository reservas,
    IAprovacaoDeAcaoRepository aprovacoes,
    IAprovacoesDeAcaoAppService gateway,
    IGerenciadorDeWorktrees gerenciador,
    ICommitDeWorktree commits,
    OpcoesDaEntrega? opcoes = null,
    IdentidadeDoProcessador? identidade = null) : IEntregaDoArtefatoAppService
{
    // As capacidades que a entrega usa, fixas no payload: mudar o que ela faz é mudar o hash.
    public static readonly IReadOnlyList<string> Capacidades = ["git.add", "git.commit"];

    public const string SemIdentidade =
        "A identidade Git da entrega não está configurada (Orquestracao:Entrega:AutorNome e AutorEmail); nada foi pedido.";

    private readonly OpcoesDaEntrega opcoes = opcoes ?? new OpcoesDaEntrega();
    private string Maquina => identidade?.Host ?? Environment.MachineName;

    public async Task<SolicitacaoDeEntregaDto> SolicitarAsync(ProprietarioDaExecucao dono, string codigo, string? mensagem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dono);
        var execucao = await LocalizarAsync(dono, codigo, cancellationToken);
        if (execucao is null) return new(ResultadoDaSolicitacaoDeEntrega.NaoEncontrada, null);
        var mensagemNormalizada = string.IsNullOrWhiteSpace(mensagem) ? null : Normalizar(mensagem);
        // A mensagem vai ao commit, ao ledger e ao Telegram: nunca com credencial.
        if (ProtecaoDeSegredos.ContemSegredo(mensagemNormalizada))
            throw new ArgumentException("A mensagem parece conter uma credencial; reescreva sem ela.");

        // Uma entrega por execução, com a execução travada (FOR UPDATE) da leitura dos pedidos existentes até a gravação
        // do novo: dois pedidos simultâneos da mesma execução, mesmo com mensagens diferentes, nunca criam dois; o
        // segundo espera o primeiro gravar e o encontra. A execução de uma entrega já aprovada fica fora da transação
        // (ela tem as próprias gravações e o CAS do gateway).
        SolicitacaoDeEntregaDto? resultado = null;
        AprovacaoDeAcao? aprovadaParaExecutar = null;
        await fila.ExecutarEmTransacaoAsync(async token =>
        {
            _ = await execucoes.ObterParaAtualizarAsync(execucao.Id, token) ??
                throw new InvalidOperationException("Execução não registrada no ledger.");
            var existentes = (await aprovacoes.ListarDaExecucaoAsync(execucao.Id, token))
                .Where(a => a.Acao == CatalogoDeAcoes.EntregarArtefatoDoRoteiro.Nome).ToArray();
            if (existentes.FirstOrDefault(a => a.Estado == EstadoDaAprovacaoDeAcao.Executada) is { } entregue)
            {
                resultado = new(ResultadoDaSolicitacaoDeEntrega.JaEntregue, execucao.Codigo, await DtoAsync(dono, entregue, token));
                return;
            }
            if (existentes.FirstOrDefault(a => a.Estado is EstadoDaAprovacaoDeAcao.Incerto or EstadoDaAprovacaoDeAcao.EmExecucao)
                is { } incerta)
                throw new InvalidOperationException(incerta.Estado == EstadoDaAprovacaoDeAcao.Incerto
                    ? $"A entrega de {execucao.Codigo} ficou incerta e não será repetida ({incerta.Resultado ?? "estado não observado"}). " +
                      "Confira a branch da worktree antes de qualquer outra ação."
                    : $"A entrega de {execucao.Codigo} está em execução.");
            if (existentes.FirstOrDefault(a => a.Estado is EstadoDaAprovacaoDeAcao.Pendente or EstadoDaAprovacaoDeAcao.Aprovada)
                is { } aberta)
            {
                if (mensagemNormalizada is not null &&
                    mensagemNormalizada != PayloadDaAcao.Parametro(aberta.Payload, "mensagem"))
                    throw new InvalidOperationException($"Já há um pedido de entrega de {execucao.Codigo} em aberto, com outra " +
                        "mensagem. Recuse-o em /acoes antes de pedir outro.");
                if (aberta.Estado == EstadoDaAprovacaoDeAcao.Pendente)
                    resultado = new(ResultadoDaSolicitacaoDeEntrega.EmAberto, execucao.Codigo, await DtoAsync(dono, aberta, token));
                else aprovadaParaExecutar = aberta;
                return;
            }

            var autor = opcoes.Autor ?? throw new InvalidOperationException(opcoes.ProblemaDaConfiguracao is { } problema
                ? $"A identidade Git da entrega está inválida ({problema}); nada foi pedido."
                : SemIdentidade);
            var fatos = await FatosAsync(execucao, token);
            if (fatos.Head != fatos.Reserva.ShaBase)
                throw new InvalidOperationException($"A branch da worktree de {execucao.Codigo} já tem commits além da base; " +
                    "nada foi pedido.");
            if (fatos.Alteracoes.Hash != fatos.Gate.HashDoArtefato)
                throw new InvalidOperationException($"O conteúdo da worktree de {execucao.Codigo} mudou depois da aprovação no " +
                    $"gate (agora {fatos.Alteracoes.Hash[..12]}, aprovado {fatos.Gate.HashDoArtefato[..12]}); nada foi pedido.");
            if (fatos.Alteracoes.Vazias)
                throw new InvalidOperationException($"A worktree de {execucao.Codigo} não tem alterações; nada a entregar.");

            var parametros = Parametros(fatos.Gate.HashDoArtefato, autor, fatos.Head, fatos.Reserva.Branch,
                mensagemNormalizada ?? $"Artefato aprovado do roteiro {execucao.Codigo} (D.A.N.T.E.)");
            var pedido = await gateway.SolicitarAsync(new SolicitacaoDeAcaoDto(CatalogoDeAcoes.EntregarArtefatoDoRoteiro.Nome,
                execucao.Id, fatos.Gate.IdEtapa, fatos.Reserva.Alias, Capacidades, parametros, dono), token);
            resultado = new(pedido.Novo ? ResultadoDaSolicitacaoDeEntrega.Solicitada : ResultadoDaSolicitacaoDeEntrega.EmAberto,
                execucao.Codigo, pedido.Aprovacao);
        }, cancellationToken);
        if (aprovadaParaExecutar is null) return resultado!;
        var executada = await ExecutarAprovadaAsync(dono, aprovadaParaExecutar.Id, cancellationToken);
        return new(ResultadoDaSolicitacaoDeEntrega.Executada, execucao.Codigo, executada?.Aprovacao, executada);
    }

    public async Task<ExecucaoDeAcaoDto?> ExecutarAprovadaAsync(ProprietarioDaExecucao dono, Guid idAprovacao,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dono);
        var aprovacao = await aprovacoes.ObterPorIdAsync(idAprovacao, cancellationToken);
        if (aprovacao is null || aprovacao.Acao != CatalogoDeAcoes.EntregarArtefatoDoRoteiro.Nome ||
            !dono.EDono(aprovacao.CanalDoDono, aprovacao.IdDoDono))
            return null;
        // A worktree da remontagem é a do commit: as duas leituras acontecem sobre o mesmo diretório conferido.
        WorktreePlanejada? worktree = null;
        var acao = new ExecucaoDaAcaoProtegida(CatalogoDeAcoes.EntregarArtefatoDoRoteiro,
            async (gravada, token) =>
            {
                var (payload, conferida) = await RemontarAsync(gravada, token);
                worktree = conferida;
                return payload;
            },
            async (gravada, token) => await CommitarAsync(gravada, worktree!, token));
        return await gateway.ExecutarAsync(aprovacao.Id, acao, cancellationToken);
    }

    // ---- remontagem e execução ----

    // O payload com os fatos de agora: o hash do conteúdo atual da worktree, o HEAD atual como base, a branch e o alias
    // da reserva, a etapa do gate aprovado e o autor configurado. A mensagem é a escolhida pelo dono (gravada no pedido).
    // Qualquer diferença do aprovado (conteúdo, HEAD, autor, worktree) muda o hash e a aprovação não executa.
    private async Task<(PayloadDaAcao Payload, WorktreePlanejada Worktree)> RemontarAsync(AprovacaoDeAcao aprovacao,
        CancellationToken cancellationToken)
    {
        var execucao = await execucoes.ObterPorIdAsync(aprovacao.IdExecucao, cancellationToken) ??
            throw new InvalidOperationException("Execução não encontrada.");
        var autor = opcoes.Autor ?? throw new InvalidOperationException(SemIdentidade);
        var fatos = await FatosAsync(execucao, cancellationToken);
        var mensagem = PayloadDaAcao.Parametro(aprovacao.Payload, "mensagem") ??
            throw new InvalidOperationException("Pedido sem mensagem.");
        var payload = PayloadDaAcao.Montar(CatalogoDeAcoes.EntregarArtefatoDoRoteiro, execucao.Id, fatos.Gate.IdEtapa,
            fatos.Reserva.Alias, Capacidades, SolicitanteDaAcao.Dono(aprovacao.CanalDoDono, aprovacao.IdDoDono),
            Parametros(fatos.Alteracoes.Hash, autor, fatos.Head, fatos.Reserva.Branch, mensagem));
        return (payload, WorktreeDaExecucaoDto.De(fatos.Reserva).Planejada);
    }

    // Exatamente os parâmetros aprovados (o gateway já conferiu que o payload remontado é o mesmo).
    private async Task<DesfechoDaAcaoProtegida> CommitarAsync(AprovacaoDeAcao aprovacao, WorktreePlanejada worktree,
        CancellationToken cancellationToken)
    {
        string Parametro(string nome) => PayloadDaAcao.Parametro(aprovacao.Payload, nome) ??
            throw new InvalidOperationException($"Pedido sem {nome}.");
        var commit = new CommitDoArtefatoDto(Parametro("base"), Parametro("artefato"),
            new AutorDoCommit(Parametro("autor_nome"), Parametro("autor_email")), Parametro("mensagem"));
        return await commits.CommitarAsync(worktree, commit, cancellationToken) switch
        {
            DesfechoDoCommitDto.Commitado c => new DesfechoDaAcaoProtegida.Executada(
                $"Commit {c.Sha} na branch {worktree.Branch} sobre {commit.Base[..12]}, sem push."),
            DesfechoDoCommitDto.SemEfeito s => new DesfechoDaAcaoProtegida.SemEfeito(s.Motivo),
            DesfechoDoCommitDto.Incerto i => new DesfechoDaAcaoProtegida.Incerta(i.Motivo, i.EstadoObservado),
            _ => new DesfechoDaAcaoProtegida.Incerta(AprovacoesDeAcaoAppService.MotivoDaFalhaInesperada,
                await commits.DescreverEstadoAsync(worktree, CancellationToken.None))
        };
    }

    // ---- apoio ----

    private sealed record FatosDaEntrega(PedidoDeAprovacao Gate, ReservaDeWorktree Reserva, string Head,
        AlteracoesDaWorktreeDto Alteracoes);

    // O roteiro concluído pelo gate aprovado, a worktree principal deste host (liberada ou preservada, nunca removida) e
    // o que ela contém agora. Recusas: InvalidOperationException com o motivo, nada é pedido nem executado.
    private async Task<FatosDaEntrega> FatosAsync(Execucao execucao, CancellationToken cancellationToken)
    {
        if (execucao.Tipo != TipoDeExecucao.Roteiro || execucao.Estado != EstadoDaExecucao.Concluida)
            throw new InvalidOperationException($"{execucao.Codigo} não é um roteiro concluído pela aprovação do artefato.");
        var gate = (await pedidos.ListarDaExecucaoAsync(execucao.Id, cancellationToken)).SingleOrDefault(p =>
                p.Finalidade == FinalidadeDaAprovacao.ArtefatoDoRoteiro && p.Estado == EstadoDoPedidoDeAprovacao.Aprovado) ??
            throw new InvalidOperationException($"O artefato de {execucao.Codigo} não foi aprovado no gate; nada a entregar.");
        var reserva = (await reservas.ListarDaExecucaoAsync(execucao.Id, cancellationToken)).SingleOrDefault(r => r.Principal) ??
            throw new InvalidOperationException($"{execucao.Codigo} não tem worktree registrada.");
        if (reserva.Estado is not (EstadoDaReservaDeWorktree.Liberada or EstadoDaReservaDeWorktree.Preservada))
            throw new InvalidOperationException($"A worktree de {execucao.Codigo} não está disponível para entrega " +
                $"({reserva.Estado}).");
        if (reserva.MaquinaDoWorker != Maquina)
            throw new InvalidOperationException($"A worktree de {execucao.Codigo} está em outro host; a entrega só ocorre nele.");
        var worktree = WorktreeDaExecucaoDto.De(reserva).Planejada;
        try
        {
            var head = await commits.LerHeadAsync(worktree, cancellationToken);
            var alteracoes = await gerenciador.ResumirAlteracoesAsync(worktree, 1, cancellationToken);
            return new FatosDaEntrega(gate, reserva, head, alteracoes);
        }
        catch (WorktreeInvalidaException exception)
        {
            throw new InvalidOperationException($"A worktree de {execucao.Codigo} não pôde ser conferida ({exception.Message}).");
        }
    }

    private async Task<Execucao?> LocalizarAsync(ProprietarioDaExecucao dono, string codigo, CancellationToken cancellationToken)
    {
        if (!Execucao.TentarInterpretarCodigo(codigo, out var numero, out var avulsa) || avulsa) return null;
        var encontrada = await execucoes.ConsultarPorNumeroAsync(numero, cancellationToken);
        return encontrada is { Tipo: TipoDeExecucao.Roteiro } && dono.EDono(encontrada.Canal, encontrada.IdDoUsuarioNoCanal)
            ? encontrada : null;
    }

    private async Task<AprovacaoDeAcaoDto?> DtoAsync(ProprietarioDaExecucao dono, AprovacaoDeAcao aprovacao,
        CancellationToken cancellationToken) => await gateway.ObterAsync(dono, aprovacao.Id, cancellationToken);

    private static string Parametros(string artefato, AutorDoCommit autor, string baseDoCommit, string branch, string mensagem) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["artefato"] = artefato,
            ["autor_email"] = autor.Email,
            ["autor_nome"] = autor.Nome,
            ["base"] = baseDoCommit,
            ["branch"] = branch,
            ["mensagem"] = mensagem
        });

    // A mesma normalização do payload (NFC, \n, sem pontas), para comparar a mensagem pedida com a gravada.
    private static string Normalizar(string texto) =>
        texto.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
}
