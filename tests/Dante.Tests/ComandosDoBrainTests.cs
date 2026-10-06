using Dante.Infrastructure.Banco;
using Dante.Infrastructure.Brain;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

public sealed class ComandosDoBrainTests
{
    [Theory]
    [InlineData("migrate")]
    [InlineData("health")]
    [InlineData("backup")]
    [InlineData("restore")]
    public async Task BancoNaoConfiguradoRetornaFalhaSemIniciarHost(string comando)
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(1, await ComandosDoBrain.ExecutarAsync(provider, ["--brain", comando]));
    }

    [Fact]
    public async Task ComandoFuncionalNaoResolveAdministracaoDoBanco()
    {
        var resolveuAdministracao = false;
        using var provider = new ServiceCollection()
            .AddScoped<AdministracaoDoBanco>(_ =>
            {
                resolveuAdministracao = true;
                throw new InvalidOperationException("Não deve ser resolvido para operações funcionais.");
            }).BuildServiceProvider();
        Assert.Equal(1, await ComandosDoBrain.ExecutarAsync(provider, ["--brain", "inspect", "invalido", "invalido", "-"]));
        Assert.False(resolveuAdministracao);
    }

    [Fact]
    public async Task ComandoDesconhecidoRetornaFalha()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(1, await ComandosDoBrain.ExecutarAsync(provider, ["--brain", "invalido"]));
    }
}
