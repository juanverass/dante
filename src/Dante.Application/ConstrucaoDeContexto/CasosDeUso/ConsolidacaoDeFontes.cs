using Dante.Application.BuscaDoBrain;
using Dante.Application.DocumentosFonte;
using Dante.Application.SegurancaDoBrain;
using static Dante.Application.ConstrucaoDeContexto.SelecaoDeContexto;

namespace Dante.Application.ConstrucaoDeContexto;

// Prova de consolidação de uma fonte bruta inteira (#147). Sem prova, a fonte segue integral.
// Prova = todos os trechos indexados da revisão citada cobrem o documento, e cada trecho tem um conhecimento
// candidato ao pacote cuja proveniência registrada é exatamente a referência do trecho (id, revisão, parte e hash)
// e cujo conteúdo contém o texto do trecho. Co-ocorrência no snapshot ou semelhança de assunto não é prova.
internal static class ConsolidacaoDeFontes
{
    internal const int MaximoDeTrechos = 32;
    internal static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ProvarAsync(AcessoAoBrain acesso,
        IReadOnlyList<ItemDeContextoDto> candidatos, IIndiceDeFontes fontes, CancellationToken ct)
    {
        var provas = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var derivados = candidatos.Where(x => x.Origem == "conhecimento" && x.ReferenciaDaFonte is not null)
            .OrderBy(x => x.Chave, StringComparer.Ordinal).ToArray();
        if (derivados.Length == 0) return provas;
        foreach (var fonte in candidatos.Where(x => x.Origem == "fonte_bruta" && x.InicioDaFonte is null))
        {
            var cobertura = new List<string>(); var fim = 0; var completa = false;
            for (var numero = 0; numero < MaximoDeTrechos; numero++)
            {
                var trecho = await fontes.ObterTrechoAsync(acesso, fonte.Id, fonte.Revisao, numero, ct);
                if (trecho is null) { completa = numero > 0 && fim >= fonte.Conteudo.EnumerateRunes().Count(); break; }
                var texto = Normalizar(ProtecaoDeSegredos.Redigir(trecho.Conteudo)!);
                var derivado = derivados.FirstOrDefault(k => k.ReferenciaDaFonte == trecho.Referencia &&
                    Normalizar(k.Conteudo).Contains(texto, StringComparison.Ordinal));
                if (derivado is null || texto.Length == 0) break;
                cobertura.Add(derivado.Chave); fim = Math.Max(fim, trecho.Inicio + trecho.Conteudo.EnumerateRunes().Count());
            }
            if (completa) provas[fonte.Chave] = cobertura;
        }
        return provas;
    }
}
