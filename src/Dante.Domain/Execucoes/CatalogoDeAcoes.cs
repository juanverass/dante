using System.Text.RegularExpressions;

namespace Dante.Domain.Execucoes;

// Risco de uma ação do D.A.N.T.E. (AD-72, #311). Leitura: só lê. EscritaInterna: altera o ledger ou um artefato local do
// próprio D.A.N.T.E. (worktree, branch local), sem sair da máquina. EfeitoExterno: alcança algo fora dela (push, PR,
// comentário, API de terceiros).
public enum ClasseDeRisco
{
    Leitura,
    EscritaInterna,
    EfeitoExterno
}

// Quando uma ação exige aprovação humana do gateway (AD-72). Nunca: executa sem pedido (as ferramentas do
// dante_coordenacao, que já têm autorização própria). Sempre: só com aprovação válida. ConformeModo: quem chama informa se
// o modo da sessão exige aprovação; o mapeamento do modo é do gateway (PR 2), não deste catálogo.
public enum PoliticaDeAprovacao
{
    Nunca,
    ConformeModo,
    Sempre
}

// Forma de um parâmetro de ação. Texto: uma linha, sem caractere de controle. TextoLongo: várias linhas (\n). HashSha256:
// 64 hexadecimais minúsculos. ShaDeCommit: 40 (SHA-1) ou 64 (SHA-256) hexadecimais minúsculos. NomeDeBranch: nome de ref
// local seguro (sem "..", "//", "@{", espaço, ponta "/" ou ".", nem ".lock"). Email: endereço simples, sem espaço nem <>.
public enum TipoDeParametroDaAcao
{
    Texto,
    TextoLongo,
    HashSha256,
    ShaDeCommit,
    NomeDeBranch,
    Email
}

// Todo parâmetro declarado é obrigatório: o payload aprovado é sempre completo, nunca completado depois.
public sealed record ParametroDaAcao(string Nome, TipoDeParametroDaAcao Tipo, int Maximo, string Descricao, int Minimo = 1);

// Ação conhecida pelo gateway (AD-72): nome único no catálogo, risco, política e parâmetros. O que não está em
// CatalogoDeAcoes não existe para o D.A.N.T.E.: não é exposto, pedido nem executado. Construir uma definição fora do
// catálogo não a torna válida: o payload só é montado para a instância do próprio catálogo.
public sealed class DefinicaoDeAcao
{
    public DefinicaoDeAcao(string nome, ClasseDeRisco risco, PoliticaDeAprovacao politica, bool exigeRepositorio,
        string descricao, IReadOnlyList<ParametroDaAcao> parametros)
    {
        Nome = nome;
        Risco = risco;
        Politica = politica;
        ExigeRepositorio = exigeRepositorio;
        Descricao = descricao;
        Parametros = parametros;
    }

    public string Nome { get; }
    public ClasseDeRisco Risco { get; }
    public PoliticaDeAprovacao Politica { get; }
    // O alvo do payload é o alias de um repositório (@alias); sem ele, a ação não tem alvo.
    public bool ExigeRepositorio { get; }
    public string Descricao { get; }
    public IReadOnlyList<ParametroDaAcao> Parametros { get; }

    // Pedido de aprovação só existe para ação que pode exigi-lo.
    public bool Protegida => Politica != PoliticaDeAprovacao.Nunca;

    // modoExigeAprovacao só conta para ConformeModo; vem de quem conhece o modo da sessão.
    public bool ExigeAprovacao(bool modoExigeAprovacao) => Politica switch
    {
        PoliticaDeAprovacao.Sempre => true,
        PoliticaDeAprovacao.ConformeModo => modoExigeAprovacao,
        _ => false
    };
}

// Catálogo fechado do gateway (AD-72, #311). Uma ação nova entra aqui, com migration que atualize a lista de ações
// protegidas aceitas pelo banco (ck_aprovacoes_de_acao_acao); mudar a forma de uma ação é criar outra, nunca editar esta.
public static class CatalogoDeAcoes
{
    public const int TamanhoMaximoDoNome = 63;
    public const string PrefixoDoCoordenacao = "dante_coordenacao.";

    private static readonly Regex FormatoDoNome = new("^[a-z][a-z0-9_]*\\.[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);

    // Ferramentas atuais do dante_coordenacao (#308–#310): autorização própria (canal da sessão, token, perfil, posse da
    // etapa); entram no catálogo com política Nunca para o gateway registrá-las sem mudar o comportamento (PR 2). Os
    // parâmetros são validados pelos contratos delas, não aqui: com política Nunca, nenhum payload é montado.
    public static readonly DefinicaoDeAcao EstadoDaExecucao = Coordenacao("estado_da_execucao", ClasseDeRisco.Leitura,
        "Estado das delegações da execução.");
    public static readonly DefinicaoDeAcao EntregarResultado = Coordenacao("entregar_resultado", ClasseDeRisco.EscritaInterna,
        "Resultado estruturado da etapa, gravado uma vez no ledger.");
    public static readonly DefinicaoDeAcao ConsultarAgente = Coordenacao("consultar_agente", ClasseDeRisco.EscritaInterna,
        "Delegação consulta.v1 a um agente concedido.");
    public static readonly DefinicaoDeAcao ProporPlano = Coordenacao("propor_plano", ClasseDeRisco.EscritaInterna,
        "Plano do coordenador, validado antes do gate.");
    public static readonly DefinicaoDeAcao EntregarSintese = Coordenacao("entregar_sintese", ClasseDeRisco.EscritaInterna,
        "Síntese validada do coordenador.");

    // Primeiro consumidor do gateway (#311, PR 3): commit do artefato aprovado no gate do Roteiro, pelo D.A.N.T.E., na
    // branch da worktree, sem push. O artefato é o hash do diff aprovado no gate; a identidade do autor é explícita.
    public static readonly DefinicaoDeAcao EntregarArtefatoDoRoteiro = new("dante.entregar_artefato_do_roteiro",
        ClasseDeRisco.EscritaInterna, PoliticaDeAprovacao.Sempre, exigeRepositorio: true,
        "Commit local do artefato aprovado do roteiro na branch da worktree, sem push.",
        [
            new("artefato", TipoDeParametroDaAcao.HashSha256, 64, "Hash do diff aprovado no gate do roteiro."),
            new("autor_email", TipoDeParametroDaAcao.Email, 254, "E-mail do autor do commit."),
            new("autor_nome", TipoDeParametroDaAcao.Texto, 100, "Nome do autor do commit."),
            new("base", TipoDeParametroDaAcao.ShaDeCommit, 64, "Commit base da worktree."),
            new("branch", TipoDeParametroDaAcao.NomeDeBranch, 200, "Branch da worktree."),
            new("mensagem", TipoDeParametroDaAcao.TextoLongo, 2000, "Mensagem do commit.")
        ]);

    public static IReadOnlyList<DefinicaoDeAcao> Todas { get; } =
        [EstadoDaExecucao, EntregarResultado, ConsultarAgente, ProporPlano, EntregarSintese, EntregarArtefatoDoRoteiro];

    // As únicas ações que podem ter pedido de aprovação gravado (a lista do check do banco).
    public static IReadOnlyList<DefinicaoDeAcao> Protegidas { get; } = Todas.Where(a => a.Protegida).ToArray();

    // Nega o desconhecido: nome fora do catálogo é nulo, sem tentativa de correspondência aproximada.
    public static DefinicaoDeAcao? Obter(string? nome)
    {
        foreach (var acao in Todas)
            if (string.Equals(acao.Nome, nome, StringComparison.Ordinal)) return acao;
        return null;
    }

    internal static bool NomeValido(string nome) => nome.Length <= TamanhoMaximoDoNome && FormatoDoNome.IsMatch(nome);

    private static DefinicaoDeAcao Coordenacao(string ferramenta, ClasseDeRisco risco, string descricao) =>
        new(PrefixoDoCoordenacao + ferramenta, risco, PoliticaDeAprovacao.Nunca, exigeRepositorio: false, descricao, []);
}
