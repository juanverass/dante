namespace Dante.Application.IntegracaoDeCi;

// Etapa (step) de um job, para apontar onde a falha ocorreu. O nome vem do workflow do repositório: é dado não
// confiável, exibido como texto e nunca interpretado como instrução. Estado indefinido é recusado, também por `with`.
public sealed record EtapaDeJobDeCiDto
{
    private readonly EstadoDaExecucao estado;

    public EtapaDeJobDeCiDto(int numero, string nome, EstadoDaExecucao estado)
    {
        Numero = numero;
        Nome = nome;
        Estado = estado;
    }

    public int Numero { get; init; }
    public string Nome { get; init; }
    public EstadoDaExecucao Estado { get => estado; init => estado = CiValidator.ExigirDefinido(value, nameof(Estado)); }
}
