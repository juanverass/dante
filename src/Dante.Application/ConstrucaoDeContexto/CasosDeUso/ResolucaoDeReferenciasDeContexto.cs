using Dante.Application.BuscaDoBrain;
using Dante.Application.CapturaDeConhecimento;
using Dante.Application.DocumentosFonte;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;

namespace Dante.Application.ConstrucaoDeContexto;

internal static class ResolucaoDeReferenciasDeContexto
{
    internal static RegistroDeContextoDto Descartar(ReferenciaTipadaDeContexto r, string motivo) =>
        new(r.Chave, r.Id, r.Tipo, "descartado", motivo, 0);
    internal const string Indisponivel = "Referência indisponível, encerrada, fora dos filtros/escopo ou sem autorização.";
    internal const string Divergente = "Revisão da referência diverge da revisão disponível; conteúdo não substituído.";
    internal const string Conflitante = "Referências ao mesmo item exigem revisões diferentes; conteúdo não resolvido.";
    private static bool Permitida(Sensibilidade s, AcessoAoBrain acesso) =>
        s is Sensibilidade.Publico or Sensibilidade.Pessoal or Sensibilidade.Trabalho ||
        s == Sensibilidade.Confidencial && acesso.PermitirConfidencial;

    internal static async Task ResolverAsync(IReadOnlyList<ReferenciaTipadaDeContexto> referencias, AcessoAoBrain acesso,
        BuscaDoBrainSearchDto filtros, ICandidatoDeConhecimentoRepository candidatos, IDocumentoFonteRepository documentos,
        ICollection<ItemDeContextoDto> itens, ICollection<RegistroDeContextoDto> registros, CancellationToken ct)
    {
        foreach (var grupo in referencias.Where(r => r.Tipo != "conhecimento").GroupBy(r => (r.Tipo, r.Id)))
        {
            if (grupo.Count() > 1)
            { foreach (var conflito in grupo) registros.Add(Descartar(conflito, Conflitante)); continue; }
            var r = grupo.Single();
            if (r.Tipo == "candidato")
            {
                var c = await candidatos.ObterPorIdAsync(r.Id, ct);
                if (c is null || c.IdEspacoDeConhecimento != acesso.IdEspacoDeConhecimento || c.IdProjeto != acesso.IdProjeto ||
                    !Permitida(c.Sensibilidade, acesso) || c.Estado != EstadoDoCandidato.Pendente || !ElegibilidadeDeContexto.AtendeFiltros(c, filtros))
                { registros.Add(Descartar(r, Indisponivel)); continue; }
                if (c.Revisao != r.Revisao) { registros.Add(Descartar(r, Divergente)); continue; }
                var conteudo = ProtecaoDeSegredos.Redigir(string.Join("\n", new[] { c.Titulo, c.Conteudo }.Where(x => !string.IsNullOrWhiteSpace(x))))!;
                if (conteudo == "[conteúdo protegido]") { registros.Add(Descartar(r, Indisponivel)); continue; }
                itens.Add(new($"candidato:{c.Id:D}", c.Id, c.Revisao, "candidato", null, null, c.Sensibilidade, conteudo, r.Chave,
                    "Referência tipada do snapshot: candidato pendente, não confirmado.", 0.02, 0,
                    NaturezaDeOrigem: c.Natureza, Natureza: c.Natureza, Referenciado: true));
            }
            else
            {
                var d = await documentos.ObterPorIdAsync(r.Id, ct);
                if (d is null || d.Removido || d.IdEspacoDeConhecimento != acesso.IdEspacoDeConhecimento || d.IdProjeto != acesso.IdProjeto ||
                    !Permitida(d.Sensibilidade, acesso) || !ElegibilidadeDeContexto.AtendeFiltros(d, filtros))
                { registros.Add(Descartar(r, Indisponivel)); continue; }
                if (d.Revisao != r.Revisao) { registros.Add(Descartar(r, Divergente)); continue; }
                var conteudo = ProtecaoDeSegredos.Redigir(d.Conteudo)!;
                if (conteudo == "[conteúdo protegido]") { registros.Add(Descartar(r, Indisponivel)); continue; }
                itens.Add(new($"fonte:{d.Id:D}", d.Id, d.Revisao, "fonte_bruta", null, null, d.Sensibilidade, conteudo, r.Chave,
                    "Referência tipada do snapshot: fonte bruta, exige consolidação para ser fato.", 0.02, 0,
                    AutoriaDaFonte: d.Autoria, Referenciado: true));
            }
        }
    }
}
