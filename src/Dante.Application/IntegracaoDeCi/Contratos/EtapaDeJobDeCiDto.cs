namespace Dante.Application.IntegracaoDeCi;

// Etapa (step) de um job, para apontar onde a falha ocorreu. O nome vem do workflow do repositório: é dado não
// confiável, exibido como texto e nunca interpretado como instrução.
public sealed record EtapaDeJobDeCiDto(int Numero, string Nome, EstadoDaExecucao Estado);
