using Dante.Domain.EspacosDeConhecimento;

namespace Dante.Tests;

public sealed class EspacoDeConhecimentoTests
{
    private static readonly Guid Proprietario = Guid.NewGuid();

    [Fact]
    public void NewSpaceIsActiveOwnedAndHasStableDomainIdentity()
    {
        var espaco = new EspacoDeConhecimento(Proprietario, "  Pessoal  ", "  Vida pessoal  ");

        Assert.NotEqual(Guid.Empty, espaco.Id);
        Assert.Equal(Proprietario, espaco.IdUsuario);
        Assert.Equal("Pessoal", espaco.Nome);
        Assert.Equal("Vida pessoal", espaco.Descricao);
        Assert.Equal(EstadoDoEspacoDeConhecimento.Ativo, espaco.Estado);
        Assert.False(espaco.Arquivado);
        Assert.NotEqual(espaco.Id, new EspacoDeConhecimento(Proprietario, "Pessoal").Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Linha\nquebrada")]
    public void NameIsRequiredAndPrintable(string? nome) =>
        Assert.Throws<ArgumentException>(() => new EspacoDeConhecimento(Proprietario, nome!));

    [Fact]
    public void NameAndDescriptionHaveMaximumLengthsAfterTrimming()
    {
        var nome = new string('n', EspacoDeConhecimento.TamanhoMaximoDoNome);
        var descricao = new string('d', EspacoDeConhecimento.TamanhoMaximoDaDescricao);

        var espaco = new EspacoDeConhecimento(Proprietario, $" {nome} ", $" {descricao} ");

        Assert.Equal(nome, espaco.Nome);
        Assert.Equal(descricao, espaco.Descricao);
        Assert.Throws<ArgumentException>(() => new EspacoDeConhecimento(Proprietario, nome + "n"));
        Assert.Throws<ArgumentException>(() => new EspacoDeConhecimento(Proprietario, "Trabalho", descricao + "d"));
    }

    [Fact]
    public void BlankDescriptionIsAbsent() =>
        Assert.Null(new EspacoDeConhecimento(Proprietario, "Estudos", "   ").Descricao);

    [Fact]
    public void OwnerIsRequired() =>
        Assert.Throws<ArgumentException>(() => new EspacoDeConhecimento(Guid.Empty, "Trabalho"));

    [Fact]
    public void UpdateChangesPresentationButNotIdentityOrOwner()
    {
        var espaco = new EspacoDeConhecimento(Proprietario, "Trabalho", "Antiga");
        var id = espaco.Id;

        espaco.Atualizar(" Empresa X ", null);

        Assert.Equal(id, espaco.Id);
        Assert.Equal(Proprietario, espaco.IdUsuario);
        Assert.Equal("Empresa X", espaco.Nome);
        Assert.Null(espaco.Descricao);
    }

    [Fact]
    public void InvalidUpdateLeavesTheSpaceUnchanged()
    {
        var espaco = new EspacoDeConhecimento(Proprietario, "Trabalho", "Antiga");

        Assert.Throws<ArgumentException>(() => espaco.Atualizar("Novo", new string('d', 1001)));
        Assert.Throws<ArgumentException>(() => espaco.Atualizar(" ", "Nova"));

        Assert.Equal("Trabalho", espaco.Nome);
        Assert.Equal("Antiga", espaco.Descricao);
    }

    [Fact]
    public void ArchivedSpaceIsReadOnlyUntilReactivated()
    {
        var espaco = new EspacoDeConhecimento(Proprietario, "Estudos");

        espaco.Arquivar();

        Assert.True(espaco.Arquivado);
        Assert.Equal(EstadoDoEspacoDeConhecimento.Arquivado, espaco.Estado);
        Assert.Throws<InvalidOperationException>(() => espaco.Atualizar("Outro", null));
        Assert.Equal("Estudos", espaco.Nome);

        espaco.Reativar();
        espaco.Atualizar("Outro", null);

        Assert.False(espaco.Arquivado);
        Assert.Equal("Outro", espaco.Nome);
    }

    [Fact]
    public void ArchiveAndReactivateRequireTheOppositeState()
    {
        var espaco = new EspacoDeConhecimento(Proprietario, "Pessoal");

        Assert.Throws<InvalidOperationException>(espaco.Reativar);
        espaco.Arquivar();
        Assert.Throws<InvalidOperationException>(espaco.Arquivar);
    }

    // Espaço é o limite do Brain: conhecimento pode pertencer a ele sem Projeto, e ele não conhece canal nem provider.
    [Fact]
    public void SpaceDoesNotDependOnProjectsChannelsOrPersistence()
    {
        var tipo = typeof(EspacoDeConhecimento);
        var dependencias = tipo.GetProperties().Select(propriedade => propriedade.PropertyType)
            .Concat(tipo.GetConstructors().SelectMany(construtor => construtor.GetParameters())
                .Select(parametro => parametro.ParameterType));

        Assert.All(dependencias, dependencia => Assert.True(
            dependencia.IsPrimitive || dependencia == typeof(string) || dependencia == typeof(Guid) ||
            dependencia == typeof(EstadoDoEspacoDeConhecimento), dependencia.FullName));
    }
}
