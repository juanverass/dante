namespace Dante.Application.IntegracaoDeCi;

// Classificação de uma falha com as evidências que a sustentam. Só nasce pela MatrizDeFalhasDeCi, que recusa
// categoria sem a evidência exigida.
public sealed record FalhaDeCiDto
{
    internal FalhaDeCiDto(CategoriaDaFalhaDeCi categoria, IdentidadeDaExecucaoDeCi execucao,
        IReadOnlyList<EvidenciaDeCiDto> evidencias)
    {
        Categoria = categoria;
        Execucao = execucao;
        Evidencias = evidencias;
    }

    public CategoriaDaFalhaDeCi Categoria { get; }
    public IdentidadeDaExecucaoDeCi Execucao { get; }
    public IReadOnlyList<EvidenciaDeCiDto> Evidencias { get; }
}
