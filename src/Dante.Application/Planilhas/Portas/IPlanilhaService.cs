namespace Dante.Application.Planilhas;

// Porta do provedor de planilhas (#224): conhece planilhas, abas, intervalos, células, fórmulas, mesclagens e
// metadados, nunca o significado dos dados. Cada provedor é um adapter da Infrastructure que implementa a mesma
// porta. Falhas chegam como FalhaDePlanilhaException com motivo e mensagem seguros.
public interface IPlanilhaService
{
    // ID da planilha a partir do que o usuário colou (URL de compartilhamento ou ID puro); null quando não reconhece.
    string? IdentificarPlanilha(string urlOuId);

    Task<PlanilhaDto> ObterMetadadosAsync(string idDaPlanilha, bool calcularAreaUsada,
        CancellationToken cancellationToken = default);

    // Valores exibidos, brutos e fórmulas de um retângulo (ou da aba inteira), com as mesclagens que o tocam.
    // Lê no máximo limiteDeCelulas células da grade e marca o resultado como truncado quando há mais.
    Task<IntervaloDaPlanilhaDto> LerAsync(string idDaPlanilha, IntervaloA1 intervalo, int limiteDeCelulas,
        CancellationToken cancellationToken = default);

    // Só os valores exibidos, em lote: base barata para busca e área usada.
    Task<IReadOnlyList<ValoresLidos>> LerValoresExibidosAsync(string idDaPlanilha, IReadOnlyList<IntervaloA1> intervalos,
        CancellationToken cancellationToken = default);

    // Grava apenas valores (formatação e células fora dos retângulos ficam intactas). Texto é interpretado como
    // digitado pelo usuário na localidade da planilha, quando o provider conhece essa localidade.
    // Providers de arquivo podem exigir número/booleano tipados e informar essa limitação na leitura.
    Task<IReadOnlyList<string>> AtualizarAsync(string idDaPlanilha, IReadOnlyList<ValoresParaEscrita> escritas,
        CancellationToken cancellationToken = default);

    // Acrescenta uma linha após a tabela detectada no intervalo e devolve o retângulo efetivamente escrito.
    Task<IntervaloA1> AdicionarLinhaAsync(string idDaPlanilha, IntervaloA1 tabela, IReadOnlyList<ValorDeCelula> valores,
        CancellationToken cancellationToken = default);
}
