using System.Text.RegularExpressions;
using Dante.Domain.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Domain.Agentes;

// Identidade reutilizável de um agente registrado (AD-68), separada da sessão e da execução. O handle é único,
// minúsculo, sem @ (que continua sendo repositório, AD-27) e imutável; nunca volta a ser usado, nem depois de
// arquivado. O comportamento vive nas versões: editar cria a próxima, e a atual é a que novas sessões e execuções
// congelam. Nome e descrição são apresentação e mudam sem versão nova.
public sealed class Agente : EntidadeBase
{
    public const int TamanhoMaximoDoHandle = 31;
    public const int TamanhoMaximoDoNome = 100;
    public const int TamanhoMaximoDaDescricao = 500;

    private static readonly Regex FormatoDoHandle = new("^[a-z][a-z0-9-]{1,30}$", RegexOptions.CultureInvariant);

    // Os embutidos e as palavras que os comandos já interpretam na posição do agente nunca viram handle registrado:
    // um handle assim tornaria ambígua a precedência (agente explícito, modo, repositório).
    public static IReadOnlySet<string> HandlesReservados { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "claude", "codex", "manual", "approval", "auto", "plan", "full", "default", "general", "none", "all", "todos",
        "list", "start", "select", "stop", "close", "set"
    };

    private Agente()
    {
    }

    public Agente(string handle, string? nome, string? descricao, long idDoCriador, DateTimeOffset criadoEm)
    {
        ValidarHandle(handle);
        Handle = handle;
        Nome = TextoOpcional(nome, TamanhoMaximoDoNome, nameof(nome));
        Descricao = TextoOpcional(descricao, TamanhoMaximoDaDescricao, nameof(descricao));
        IdDoCriador = idDoCriador;
        CriadoEm = criadoEm;
        AtualizadoEm = criadoEm;
    }

    public string Handle { get; private set; } = "";
    public string? Nome { get; private set; }
    public string? Descricao { get; private set; }
    public EstadoDoAgente Estado { get; private set; } = EstadoDoAgente.Ativo;
    public Guid? IdVersaoAtual { get; private set; }
    public int NumeroDaVersaoAtual { get; private set; }
    public long IdDoCriador { get; private set; }
    public DateTimeOffset CriadoEm { get; private set; }
    public DateTimeOffset AtualizadoEm { get; private set; }

    public static bool HandleValido(string? handle) =>
        handle is not null && FormatoDoHandle.IsMatch(handle) && !HandlesReservados.Contains(handle);

    public static void ValidarHandle(string? handle)
    {
        if (handle is null || !FormatoDoHandle.IsMatch(handle))
            throw new ArgumentException(
                "Handle inválido: use de 2 a 31 caracteres minúsculos (letras, números e -), começando por letra, sem @.",
                nameof(handle));
        if (HandlesReservados.Contains(handle))
            throw new ArgumentException($"O handle {handle} é reservado.", nameof(handle));
    }

    // A primeira versão nasce com o agente; as seguintes, de cada edição. Arquivado não recebe versão.
    public VersaoDoAgente NovaVersao(ConfiguracaoDoAgente configuracao, long idDoAutor, DateTimeOffset quando)
    {
        ArgumentNullException.ThrowIfNull(configuracao);
        if (Estado == EstadoDoAgente.Arquivado) throw new InvalidOperationException("Agente arquivado não recebe versões.");
        var versao = new VersaoDoAgente(this, NumeroDaVersaoAtual + 1, configuracao, idDoAutor, quando);
        IdVersaoAtual = versao.Id;
        NumeroDaVersaoAtual = versao.Numero;
        AtualizadoEm = quando;
        return versao;
    }

    public void Apresentar(string? nome, string? descricao, DateTimeOffset quando)
    {
        if (Estado == EstadoDoAgente.Arquivado) throw new InvalidOperationException("Agente arquivado não muda.");
        Nome = TextoOpcional(nome, TamanhoMaximoDoNome, nameof(nome));
        Descricao = TextoOpcional(descricao, TamanhoMaximoDaDescricao, nameof(descricao));
        AtualizadoEm = quando;
    }

    // Nada novo começa com um agente desativado; o que já está ativo segue até o fim.
    public void Desativar(DateTimeOffset quando) => Transitar(EstadoDoAgente.Ativo, EstadoDoAgente.Desativado, quando);

    public void Reativar(DateTimeOffset quando) => Transitar(EstadoDoAgente.Desativado, EstadoDoAgente.Ativo, quando);

    // Só a partir de desativado: arquivar exige primeiro parar de aceitar trabalho novo.
    public void Arquivar(DateTimeOffset quando) => Transitar(EstadoDoAgente.Desativado, EstadoDoAgente.Arquivado, quando);

    private void Transitar(EstadoDoAgente de, EstadoDoAgente para, DateTimeOffset quando)
    {
        if (Estado != de) throw new TransicaoInvalidaException("agente", Estado.ToString(), para.ToString());
        Estado = para;
        AtualizadoEm = quando;
    }

    private static string? TextoOpcional(string? valor, int tamanhoMaximo, string parametro)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;
        var texto = valor.Trim();
        if (texto.Length > tamanhoMaximo) throw new ArgumentException("Texto excede o tamanho máximo.", parametro);
        return texto;
    }
}
