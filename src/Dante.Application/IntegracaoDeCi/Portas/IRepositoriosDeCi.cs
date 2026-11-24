namespace Dante.Application.IntegracaoDeCi;

// Identidade de CI de um alias cadastrado (AD-76, #238): o dono/repositorio do cadastro e a única conexão do host que
// libera esse alias. Sem rede e sem ler credencial. Alias sem nome no provedor, sem conexão que o libere ou liberado por
// mais de uma conexão responde FalhaNaConsultaDeCiException(NaoConfigurada): nunca se escolhe uma conexão nem se herda a
// de outro repositório (um repositório vinculado não usa a conexão do principal).
public interface IRepositoriosDeCi
{
    RepositorioDeCi Resolver(string alias);
}
