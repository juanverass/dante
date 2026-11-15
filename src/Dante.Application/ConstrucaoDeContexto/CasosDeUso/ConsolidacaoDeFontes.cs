using System.Text;
using Dante.Application.BuscaDoBrain;
using Dante.Application.DocumentosFonte;
using Dante.Application.SegurancaDoBrain;
using static Dante.Application.ConstrucaoDeContexto.SelecaoDeContexto;

namespace Dante.Application.ConstrucaoDeContexto;

// Prova de consolidação de uma fonte bruta inteira (#147). Sem prova, a fonte segue integral.
// 1. Remontagem: os trechos indexados da revisão citada cobrem o documento de 0 ao fim, sem lacuna; cada trecho começa em
//    ou antes do fim já coberto, a sobreposição coincide, e o documento remontado é a própria fonte. Maior fim alcançado não
//    basta. O índice grava Inicio em caracteres do PostgreSQL (code points = Rune).
// 2. Cobertura: cada conhecimento candidato ao pacote conta só pela proveniência registrada (ReferenciaDeTrecho): mesmo
//    documento, mesma revisão, parte existente com a mesma referência base (id, revisão, parte, hash do documento). Sem
//    intervalo, cita a parte inteira; com intervalo, [inicio, fim) fica dentro da parte, começa e termina em fronteira de
//    linha e o SHA-256 do texto citado confere. Em ambos os casos o conteúdo do conhecimento contém o texto citado.
// 3. A união dos intervalos aceitos cobre todo ponto de código do documento; só sobra fora dela espaço em branco, que a
//    comparação normalizada já não distinguia. Co-ocorrência no snapshot ou semelhança de assunto não é prova.
internal static class ConsolidacaoDeFontes
{
    internal const int MaximoDeTrechos = 32;
    internal static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ProvarAsync(AcessoAoBrain acesso,
        IReadOnlyList<ItemDeContextoDto> candidatos, IIndiceDeFontes fontes, CancellationToken ct)
    {
        var provas = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var derivados = candidatos.Where(x => x.Origem == "conhecimento" && x.ReferenciaDaFonte is not null)
            .Select(x => (Item: x, Referencia: ReferenciaDeTrecho.Ler(x.ReferenciaDaFonte)))
            .Where(x => x.Referencia is not null).OrderBy(x => x.Item.Chave, StringComparer.Ordinal).ToArray();
        if (derivados.Length == 0) return provas;
        foreach (var fonte in candidatos.Where(x => x.Origem == "fonte_bruta" && x.InicioDaFonte is null))
        {
            if (!derivados.Any(d => d.Referencia!.IdDocumento == fonte.Id && d.Referencia.Revisao == fonte.Revisao)) continue;
            var trechos = new List<TrechoDaFonteDto>(); var documento = new List<Rune>(); var completa = false;
            for (var numero = 0; numero < MaximoDeTrechos; numero++)
            {
                var trecho = await fontes.ObterTrechoAsync(acesso, fonte.Id, fonte.Revisao, numero, ct);
                if (trecho is null) { completa = numero > 0 && Remontado(documento) == fonte.Conteudo; break; }
                if (trecho.Numero != numero || !Continuo(documento, trecho)) break;
                trechos.Add(trecho);
            }
            if (!completa) continue;
            var coberto = new bool[documento.Count]; var cobertura = new List<string>();
            foreach (var (derivado, r) in derivados)
            {
                if (r!.IdDocumento != fonte.Id || r.Revisao != fonte.Revisao || r.Numero >= trechos.Count) continue;
                var parte = trechos[r.Numero];
                if (r.Base != parte.Referencia) continue;
                var inicioDaParte = parte.Inicio; var fimDaParte = parte.Inicio + parte.Conteudo.EnumerateRunes().Count();
                var (a, b) = r.Inicio is { } ini ? (ini, r.Fim!.Value) : (inicioDaParte, fimDaParte);
                if (a < inicioDaParte || b > fimDaParte || a >= b) continue;
                var citado = Texto(documento, a, b);
                if (r.Inicio is not null && (r.Sha256 != ReferenciaDeTrecho.HashDoTexto(citado) || !EmFronteiraDeLinha(documento, a, b))) continue;
                var texto = Normalizar(ProtecaoDeSegredos.Redigir(citado)!);
                if (texto.Length == 0 || !Normalizar(derivado.Conteudo).Contains(texto, StringComparison.Ordinal)) continue;
                for (var i = a; i < b; i++) coberto[i] = true;
                cobertura.Add(derivado.Chave);
            }
            var integral = cobertura.Count > 0 && Enumerable.Range(0, documento.Count).All(i => coberto[i] || Rune.IsWhiteSpace(documento[i]));
            if (integral) provas[fonte.Chave] = cobertura.Distinct(StringComparer.Ordinal).ToArray();
        }
        return provas;
    }

    // Acrescenta o trecho ao documento remontado se ele começa dentro do já coberto (o primeiro, em 0) e a parte
    // sobreposta é idêntica. Lacuna, início deslocado ou sobreposição divergente invalidam a prova.
    private static bool Continuo(List<Rune> documento, TrechoDaFonteDto trecho)
    {
        if (trecho.Inicio < 0 || trecho.Inicio > documento.Count) return false;
        var runas = trecho.Conteudo.EnumerateRunes().ToArray();
        var sobreposta = Math.Min(documento.Count - trecho.Inicio, runas.Length);
        for (var i = 0; i < sobreposta; i++) if (documento[trecho.Inicio + i] != runas[i]) return false;
        documento.AddRange(runas.Skip(sobreposta));
        return true;
    }

    // Intervalo citado começa no início do documento ou logo após '\n' e termina no fim do documento ou logo após '\n':
    // uma linha nunca é citada pela metade, então "Não habilitar X" não pode ser coberto por "Não " e "habilitar X".
    internal static bool EmFronteiraDeLinha(IReadOnlyList<Rune> documento, int inicio, int fim) =>
        (inicio == 0 || documento[inicio - 1].Value == '\n') && (fim == documento.Count || documento[fim - 1].Value == '\n');

    private static string Texto(List<Rune> documento, int inicio, int fim)
    {
        var texto = new StringBuilder();
        for (var i = inicio; i < fim; i++) texto.Append(documento[i].ToString());
        return texto.ToString();
    }

    // A fonte candidata chega redigida; o documento remontado do índice passa pela mesma redação antes da comparação.
    private static string Remontado(List<Rune> documento) => ProtecaoDeSegredos.Redigir(Texto(documento, 0, documento.Count))!;
}
