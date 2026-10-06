using Dante.Application.Conhecimentos;
using Dante.Application.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
namespace Dante.Application.SegurancaDoBrain;

internal static class SaidaAutorizadaDoBrain
{
    private static bool Permitida(Sensibilidade classe, AutorizacaoDoBrain auth) =>
        auth.Escopo is { } acesso && (classe < Sensibilidade.Confidencial ||
            classe == Sensibilidade.Confidencial && acesso.PermitirConfidencial || classe == Sensibilidade.Secreto && acesso.PermitirSecreto);
    private static ProvenienciaDto Redigir(ProvenienciaDto p) => p with
    {
        Origem = ProtecaoDeSegredos.Redigir(p.Origem)!, ReferenciaDaFonte = ProtecaoDeSegredos.Redigir(p.ReferenciaDaFonte),
        RevisaoDaFonte = ProtecaoDeSegredos.Redigir(p.RevisaoDaFonte), TrechoDaFonte = ProtecaoDeSegredos.Redigir(p.TrechoDaFonte)
    };
    public static ConhecimentoDto Projetar(ConhecimentoDto dto, AutorizacaoDoBrain? auth) => auth is null ? dto : dto with
    {
        Conteudo = ProtecaoDeSegredos.Redigir(dto.Conteudo), DadosEstruturados = ProtecaoDeSegredos.Redigir(dto.DadosEstruturados),
        Tags = dto.Tags.Select(t => ProtecaoDeSegredos.Redigir(t)!).ToArray(), Proveniencia = Redigir(dto.Proveniencia),
        Historico = dto.Historico.Where(r => Permitida(r.Sensibilidade, auth)).Select(r => r with
        {
            Conteudo = ProtecaoDeSegredos.Redigir(r.Conteudo), DadosEstruturados = ProtecaoDeSegredos.Redigir(r.DadosEstruturados),
            Tags = r.Tags.Select(t => ProtecaoDeSegredos.Redigir(t)!).ToArray(), Proveniencia = Redigir(r.Proveniencia)
        }).ToArray()
    };
    public static CandidatoDeConhecimentoDto Projetar(CandidatoDeConhecimentoDto dto, AutorizacaoDoBrain? auth) => auth is null ? dto : dto with
    {
        Conteudo = ProtecaoDeSegredos.Redigir(dto.Conteudo)!,
        Historico = dto.Historico.Where(r => Permitida(r.Sensibilidade, auth)).Select(r => r with
        { Conteudo = ProtecaoDeSegredos.Redigir(r.Conteudo)!, Justificativa = ProtecaoDeSegredos.Redigir(r.Justificativa)!, Proveniencia = Redigir(r.Proveniencia) }).ToArray()
    };
}
