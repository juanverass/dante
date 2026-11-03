using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;

namespace Dante.Application.Conhecimentos;

// Apresentação conservadora: status legado é preservado, mas não comprova autoria (AD-59 I7/D3).
public static class RotulagemDoConhecimento
{
    public static string Rotular(NaturezaDoConteudo? origem, NaturezaDoConteudo? revisao, StatusDoConhecimento? status)
    {
        var avisos = new List<string>();
        if (origem == NaturezaDoConteudo.ConclusaoDoAgente) avisos.Add("Conclusão do agente, não confirmada");
        else if (revisao == NaturezaDoConteudo.ConclusaoDoAgente) avisos.Add("Revisão do agente, não confirmada");
        if (!HumanaOuAgente(origem)) avisos.Add("Autoria de origem desconhecida, não comprovada");
        if (!HumanaOuAgente(revisao)) avisos.Add("Natureza da revisão não registrada; autoria não comprovada");
        if (avisos.Count > 0)
        {
            if (status is StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido) avisos.Add(status.ToString()!);
            return string.Join("; ", avisos);
        }
        return status switch
        {
            StatusDoConhecimento.Confirmado => "Confirmado",
            StatusDoConhecimento.Inferido => "Inferido, não confirmado",
            StatusDoConhecimento.Temporario => "Temporário, não confirmado",
            StatusDoConhecimento.Inativo => "Inativo",
            StatusDoConhecimento.Substituido => "Substituído",
            _ => "Estado epistêmico não comprovado"
        };
    }
    public static string RotularFonte(AutoriaDaFonte autoria) => autoria switch
    {
        AutoriaDaFonte.Agente => "Fonte bruta de autoria do agente, não confirmada",
        AutoriaDaFonte.Usuario => "Fonte bruta de autoria do usuário, não confirmada",
        _ => "Fonte bruta de autoria desconhecida, não comprovada"
    };
    private static bool HumanaOuAgente(NaturezaDoConteudo? natureza) => natureza is
        NaturezaDoConteudo.DitoPeloUsuario or NaturezaDoConteudo.FonteSelecionada or NaturezaDoConteudo.ConclusaoDoAgente;
}
