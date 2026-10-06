using System.Text.RegularExpressions;

namespace Dante.Application.Planilhas;

// Validação de entrada das operações de planilha (AD-52): formato de alias/ID/intervalo, limites e forma dos lotes.
internal static class PlanilhaValidator
{
    internal static bool PareceAlias(string? alias) =>
        Regex.IsMatch(alias?.Trim().TrimStart('@').ToLowerInvariant() ?? string.Empty, "^[a-z][a-z0-9_-]{0,39}$");

    internal static string ExigirAlias(string? alias)
    {
        var normalizado = alias?.Trim().TrimStart('@').ToLowerInvariant();
        if (string.IsNullOrEmpty(normalizado) || !PareceAlias(normalizado))
            throw new ArgumentException("Alias inválido. Use de 1 a 40 letras minúsculas, números, _ ou -, começando com letra.",
                nameof(alias));
        return normalizado;
    }

    internal static IntervaloA1 ExigirIntervaloComAba(string? intervalo, string parametro)
    {
        if (!IntervaloA1.TentarInterpretar(intervalo, out var interpretado))
            throw new ArgumentException($"Intervalo A1 inválido: {intervalo}. Use Aba!B12, Aba!B12:D20 ou 'Aba com espaço'!A1.",
                parametro);
        if (interpretado!.Aba is null)
            throw new ArgumentException($"Informe a aba no intervalo {intervalo} (por exemplo Aba!{intervalo}).", parametro);
        return interpretado;
    }

    internal static int ExigirLimite(int? limite, int padrao, int maximo, string parametro)
    {
        var valor = limite ?? padrao;
        if (valor < 1 || valor > maximo)
            throw new ArgumentOutOfRangeException(parametro, valor, $"O limite deve estar entre 1 e {maximo}.");
        return valor;
    }

    internal static string ExigirTermo(string? termo, string parametro)
    {
        var texto = termo?.Trim() ?? string.Empty;
        if (texto.Length is 0 or > 200)
            throw new ArgumentException("O texto procurado deve ter de 1 a 200 caracteres.", parametro);
        return texto;
    }

    internal static IReadOnlyList<IntervaloA1> ExigirEscrita(EscritaNaPlanilhaDto escrita)
    {
        ArgumentNullException.ThrowIfNull(escrita);
        if (escrita.Alteracoes is not { Count: > 0 })
            throw new ArgumentException("Informe ao menos uma alteração.", nameof(escrita));
        var intervalos = new List<IntervaloA1>();
        long celulas = 0;
        foreach (var alteracao in escrita.Alteracoes)
        {
            var intervalo = ExigirIntervaloComAba(alteracao.Intervalo, nameof(escrita));
            if (intervalo.AbaInteira)
                throw new ArgumentException("A escrita exige um retângulo exato, não a aba inteira.", nameof(escrita));
            ExigirForma(alteracao.Valores, intervalo, "valores");
            if (alteracao.ValoresEsperados is { } esperados) ExigirForma(esperados, intervalo, "valores esperados");
            foreach (var valor in alteracao.Valores.SelectMany(linha => linha))
                if ((valor?.Texto?.Length ?? 0) > PlanilhasAppService.MaximoDeCaracteresPorCelula)
                    throw new ArgumentException("Valor maior que o limite de uma célula.", nameof(escrita));
            if (intervalos.Any(anterior => anterior.Aba == intervalo.Aba && anterior.Intersecta(intervalo)))
                throw new ArgumentException($"O intervalo {alteracao.Intervalo} se sobrepõe a outra alteração do lote.", nameof(escrita));
            celulas += intervalo.QuantidadeDeCelulas;
            intervalos.Add(intervalo);
        }
        if (celulas > PlanilhasAppService.MaximoDeCelulasPorEscrita)
            throw new ArgumentException($"No máximo {PlanilhasAppService.MaximoDeCelulasPorEscrita} células por escrita.", nameof(escrita));
        return intervalos;
    }

    internal static void ExigirReferencia(EscritaPorReferenciaDto escrita)
    {
        ArgumentNullException.ThrowIfNull(escrita);
        ExigirTermo(escrita.Referencia, nameof(escrita));
        if (escrita.Coluna is not null)
        {
            if (!Regex.IsMatch(escrita.Coluna.Trim(), "^[A-Za-z]{1,3}$"))
                throw new ArgumentException($"Coluna inválida: {escrita.Coluna}. Use a letra da coluna, como D.", nameof(escrita));
            if (escrita.DeslocamentoDeColunas != 0)
                throw new ArgumentException("Informe a coluna ou o deslocamento de colunas, não os dois.", nameof(escrita));
        }
        if (Math.Abs(escrita.DeslocamentoDeColunas) > PlanilhasAppService.MaximoDeDeslocamento || Math.Abs(escrita.DeslocamentoDeLinhas) > PlanilhasAppService.MaximoDeDeslocamento)
            throw new ArgumentOutOfRangeException(nameof(escrita), $"Deslocamentos limitados a {PlanilhasAppService.MaximoDeDeslocamento}.");
        if (escrita.Coluna is null && escrita.DeslocamentoDeColunas == 0 && escrita.DeslocamentoDeLinhas == 0)
            throw new ArgumentException("O alvo não pode ser a própria célula de referência; informe a coluna ou um deslocamento.",
                nameof(escrita));
        if (escrita.Intervalo is not null) ExigirIntervaloComAba(escrita.Intervalo, nameof(escrita));
        if ((escrita.Valor?.Texto?.Length ?? 0) > PlanilhasAppService.MaximoDeCaracteresPorCelula)
            throw new ArgumentException("Valor maior que o limite de uma célula.", nameof(escrita));
    }

    internal static IntervaloA1 ExigirLinha(AdicaoDeLinhaDto adicao)
    {
        ArgumentNullException.ThrowIfNull(adicao);
        var intervalo = ExigirIntervaloComAba(adicao.Intervalo, nameof(adicao));
        if (adicao.Valores is not { Count: > 0 } || adicao.Valores.Count > PlanilhasAppService.MaximoDeCelulasPorEscrita)
            throw new ArgumentException($"Informe de 1 a {PlanilhasAppService.MaximoDeCelulasPorEscrita} valores para a linha.", nameof(adicao));
        if (adicao.Valores.Any(valor => valor is null || (valor.Texto?.Length ?? 0) > PlanilhasAppService.MaximoDeCaracteresPorCelula))
            throw new ArgumentException("Valor ausente ou maior que o limite de uma célula.", nameof(adicao));
        return intervalo;
    }

    internal static (string Nome, IntervaloA1 Intervalo, string? Descricao) ExigirRegiao(string? nome, string? intervalo,
        string? descricao)
    {
        var normalizado = nome?.Trim() ?? string.Empty;
        if (normalizado.Length is 0 or > 60)
            throw new ArgumentException("O nome da região deve ter de 1 a 60 caracteres.", nameof(nome));
        var texto = descricao?.Trim();
        if (texto?.Length > 500)
            throw new ArgumentException("A descrição da região deve ter até 500 caracteres.", nameof(descricao));
        return (normalizado, ExigirIntervaloComAba(intervalo, nameof(intervalo)), string.IsNullOrEmpty(texto) ? null : texto);
    }

    internal static string? ExigirDescricao(string? descricao)
    {
        var texto = descricao?.Trim();
        if (texto?.Length > 500)
            throw new ArgumentException("A descrição deve ter até 500 caracteres.", nameof(descricao));
        return string.IsNullOrEmpty(texto) ? null : texto;
    }

    private static void ExigirForma<T>(IReadOnlyList<IReadOnlyList<T>>? linhas, IntervaloA1 intervalo, string nome)
    {
        if (linhas is null || linhas.Count != intervalo.QuantidadeDeLinhas ||
            linhas.Any(linha => linha is null || linha.Count != intervalo.QuantidadeDeColunas))
            throw new ArgumentException(
                $"Os {nome} devem ter {intervalo.QuantidadeDeLinhas} linha(s) de {intervalo.QuantidadeDeColunas} coluna(s), " +
                $"como o intervalo {intervalo}.", "escrita");
    }}
