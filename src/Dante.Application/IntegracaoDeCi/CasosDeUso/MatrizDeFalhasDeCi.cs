namespace Dante.Application.IntegracaoDeCi;

// Matriz de falhas (AD-76): cada categoria só é aceita com ao menos uma evidência de um dos tipos listados. Limite de
// minutos ou cobrança exige mensagem explícita do provedor: workflow vermelho, job sem log ou fila longa não bastam.
// Indeterminada não exige nada e é a resposta honesta quando falta evidência.
public static class MatrizDeFalhasDeCi
{
    public static IReadOnlyDictionary<CategoriaDaFalhaDeCi, IReadOnlyList<TipoDeEvidenciaDeCi>> EvidenciasAceitas { get; } =
        new Dictionary<CategoriaDaFalhaDeCi, IReadOnlyList<TipoDeEvidenciaDeCi>>
        {
            [CategoriaDaFalhaDeCi.ErroNoCodigoOuTeste] = [TipoDeEvidenciaDeCi.EtapaComFalha, TipoDeEvidenciaDeCi.TrechoDeLog],
            [CategoriaDaFalhaDeCi.RunnerIndisponivel] = [TipoDeEvidenciaDeCi.TempoSemRunner, TipoDeEvidenciaDeCi.AnotacaoDoProvedor],
            [CategoriaDaFalhaDeCi.LimiteDeMinutosOuCobranca] = [TipoDeEvidenciaDeCi.AnotacaoDoProvedor],
            [CategoriaDaFalhaDeCi.LogsInacessiveis] = [TipoDeEvidenciaDeCi.RespostaDoProvedor],
            [CategoriaDaFalhaDeCi.Indeterminada] = []
        };

    public static FalhaDeCiDto Classificar(CategoriaDaFalhaDeCi categoria, ExecucaoDeCiDto execucao,
        IReadOnlyList<EvidenciaDeCiDto> evidencias)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        ArgumentNullException.ThrowIfNull(evidencias);
        if (!EvidenciasAceitas.TryGetValue(categoria, out var aceitas))
            throw new ArgumentOutOfRangeException(nameof(categoria), "Categoria de falha desconhecida.");
        if (execucao.Estado is not (EstadoDaExecucao.Falha or EstadoDaExecucao.Cancelada or EstadoDaExecucao.Expirada)
            && !(categoria == CategoriaDaFalhaDeCi.RunnerIndisponivel && execucao.Estado == EstadoDaExecucao.AguardandoRunner))
            throw new ArgumentException("Só se classifica execução terminada sem sucesso ou presa sem runner.", nameof(execucao));
        if (aceitas.Count > 0 && !evidencias.Any(e => aceitas.Contains(e.Tipo)))
            throw new ArgumentException($"{categoria} exige evidência do tipo {string.Join(" ou ", aceitas)}.", nameof(evidencias));

        var jobs = execucao.Jobs.Select(j => j.IdDoJob).ToHashSet();
        if (evidencias.Any(e => e.IdDoJob is { } id && !jobs.Contains(id)))
            throw new ArgumentException("Evidência aponta para job de outra tentativa ou execução.", nameof(evidencias));
        return new FalhaDeCiDto(categoria, execucao.Identidade, [.. evidencias]);
    }
}
