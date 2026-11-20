using Dante.Domain.Comum;

namespace Dante.Domain.Execucoes;

// Síntese validada do coordenador de uma Coordenada (AD-75, D7, #310 PR 3): a forma canônica que ValidacaoDaSintese
// aceitou contra a trilha mecânica, com o hash calculado aqui, a etapa de síntese e a sessão vinculada a ela, que a
// entregou. Uma por execução, imutável e nunca removida (trigger). Separada da trilha mecânica: a validação é estrutural
// (cobertura de todas as unidades, citações aos resultados persistidos, divergência com ao menos duas unidades) e não
// atesta o mérito do que o coordenador afirma nem consenso entre os agentes.
public sealed class SinteseDaCoordenacao : EntidadeBase
{
    public const int TamanhoDoHash = 64;

    private SinteseDaCoordenacao()
    {
    }

    public SinteseDaCoordenacao(Execucao execucao, EtapaDaExecucao etapa, SinteseValidada sintese,
        DateTimeOffset registradaEm)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        ArgumentNullException.ThrowIfNull(etapa);
        ArgumentNullException.ThrowIfNull(sintese);
        if (execucao.Tipo != TipoDeExecucao.Coordenada) throw new InvalidOperationException("Síntese só numa Coordenada.");
        if (execucao.Terminal) throw new InvalidOperationException("Execução encerrada não recebe síntese.");
        if (etapa.IdExecucao != execucao.Id || etapa.Tipo != TipoDeEtapa.TurnoDeAgente || etapa.Papel is null ||
            etapa.IdPlano is not null || etapa.Estado != EstadoDaEtapa.EmExecucao)
            throw new InvalidOperationException("Síntese só pela etapa de síntese em curso desta Coordenada.");
        if (etapa.IdSessaoDeAgente is not { } sessao)
            throw new InvalidOperationException("A sessão que entrega a síntese precisa estar vinculada à etapa.");
        if (string.IsNullOrWhiteSpace(sintese.Conteudo))
            throw new ArgumentException("Síntese sem conteúdo.", nameof(sintese));
        IdExecucao = execucao.Id;
        IdEtapa = etapa.Id;
        IdSessaoDeAgente = sessao;
        Conteudo = sintese.Conteudo;
        // O hash gravado é o recalculado do conteúdo, nunca um informado.
        Hash = ValidacaoDoPlano.Hash(sintese.Conteudo);
        RegistradaEm = registradaEm;
    }

    public Guid IdExecucao { get; private set; }
    public Guid IdEtapa { get; private set; }
    public Guid IdSessaoDeAgente { get; private set; }
    // Forma canônica (texto, não jsonb: o hash é dos bytes exatos).
    public string Conteudo { get; private set; } = "";
    public string Hash { get; private set; } = "";
    public DateTimeOffset RegistradaEm { get; private set; }
}
