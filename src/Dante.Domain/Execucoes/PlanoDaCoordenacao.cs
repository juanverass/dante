using Dante.Domain.Comum;

namespace Dante.Domain.Execucoes;

// AguardandoAprovacao até a decisão humana; Aprovado autoriza só materializar e despachar este plano somente leitura;
// Recusado e Cancelado são terminais; Substituido: uma revisão posterior tomou o lugar deste plano (que continua
// gravado, com as etapas concluídas dele).
public enum EstadoDoPlano
{
    AguardandoAprovacao,
    Aprovado,
    Recusado,
    Substituido,
    Cancelado
}

// Por que uma revisão do plano existe (D6): a inicial; uma unidade do plano anterior terminou sem resposta; ou a pessoa
// pediu correção no gate do plano. Nenhuma outra causa cria revisão.
public enum MotivoDoPlano
{
    Inicial,
    UnidadeSemResposta,
    PedidoHumano
}

public sealed record MaterializacaoDoPlano(IReadOnlyList<EtapaDaExecucao> Etapas, IReadOnlyList<DependenciaDeEtapa> Dependencias);

// Plano de uma Coordenada (AD-75, #310): uma revisão imutável da forma canônica validada (ValidacaoDoPlano), com o hash
// calculado aqui. Revisões formam uma cadeia (anterior → seguinte), no máximo 3 versões por execução (a inicial e duas
// substitutas), e nenhuma é apagada: substituir marca a anterior Substituido e ignora, com motivo, só as etapas
// pendentes dela. A aprovação é a decisão humana do gate (PedidoDeAprovacao com finalidade PlanoSomenteLeitura) sobre
// exatamente este hash; ela autoriza só este plano somente leitura e não é aprovação do gateway da #311 (nunca concede
// escrita, commit, push nem outra ação protegida). O banco também protege conteúdo, hash e transições.
public sealed class PlanoDaCoordenacao : EntidadeBase
{
    public const int RevisoesMaximas = 3;
    public const int TamanhoDoHash = 64;

    private PlanoDaCoordenacao()
    {
    }

    private PlanoDaCoordenacao(Execucao execucao, int revisao, Guid? idPlanoAnterior, MotivoDoPlano motivo,
        PlanoValidado plano, DateTimeOffset criadoEm)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        ArgumentNullException.ThrowIfNull(plano);
        if (execucao.Tipo != TipoDeExecucao.Coordenada) throw new InvalidOperationException("Plano só numa Coordenada.");
        if (execucao.Terminal) throw new InvalidOperationException("Execução encerrada não recebe plano.");
        // A forma e o hash gravados são os recalculados aqui, nunca os informados por quem propôs.
        var conferido = ValidacaoDoPlano.LerCanonico(plano.Conteudo);
        if (conferido.Unidades.Count is 0 || conferido.Unidades.Count > ValidacaoDoPlano.MaximoDeUnidades)
            throw new InvalidOperationException($"Plano com 1 a {ValidacaoDoPlano.MaximoDeUnidades} unidades.");
        IdExecucao = execucao.Id;
        Revisao = revisao;
        IdPlanoAnterior = idPlanoAnterior;
        Motivo = motivo;
        Conteudo = conferido.Conteudo;
        Hash = conferido.Hash;
        QuantidadeDeUnidades = conferido.Unidades.Count;
        CriadoEm = criadoEm;
    }

    public Guid IdExecucao { get; private set; }
    // 1 na inicial; cada substituta é a anterior mais um, até RevisoesMaximas.
    public int Revisao { get; private set; }
    public Guid? IdPlanoAnterior { get; private set; }
    public MotivoDoPlano Motivo { get; private set; }
    // Forma canônica (texto, não jsonb: o hash é dos bytes exatos).
    public string Conteudo { get; private set; } = "";
    public string Hash { get; private set; } = "";
    public int QuantidadeDeUnidades { get; private set; }
    public EstadoDoPlano Estado { get; private set; } = EstadoDoPlano.AguardandoAprovacao;
    public DateTimeOffset CriadoEm { get; private set; }
    public DateTimeOffset? DecididoEm { get; private set; }
    public DateTimeOffset? SubstituidoEm { get; private set; }

    public static PlanoDaCoordenacao Inicial(Execucao execucao, CoordenacaoDaExecucao coordenacao, PlanoValidado plano,
        DateTimeOffset criadoEm)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        ArgumentNullException.ThrowIfNull(coordenacao);
        if (coordenacao.IdExecucao != execucao.Id)
            throw new InvalidOperationException("A coordenação é de outra execução.");
        return new PlanoDaCoordenacao(execucao, 1, null, MotivoDoPlano.Inicial, plano, criadoEm);
    }

    // Revisão substituta (D6), só por transição explícita:
    // * UnidadeSemResposta: o anterior está aprovado, nenhuma unidade dele está em curso e ao menos uma terminou sem
    //   resposta (Falhou ou Cancelada). etapasDoAnterior são todas as etapas materializadas do anterior;
    // * PedidoHumano: a pessoa pediu correção no gate do anterior (pedido decidido CorrecaoSolicitada daquele plano).
    // A anterior passa a Substituido; as etapas pendentes dela (Pendente ou Pronta) ficam Ignorada com o motivo; as
    // concluídas e as terminadas continuam como estão. Nada é apagado.
    public static PlanoDaCoordenacao Revisar(Execucao execucao, PlanoDaCoordenacao anterior, MotivoDoPlano motivo,
        PlanoValidado plano, IReadOnlyList<EtapaDaExecucao> etapasDoAnterior, PedidoDeAprovacao? pedidoHumano,
        DateTimeOffset criadoEm)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        ArgumentNullException.ThrowIfNull(anterior);
        ArgumentNullException.ThrowIfNull(etapasDoAnterior);
        if (anterior.IdExecucao != execucao.Id) throw new InvalidOperationException("O plano anterior é de outra execução.");
        if (anterior.Revisao + 1 > RevisoesMaximas)
            throw new InvalidOperationException(
                $"Limite de {RevisoesMaximas} versões do plano atingido; nada é replanejado sem nova decisão.");
        var doAnterior = 0;
        foreach (var etapa in etapasDoAnterior)
            if (etapa.IdPlano != anterior.Id || etapa.IdExecucao != execucao.Id)
                throw new ArgumentException("Etapa que não é do plano anterior.", nameof(etapasDoAnterior));
            else doAnterior++;
        switch (motivo)
        {
            case MotivoDoPlano.UnidadeSemResposta:
                if (anterior.Estado != EstadoDoPlano.Aprovado)
                    throw new InvalidOperationException("Só um plano aprovado é replanejado por unidade sem resposta.");
                if (doAnterior != anterior.QuantidadeDeUnidades)
                    throw new ArgumentException("Informe todas as etapas do plano anterior.", nameof(etapasDoAnterior));
                var semResposta = false;
                foreach (var etapa in etapasDoAnterior)
                {
                    if (etapa.Estado is EstadoDaEtapa.EmExecucao or EstadoDaEtapa.AguardandoAprovacao
                        or EstadoDaEtapa.AguardandoEntrada or EstadoDaEtapa.Interrompida)
                        throw new InvalidOperationException("Há unidade do plano anterior em curso; replanejar a interromperia.");
                    semResposta |= etapa.Estado is EstadoDaEtapa.Falhou or EstadoDaEtapa.Cancelada;
                }
                if (!semResposta)
                    throw new InvalidOperationException("Nenhuma unidade do plano anterior terminou sem resposta.");
                break;
            case MotivoDoPlano.PedidoHumano:
                if (pedidoHumano is null || pedidoHumano.IdPlano != anterior.Id ||
                    pedidoHumano.Estado != EstadoDoPedidoDeAprovacao.CorrecaoSolicitada)
                    throw new InvalidOperationException("Replanejar a pedido exige a correção pedida no gate deste plano.");
                if (anterior.Estado != EstadoDoPlano.AguardandoAprovacao)
                    throw new InvalidOperationException("O plano anterior não aguarda mais decisão.");
                break;
            default:
                throw new ArgumentException("Uma revisão substituta é por unidade sem resposta ou pedido humano.",
                    nameof(motivo));
        }
        var novo = new PlanoDaCoordenacao(execucao, anterior.Revisao + 1, anterior.Id, motivo, plano, criadoEm);
        anterior.Estado = EstadoDoPlano.Substituido;
        anterior.SubstituidoEm = criadoEm;
        foreach (var etapa in etapasDoAnterior)
            if (etapa.Estado is EstadoDaEtapa.Pendente or EstadoDaEtapa.Pronta)
                etapa.Ignorar($"Unidade substituída pela revisão {novo.Revisao} do plano.");
        return novo;
    }

    // Relê a forma gravada e confere o hash: um conteúdo adulterado não é usado.
    public PlanoValidado Ler()
    {
        var plano = ValidacaoDoPlano.LerCanonico(Conteudo);
        if (plano.Hash != Hash) throw new InvalidOperationException("O conteúdo do plano não reproduz o hash gravado.");
        return plano;
    }

    // A decisão do gate sobre este plano: o pedido é deste plano, tem a finalidade do plano e o hash deste conteúdo.
    public void Aprovar(PedidoDeAprovacao pedido) => Decidir(pedido, EstadoDoPedidoDeAprovacao.Aprovado, EstadoDoPlano.Aprovado);

    public void Recusar(PedidoDeAprovacao pedido) => Decidir(pedido, EstadoDoPedidoDeAprovacao.Recusado, EstadoDoPlano.Recusado);

    // A execução foi cancelada enquanto o plano esperava a decisão.
    public void Cancelar(DateTimeOffset quando)
    {
        if (Estado != EstadoDoPlano.AguardandoAprovacao) throw Invalida(EstadoDoPlano.Cancelado);
        Estado = EstadoDoPlano.Cancelado;
        DecididoEm = quando;
    }

    // Etapas e dependências do plano aprovado, numeradas a partir de primeiroNumero na ordem topológica gravada, cada
    // unidade atribuída ao participante do papel no snapshot (a versão congelada, não a atual do cadastro). O snapshot
    // precisa reproduzir o hash; o coordenador nunca recebe unidade. Materializar de novo o mesmo plano é recusado
    // pelo banco (unidade única por plano).
    public MaterializacaoDoPlano Materializar(Execucao execucao, CoordenacaoDaExecucao coordenacao,
        IReadOnlyList<ParticipanteDaExecucao> participantes, int primeiroNumero, DateTimeOffset criadaEm)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        ArgumentNullException.ThrowIfNull(coordenacao);
        ArgumentNullException.ThrowIfNull(participantes);
        if (Estado != EstadoDoPlano.Aprovado) throw new InvalidOperationException("Só um plano aprovado é materializado.");
        if (execucao.Id != IdExecucao || coordenacao.IdExecucao != IdExecucao)
            throw new InvalidOperationException("Plano, coordenação e execução precisam ser os mesmos.");
        if (!coordenacao.SnapshotConfere(participantes))
            throw new InvalidOperationException("O snapshot da coordenação não confere com os participantes gravados.");
        var plano = Ler();
        var etapas = new List<EtapaDaExecucao>();
        var porUnidade = new Dictionary<string, EtapaDaExecucao>(StringComparer.Ordinal);
        var dependencias = new List<DependenciaDeEtapa>();
        var numero = primeiroNumero;
        foreach (var unidade in plano.Unidades)
        {
            ParticipanteDaExecucao? destinatario = null;
            foreach (var participante in participantes)
                if (participante.Papel == unidade.Papel) destinatario = participante;
            if (destinatario is null || destinatario.Papel == coordenacao.PapelDoCoordenador)
                throw new InvalidOperationException($"O papel {unidade.Papel} não pode receber unidade nesta execução.");
            var etapa = EtapaDaExecucao.DaUnidade(execucao, numero++, this, unidade.Id, destinatario, criadaEm);
            foreach (var requerida in unidade.DependeDe)
                dependencias.Add(new DependenciaDeEtapa(etapa, porUnidade[requerida]));
            porUnidade[unidade.Id] = etapa;
            etapas.Add(etapa);
        }
        return new MaterializacaoDoPlano(etapas.AsReadOnly(), dependencias.AsReadOnly());
    }

    // A unidade de uma etapa materializada deste plano.
    public UnidadeDoPlano Unidade(EtapaDaExecucao etapa)
    {
        ArgumentNullException.ThrowIfNull(etapa);
        if (etapa.IdPlano != Id) throw new InvalidOperationException("A etapa não é deste plano.");
        foreach (var unidade in Ler().Unidades)
            if (unidade.Id == etapa.Unidade) return unidade;
        throw new InvalidOperationException("A etapa aponta para uma unidade que o plano não tem.");
    }

    private void Decidir(PedidoDeAprovacao pedido, EstadoDoPedidoDeAprovacao esperado, EstadoDoPlano para)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        if (Estado != EstadoDoPlano.AguardandoAprovacao) throw Invalida(para);
        if (pedido.Finalidade != FinalidadeDaAprovacao.PlanoSomenteLeitura || pedido.IdPlano != Id ||
            pedido.IdExecucao != IdExecucao || pedido.HashDoArtefato != Hash || pedido.Estado != esperado)
            throw new InvalidOperationException("A decisão não é sobre este plano e este conteúdo.");
        Estado = para;
        DecididoEm = pedido.DecididoEm;
    }

    private TransicaoInvalidaException Invalida(EstadoDoPlano para) => new("plano", Estado.ToString(), para.ToString());
}
