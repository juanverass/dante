using System.Text.RegularExpressions;
using Dante.Domain.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Domain.Grupos;

// Grupo nomeado e reutilizável de agentes registrados (AD-69): unidade de execução, não agrupamento visual. O nome é
// único, minúsculo, imutável e nunca reutilizado, nem depois de arquivado. O comportamento vive nas versões: editar
// cria a próxima, mas execuções novas usam só a versão ativada por decisão humana (#308, decisão F), que pode ser
// mais antiga que a última criada.
public sealed class GrupoDeAgentes : EntidadeBase
{
    public const int TamanhoMaximoDoNome = 31;
    public const int TamanhoMaximoDaDescricao = 500;

    private static readonly Regex FormatoDoNome = new("^[a-z][a-z0-9-]{1,30}$", RegexOptions.CultureInvariant);

    // Palavras que /grupo interpreta como ação nunca viram nome: /grupo <nome> <tarefa> seria ambíguo.
    public static IReadOnlySet<string> NomesReservados { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "criar", "create", "ver", "show", "editar", "edit", "ativar", "desativar", "reativar", "arquivar", "listar",
        "list", "todos", "all", "descricao", "ajuda", "help"
    };

    private GrupoDeAgentes()
    {
    }

    public GrupoDeAgentes(string nome, string? descricao, long idDoCriador, DateTimeOffset criadoEm)
    {
        ValidarNome(nome);
        Nome = nome;
        Descricao = TextoOpcional(descricao);
        IdDoCriador = idDoCriador;
        CriadoEm = criadoEm;
        AtualizadoEm = criadoEm;
    }

    public string Nome { get; private set; } = "";
    public string? Descricao { get; private set; }
    public EstadoDoGrupo Estado { get; private set; } = EstadoDoGrupo.Ativo;
    // Última versão criada.
    public Guid? IdVersaoAtual { get; private set; }
    public int NumeroDaVersaoAtual { get; private set; }
    // Versão liberada por decisão humana para execuções novas; nula até a primeira ativação.
    public Guid? IdVersaoAtiva { get; private set; }
    public int? NumeroDaVersaoAtiva { get; private set; }
    public long IdDoCriador { get; private set; }
    public DateTimeOffset CriadoEm { get; private set; }
    public DateTimeOffset AtualizadoEm { get; private set; }

    public static bool NomeValido(string? nome) =>
        nome is not null && FormatoDoNome.IsMatch(nome) && !NomesReservados.Contains(nome);

    public static void ValidarNome(string? nome)
    {
        if (nome is null || !FormatoDoNome.IsMatch(nome))
            throw new ArgumentException(
                "Nome de grupo inválido: use de 2 a 31 caracteres minúsculos (letras, números e -), começando por letra.",
                nameof(nome));
        if (NomesReservados.Contains(nome)) throw new ArgumentException($"O nome {nome} é reservado.", nameof(nome));
    }

    public VersaoDoGrupo NovaVersao(ConfiguracaoDoGrupo configuracao, long idDoAutor, DateTimeOffset quando)
    {
        ArgumentNullException.ThrowIfNull(configuracao);
        if (Estado == EstadoDoGrupo.Arquivado) throw new InvalidOperationException("Grupo arquivado não recebe versões.");
        var versao = new VersaoDoGrupo(this, NumeroDaVersaoAtual + 1, configuracao, idDoAutor, quando);
        IdVersaoAtual = versao.Id;
        NumeroDaVersaoAtual = versao.Numero;
        AtualizadoEm = quando;
        return versao;
    }

    // A decisão humana é vinculada ao hash: quem ativa confirma o hash que viu, e uma versão adulterada (hash
    // recalculado diferente do gravado) nunca é ativada. Ativar de novo a versão já ativa é recusado.
    public AtivacaoDeVersaoDoGrupo Ativar(VersaoDoGrupo versao, string hashConfirmado, long idDoAutor, DateTimeOffset quando)
    {
        ArgumentNullException.ThrowIfNull(versao);
        if (Estado != EstadoDoGrupo.Ativo)
            throw new InvalidOperationException("Só um grupo ativo tem versão ativada.");
        if (versao.IdGrupo != Id) throw new InvalidOperationException("A versão é de outro grupo.");
        if (!versao.Integra) throw new InvalidOperationException("A versão não confere com o hash gravado.");
        if (!ConfereHash(versao.HashDaConfiguracao, hashConfirmado))
            throw new InvalidOperationException("O hash confirmado não é o desta versão.");
        if (IdVersaoAtiva == versao.Id) throw new InvalidOperationException("Esta versão já está ativa.");
        var ativacao = new AtivacaoDeVersaoDoGrupo(versao, idDoAutor, quando);
        IdVersaoAtiva = versao.Id;
        NumeroDaVersaoAtiva = versao.Numero;
        AtualizadoEm = quando;
        return ativacao;
    }

    // Prefixo mínimo do hash que a pessoa precisa confirmar: o mesmo exibido nas consultas.
    public const int PrefixoMinimoDoHash = 12;

    public static bool ConfereHash(string hash, string? confirmado)
    {
        var texto = confirmado?.Trim().ToLowerInvariant();
        return texto is { Length: >= PrefixoMinimoDoHash } && texto.Length <= hash.Length &&
            hash.StartsWith(texto, StringComparison.Ordinal);
    }

    public void Descrever(string? descricao, DateTimeOffset quando)
    {
        if (Estado == EstadoDoGrupo.Arquivado) throw new InvalidOperationException("Grupo arquivado não muda.");
        Descricao = TextoOpcional(descricao);
        AtualizadoEm = quando;
    }

    // Nada novo começa com um grupo desativado; o que já começou segue até o fim.
    public void Desativar(DateTimeOffset quando) => Transitar(EstadoDoGrupo.Ativo, EstadoDoGrupo.Desativado, quando);

    public void Reativar(DateTimeOffset quando) => Transitar(EstadoDoGrupo.Desativado, EstadoDoGrupo.Ativo, quando);

    public void Arquivar(DateTimeOffset quando) => Transitar(EstadoDoGrupo.Desativado, EstadoDoGrupo.Arquivado, quando);

    private void Transitar(EstadoDoGrupo de, EstadoDoGrupo para, DateTimeOffset quando)
    {
        if (Estado != de) throw new TransicaoInvalidaException("grupo", Estado.ToString(), para.ToString());
        Estado = para;
        AtualizadoEm = quando;
    }

    private static string? TextoOpcional(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;
        var texto = valor.Trim();
        if (texto.Length > TamanhoMaximoDaDescricao)
            throw new ArgumentException("Descrição excede o tamanho máximo.", nameof(valor));
        return texto;
    }
}
