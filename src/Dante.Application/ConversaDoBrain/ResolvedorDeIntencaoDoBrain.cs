using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
namespace Dante.Application.ConversaDoBrain;

// Gramática determinística: ambiguidade pede esclarecimento, nunca autorização por inferência/LLM.
public static class ResolvedorDeIntencaoDoBrain
{
    public static string Normalizar(string valor)=>string.Concat(valor.Normalize(NormalizationForm.FormD).Where(c=>CharUnicodeInfo.GetUnicodeCategory(c)!=UnicodeCategory.NonSpacingMark)).ToLowerInvariant().Normalize(NormalizationForm.FormC);
    public static IntencaoResolvidaDto Resolver(string mensagem)
    {
        var original=mensagem.Normalize(NormalizationForm.FormC).Trim();if(original.StartsWith("/brain ",StringComparison.OrdinalIgnoreCase))original=original[7..].Trim();
        var texto=Normalizar(original);Match M(string p)=>Regex.Match(texto,p,RegexOptions.CultureInvariant|RegexOptions.Singleline|RegexOptions.NonBacktracking,TimeSpan.FromMilliseconds(100));
        string Corpo(Match m)=>m.Groups["texto"].Success?original.Substring(m.Groups["texto"].Index,m.Groups["texto"].Length).Trim():"";
        int? Numero(string campo)
        {
            if(int.TryParse(campo,out var n))return n;
            return campo switch{"primeiro" or "primeira"=>1,"segundo" or "segunda"=>2,"terceiro" or "terceira"=>3,"quarto" or "quarta"=>4,"quinto" or "quinta"=>5,_=>null};
        }
        var confirmar=M(@"^(?:confirmo|confirme|confirmar|sim,? confirmo)(?: (?:o |a |item )?(?<n>\d+|primeir[oa]|segund[oa]|terceir[oa]|quart[oa]|quint[oa]))?[.!]?$" );
        if(confirmar.Success)return new(IntencaoDoBrain.Confirmar,Numero:Numero(confirmar.Groups["n"].Value));
        if(M(@"^(?:cancelar|cancele|nao,? cancele|desista)[.!]?$").Success)return new(IntencaoDoBrain.Cancelar);
        if(M(@"^(?:liste|listar|mostre|mostrar) (?:os )?candidatos(?: pendentes)?[.!]?$").Success)return new(IntencaoDoBrain.ListarCandidatos);
        var consulta=M(@"^(?:(?:o que (?:voce )?(?:sabe|sabemos)(?: sobre| de| a respeito de)?)|(?:busque|buscar|procure)(?: no brain)?)\s+(?<texto>.+)$");
        if(consulta.Success)return new(IntencaoDoBrain.Consultar,Corpo(consulta).TrimEnd('?'));
        var experiencia=M(@"^ja (?:resolvemos|vimos|lidamos com) (?:algo )?(?:parecido|semelhante)(?: (?:com|a|sobre))?[: ]*(?<texto>.*)$");
        if(experiencia.Success)return new(IntencaoDoBrain.Experiencia,Corpo(experiencia).TrimEnd('?'));
        var fonte=M(@"^importe (?:uma )?(?<formato>nota|markdown) (?<nome>[^:]+):\s*(?<texto>.+)$");
        if(fonte.Success)return new(IntencaoDoBrain.ImportarFonte,Corpo(fonte),Nome:original.Substring(fonte.Groups["nome"].Index,fonte.Groups["nome"].Length).Trim(),Formato:fonte.Groups["formato"].Value=="markdown"?"markdown":"texto");
        var consolidar=M(@"^(?:consolide|documente) (?:a )?(?<n>\d+|primeira|segunda|terceira|quarta|quinta) fonte[.!]?$");
        if(consolidar.Success)return new(IntencaoDoBrain.CapturarFonte,Numero:Numero(consolidar.Groups["n"].Value));
        var captura=M(@"^(?:documente(?: como resolvemos(?: isso)?)?|registre(?: no brain)?|guarde(?: no brain)?)(?: isto| isso)?[: ]*(?<texto>.*)$");
        if(captura.Success)return new(IntencaoDoBrain.Capturar,Corpo(captura));
        var corrigir=M(@"^(?:corrija|corrigir)(?: (?:o |a |item )?(?<n>\d+|primeir[oa]|segund[oa]|terceir[oa]|quart[oa]|quint[oa]))?(?: para|:)\s*(?<texto>.+)$");
        if(corrigir.Success)return new(IntencaoDoBrain.Corrigir,Corpo(corrigir),Numero(corrigir.Groups["n"].Value));
        var errada=M(@"^(?:essa|esta|a) informacao esta errada[.!]?(?::\s*(?<texto>.*))?$");
        if(errada.Success)return new(IntencaoDoBrain.Corrigir,Corpo(errada));
        var invalida=M(@"^(?:esqueca|invalide|remova|apague)(?: (?:isso|esta informacao|essa informacao|o item|a informacao))?(?: (?:o |a )?(?<n>\d+|primeir[oa]|segund[oa]|terceir[oa]|quart[oa]|quint[oa]))?[.!]?$");
        if(invalida.Success)return new(IntencaoDoBrain.Invalidar,Numero:Numero(invalida.Groups["n"].Value));
        var relacao=M(@"^(?:essa solucao resolveu aquele incidente|relacione (?:a )?solucao (?<b>\d+) (?:ao|com o) incidente (?<a>\d+))[.!]?$");
        if(relacao.Success)return new(IntencaoDoBrain.Relacionar,Numero:Numero(relacao.Groups["a"].Value),SegundoNumero:Numero(relacao.Groups["b"].Value));
        var origem=M(@"^(?:de onde veio(?: essa informacao| isso)?|(?:explique|mostre) (?:a )?origem)(?: (?:do item |da |do |de )?(?<n>\d+|primeir[oa]|segund[oa]|terceir[oa]|quart[oa]|quint[oa]))?[?!.]?$");
        if(origem.Success)return new(IntencaoDoBrain.Origem,Numero:Numero(origem.Groups["n"].Value));
        var escolha=M(@"^(?:o |a |item |escolho (?:o |a )?)?(?<n>\d+|primeir[oa]|segund[oa]|terceir[oa]|quart[oa]|quint[oa])[.!]?$");
        if(escolha.Success)return new(IntencaoDoBrain.Escolher,Numero:Numero(escolha.Groups["n"].Value));
        if(M(@"^(?:inspecione|mostre|audite) (?:o )?brain[.!]?$").Success)return new(IntencaoDoBrain.Inspecionar);
        if(M(@"^(?:exporte|exportar) (?:o )?brain[.!]?$").Success)return new(IntencaoDoBrain.Exportar);
        return new(IntencaoDoBrain.Nenhuma);
    }
}
