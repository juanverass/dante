using Dante.Domain.Projetos;

namespace Dante.Tests;

public sealed class ProjetoTests
{
    private static readonly Guid Espaco = Guid.NewGuid();

    [Fact]
    public void NewProjectBelongsToItsSpaceIsActiveAndHasNoRepository()
    {
        var projeto = new Projeto(Espaco, "  Dante  ", "  Orquestrar agentes  ");

        Assert.NotEqual(Guid.Empty, projeto.Id);
        Assert.Equal(Espaco, projeto.IdEspacoDeConhecimento);
        Assert.Equal("Dante", projeto.Nome);
        Assert.Equal("Orquestrar agentes", projeto.Descricao);
        Assert.Null(projeto.AliasDoRepositorio);
        Assert.Equal(EstadoDoProjeto.Ativo, projeto.Estado);
        Assert.NotEqual(projeto.Id, new Projeto(Espaco, "Dante").Id);
    }

    [Fact]
    public void SpaceIsRequired() => Assert.Throws<ArgumentException>(() => new Projeto(Guid.Empty, "Dante"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Linha\nquebrada")]
    public void NameIsRequiredAndPrintable(string? nome) =>
        Assert.Throws<ArgumentException>(() => new Projeto(Espaco, nome!));

    [Fact]
    public void NameAndDescriptionHaveMaximumLengths()
    {
        var nome = new string('n', Projeto.TamanhoMaximoDoNome);
        var descricao = new string('d', Projeto.TamanhoMaximoDaDescricao);

        Assert.Equal(nome, new Projeto(Espaco, nome, descricao).Nome);
        Assert.Throws<ArgumentException>(() => new Projeto(Espaco, nome + "n"));
        Assert.Throws<ArgumentException>(() => new Projeto(Espaco, "Dante", descricao + "d"));
        Assert.Null(new Projeto(Espaco, "Dante", "   ").Descricao);
    }

    [Fact]
    public void UpdateIsAtomicAndKeepsIdentityAndSpace()
    {
        var projeto = new Projeto(Espaco, "Dante", "Antiga");
        var id = projeto.Id;

        Assert.Throws<ArgumentException>(() => projeto.Atualizar("Novo", new string('d', 1001)));
        Assert.Equal(("Dante", "Antiga"), (projeto.Nome, projeto.Descricao));

        projeto.Atualizar(" D.A.N.T.E. ", null);

        Assert.Equal((id, Espaco, "D.A.N.T.E.", (string?)null),
            (projeto.Id, projeto.IdEspacoDeConhecimento, projeto.Nome, projeto.Descricao));
    }

    // Repositório é associação opcional: trocar ou remover não muda a identidade do projeto.
    [Fact]
    public void RepositoryIsAnOptionalAssociationNotTheIdentity()
    {
        var projeto = new Projeto(Espaco, "Dante");
        var id = projeto.Id;

        projeto.AssociarRepositorio(" @dante ");
        Assert.Equal("@dante", projeto.AliasDoRepositorio);
        projeto.AssociarRepositorio("@fitness");
        Assert.Equal("@fitness", projeto.AliasDoRepositorio);
        projeto.DesassociarRepositorio();

        Assert.Null(projeto.AliasDoRepositorio);
        Assert.Equal(id, projeto.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("@com espaco")]
    [InlineData("@linha\nquebrada")]
    public void InvalidRepositoryAliasIsRefused(string alias)
    {
        var projeto = new Projeto(Espaco, "Dante");

        Assert.Throws<ArgumentException>(() => projeto.AssociarRepositorio(alias));
        Assert.Null(projeto.AliasDoRepositorio);
    }

    [Fact]
    public void ArchivedProjectIsReadOnlyUntilReactivated()
    {
        var projeto = new Projeto(Espaco, "Dante");
        projeto.AssociarRepositorio("@dante");

        projeto.Arquivar();

        Assert.True(projeto.Arquivado);
        Assert.Throws<InvalidOperationException>(() => projeto.Atualizar("Outro", null));
        Assert.Throws<InvalidOperationException>(() => projeto.AssociarRepositorio("@outro"));
        Assert.Throws<InvalidOperationException>(projeto.DesassociarRepositorio);
        Assert.Throws<InvalidOperationException>(projeto.Arquivar);
        Assert.Equal(("Dante", "@dante"), (projeto.Nome, projeto.AliasDoRepositorio));

        projeto.Reativar();

        Assert.False(projeto.Arquivado);
        Assert.Throws<InvalidOperationException>(projeto.Reativar);
    }

    // Projeto não carrega caminho, working directory, GitHub nem tipos de canal ou persistência.
    [Fact]
    public void ProjectDependsOnlyOnPrimitivesAndItsOwnState()
    {
        var tipo = typeof(Projeto);
        var dependencias = tipo.GetProperties().Select(propriedade => propriedade.PropertyType)
            .Concat(tipo.GetConstructors().SelectMany(construtor => construtor.GetParameters())
                .Select(parametro => parametro.ParameterType));

        Assert.All(dependencias, dependencia => Assert.True(
            dependencia.IsPrimitive || dependencia == typeof(string) || dependencia == typeof(Guid) ||
            dependencia == typeof(EstadoDoProjeto), dependencia.FullName));
    }
}
