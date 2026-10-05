using Dante.Domain.Comum;

namespace Dante.Domain.Projetos;

// Trabalho persistente do Brain dentro de exatamente um EspacoDeConhecimento (AD-33, AD-42). Sobrevive às sessões e
// concentra o conhecimento do trabalho; não é repositório Git, alias nem diretório de execução. Um repositório
// cadastrado pode ser associado como referência opcional, trocada ou removida sem mudar a identidade do projeto.
public sealed class Projeto : EntidadeBase
{
    public const int TamanhoMaximoDoNome = 100;
    public const int TamanhoMaximoDaDescricao = 1000;
    public const int TamanhoMaximoDoAliasDoRepositorio = 100;

    public Projeto(Guid idEspacoDeConhecimento, string nome, string? descricao = null)
    {
        if (idEspacoDeConhecimento == Guid.Empty)
            throw new ArgumentException("Espaço de conhecimento do projeto é obrigatório.", nameof(idEspacoDeConhecimento));
        IdEspacoDeConhecimento = idEspacoDeConhecimento;
        Nome = NomeValido(nome);
        Descricao = DescricaoValida(descricao);
    }

    // O projeto pertence a um único espaço durante toda a vida; o escopo do Brain parte dele.
    public Guid IdEspacoDeConhecimento { get; private set; }
    public string Nome { get; private set; }
    // Descrição e objetivo do trabalho.
    public string? Descricao { get; private set; }
    // Referência opcional a um repositório cadastrado, pelo alias; nunca identidade.
    public string? AliasDoRepositorio { get; private set; }
    public EstadoDoProjeto Estado { get; private set; } = EstadoDoProjeto.Ativo;

    public bool Arquivado => Estado == EstadoDoProjeto.Arquivado;

    // Valida tudo antes de alterar: falha na descrição não deixa o nome já trocado.
    public void Atualizar(string nome, string? descricao)
    {
        GarantirAtivo();
        var nomeValido = NomeValido(nome);
        Descricao = DescricaoValida(descricao);
        Nome = nomeValido;
    }

    public void AssociarRepositorio(string aliasDoRepositorio)
    {
        GarantirAtivo();
        if (string.IsNullOrWhiteSpace(aliasDoRepositorio))
            throw new ArgumentException("Alias do repositório é obrigatório.", nameof(aliasDoRepositorio));
        aliasDoRepositorio = aliasDoRepositorio.Trim();
        if (aliasDoRepositorio.Length > TamanhoMaximoDoAliasDoRepositorio ||
            aliasDoRepositorio.Any(caractere => char.IsWhiteSpace(caractere) || char.IsControl(caractere)))
            throw new ArgumentException("Alias do repositório inválido.", nameof(aliasDoRepositorio));
        AliasDoRepositorio = aliasDoRepositorio;
    }

    public void DesassociarRepositorio()
    {
        GarantirAtivo();
        AliasDoRepositorio = null;
    }

    // Arquivar preserva o projeto, a associação e o conhecimento; só o torna somente leitura até ser reativado.
    public void Arquivar()
    {
        if (Arquivado) throw new InvalidOperationException("O projeto já está arquivado.");
        Estado = EstadoDoProjeto.Arquivado;
    }

    public void Reativar()
    {
        if (!Arquivado) throw new InvalidOperationException("O projeto não está arquivado.");
        Estado = EstadoDoProjeto.Ativo;
    }

    private void GarantirAtivo()
    {
        if (Arquivado) throw new InvalidOperationException("Projeto arquivado não pode ser alterado; reative-o antes.");
    }

    private static string NomeValido(string nome)
    {
        if (string.IsNullOrWhiteSpace(nome)) throw new ArgumentException("Nome do projeto é obrigatório.", nameof(nome));
        nome = nome.Trim();
        if (nome.Length > TamanhoMaximoDoNome)
            throw new ArgumentException($"Nome do projeto excede {TamanhoMaximoDoNome} caracteres.", nameof(nome));
        if (nome.Any(char.IsControl))
            throw new ArgumentException("Nome do projeto não pode conter caracteres de controle.", nameof(nome));
        return nome;
    }

    private static string? DescricaoValida(string? descricao)
    {
        if (string.IsNullOrWhiteSpace(descricao)) return null;
        descricao = descricao.Trim();
        if (descricao.Length > TamanhoMaximoDaDescricao)
            throw new ArgumentException($"Descrição do projeto excede {TamanhoMaximoDaDescricao} caracteres.",
                nameof(descricao));
        return descricao;
    }
}

public enum EstadoDoProjeto
{
    Ativo,
    Arquivado
}
