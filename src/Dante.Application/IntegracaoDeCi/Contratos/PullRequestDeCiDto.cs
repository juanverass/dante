namespace Dante.Application.IntegracaoDeCi;

// PR lido agora no provedor (#238): a fonte do head SHA atual que a consulta do CI correlaciona. Nunca vem de cache:
// um commit novo no PR muda o SHA e as execuções do commit anterior deixam de contar. Repositorio é o consultado, com
// o ID que o provedor confirmou para o repositório de destino do PR; DeFork diz que o head vem de outro repositório
// (o CI que vale continua sendo o do repositório consultado). Titulo vem de quem abriu o PR: já sanitizado, curto e
// exibido só como texto.
public sealed record PullRequestDeCiDto
{
    public const int TamanhoMaximoDoTitulo = 200;

    public PullRequestDeCiDto(RepositorioDeCi repositorio, int numero, string shaDoHead, EstadoDoPullRequestDeCi estado,
        bool rascunho, bool deFork, string titulo, Uri urlOficial)
    {
        Repositorio = repositorio ?? throw new ArgumentNullException(nameof(repositorio));
        if (repositorio.IdNoProvedor is null)
            throw new ArgumentException("PR devolvido pelo provedor precisa do ID do repositório.", nameof(repositorio));
        Numero = CiValidator.ExigirNumeroDoPr(numero);
        ShaDoHead = CiValidator.ExigirSha(shaDoHead);
        Estado = CiValidator.ExigirDefinido(estado, nameof(estado));
        Rascunho = rascunho;
        DeFork = deFork;
        Titulo = string.IsNullOrWhiteSpace(titulo) || titulo.Length > TamanhoMaximoDoTitulo || titulo.Contains('\n')
            ? throw new ArgumentException("Título do PR vazio, longo demais ou com mais de uma linha.", nameof(titulo))
            : titulo;
        UrlOficial = CiValidator.ExigirUrlOficial(urlOficial, nameof(urlOficial));
    }

    public RepositorioDeCi Repositorio { get; }
    public int Numero { get; }
    public string ShaDoHead { get; }
    public EstadoDoPullRequestDeCi Estado { get; }
    public bool Rascunho { get; }
    public bool DeFork { get; }
    public string Titulo { get; }
    public Uri UrlOficial { get; }
}
