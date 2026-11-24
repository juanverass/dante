using Dante.Application.IntegracaoDeCi;
using Dante.Infrastructure.Contextos;

namespace Dante.Infrastructure.IntegracaoDeCi;

// Identidade de CI de um alias (#238, AD-77): o dono/repositorio do cadastro (AD-07) e a única conexão do host que
// libera o alias (ConexoesDeCiAutorizadas). Sem rede e sem ler credencial: a autorização completa continua sendo
// refeita pelo provedor a cada chamada. Vínculos (AD-58) não contam: um repositório vinculado só é consultado se uma
// conexão o liberar pelo próprio alias.
internal sealed class RepositoriosDeCiConfigurados(ConexoesDeCiAutorizadas conexoes, RepositoryRegistry cadastro)
    : IRepositoriosDeCi
{
    public RepositorioDeCi Resolver(string alias)
    {
        var cadastrado = cadastro.Get(alias)
            ?? throw Falha(MotivoDaFalhaNaConsultaDeCi.NaoAutorizada, "O repositório não está cadastrado no D.A.N.T.E.");
        if (cadastrado.GitHub is not { Length: > 0 } nomeCompleto)
            throw Falha(MotivoDaFalhaNaConsultaDeCi.NaoConfigurada,
                "O cadastro do repositório não informa dono/repositorio no provedor de CI.");
        return conexoes.QueLiberam(cadastrado.Alias) switch
        {
            [var unica] => new RepositorioDeCi(cadastrado.Alias, nomeCompleto, null, unica),
            [] => throw Falha(MotivoDaFalhaNaConsultaDeCi.NaoConfigurada,
                "Nenhuma conexão de CI do host libera este repositório."),
            _ => throw Falha(MotivoDaFalhaNaConsultaDeCi.NaoConfigurada,
                "Mais de uma conexão de CI libera este repositório; deixe só uma.")
        };
    }

    private static FalhaNaConsultaDeCiException Falha(MotivoDaFalhaNaConsultaDeCi motivo, string mensagem) => new(motivo, mensagem);
}
