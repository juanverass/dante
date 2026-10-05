namespace Dante.Application.Contextos;

// Porta de leitura do catálogo de repositórios cadastrados (AD-07, AD-10); escrita e formato persistido ficam no adapter.
public interface ICatalogoDeRepositorios
{
    IReadOnlyList<RepositorioCadastrado> Listar();

    // Null quando o alias não está cadastrado; ArgumentException quando o alias é inválido.
    RepositorioCadastrado? Obter(string alias);

    // InvalidOperationException quando o ambiente do repositório não pode ser resolvido no host.
    ResolvedRepositoryEnvironment ResolverAmbiente(string alias);
}
