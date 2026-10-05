using Dante.Domain.Comum;

namespace Dante.Domain.EspacosDeConhecimento;

// Limite lógico de organização e isolamento do Brain (AD-33, AD-40), no papel conceitual de um vault: Pessoal,
// Trabalho, uma empresa, Estudos. Projetos e conhecimento referenciam o espaço pelo Id; conhecimento pode pertencer
// direto ao espaço, sem Projeto. O nome é apresentação e pode mudar; o Id e o proprietário não mudam.
public sealed class EspacoDeConhecimento : EntidadeBase
{
    public const int TamanhoMaximoDoNome = 100;
    public const int TamanhoMaximoDaDescricao = 1000;

    public EspacoDeConhecimento(Guid idUsuario, string nome, string? descricao = null)
    {
        if (idUsuario == Guid.Empty)
            throw new ArgumentException("Proprietário do espaço de conhecimento é obrigatório.", nameof(idUsuario));
        IdUsuario = idUsuario;
        Nome = NomeValido(nome);
        Descricao = DescricaoValida(descricao);
    }

    // Proprietário do espaço; o escopo do Brain parte dele (AD-33).
    public Guid IdUsuario { get; private set; }
    public string Nome { get; private set; }
    public string? Descricao { get; private set; }
    public EstadoDoEspacoDeConhecimento Estado { get; private set; } = EstadoDoEspacoDeConhecimento.Ativo;

    public bool Arquivado => Estado == EstadoDoEspacoDeConhecimento.Arquivado;

    // Valida tudo antes de alterar: falha na descrição não deixa o nome já trocado.
    public void Atualizar(string nome, string? descricao)
    {
        if (Arquivado)
            throw new InvalidOperationException("Espaço de conhecimento arquivado não pode ser alterado; reative-o antes.");
        var nomeValido = NomeValido(nome);
        Descricao = DescricaoValida(descricao);
        Nome = nomeValido;
    }

    // Arquivar preserva o espaço e o que ele contém; só o torna somente leitura até ser reativado.
    public void Arquivar()
    {
        if (Arquivado) throw new InvalidOperationException("O espaço de conhecimento já está arquivado.");
        Estado = EstadoDoEspacoDeConhecimento.Arquivado;
    }

    public void Reativar()
    {
        if (!Arquivado) throw new InvalidOperationException("O espaço de conhecimento não está arquivado.");
        Estado = EstadoDoEspacoDeConhecimento.Ativo;
    }

    private static string NomeValido(string nome)
    {
        if (string.IsNullOrWhiteSpace(nome))
            throw new ArgumentException("Nome do espaço de conhecimento é obrigatório.", nameof(nome));
        nome = nome.Trim();
        if (nome.Length > TamanhoMaximoDoNome)
            throw new ArgumentException(
                $"Nome do espaço de conhecimento excede {TamanhoMaximoDoNome} caracteres.", nameof(nome));
        if (nome.Any(char.IsControl))
            throw new ArgumentException("Nome do espaço de conhecimento não pode conter caracteres de controle.",
                nameof(nome));
        return nome;
    }

    private static string? DescricaoValida(string? descricao)
    {
        if (string.IsNullOrWhiteSpace(descricao)) return null;
        descricao = descricao.Trim();
        if (descricao.Length > TamanhoMaximoDaDescricao)
            throw new ArgumentException(
                $"Descrição do espaço de conhecimento excede {TamanhoMaximoDaDescricao} caracteres.", nameof(descricao));
        return descricao;
    }
}

public enum EstadoDoEspacoDeConhecimento
{
    Ativo,
    Arquivado
}
