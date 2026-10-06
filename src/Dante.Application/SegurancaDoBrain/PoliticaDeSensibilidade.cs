using Dante.Domain.Conhecimentos;
namespace Dante.Application.SegurancaDoBrain;

public sealed class PoliticaDeSensibilidade
{
    public bool PermiteConteudo(Conhecimento item, AcessoAoBrain acesso, FinalidadeDeLeitura finalidade)
    {
        if (!Enum.IsDefined(finalidade) || acesso.IdUsuario == Guid.Empty || acesso.IdEspacoDeConhecimento == Guid.Empty ||
            item.IdEspacoDeConhecimento != acesso.IdEspacoDeConhecimento || item.IdProjeto != acesso.IdProjeto) return false;
        return item.Sensibilidade switch
        {
            Sensibilidade.Publico or Sensibilidade.Pessoal or Sensibilidade.Trabalho => true,
            Sensibilidade.Confidencial => acesso.PermitirConfidencial && finalidade != FinalidadeDeLeitura.IndexacaoExterna,
            Sensibilidade.Secreto => acesso.PermitirSecreto && finalidade == FinalidadeDeLeitura.Leitura,
            _ => false
        };
    }
    public LeituraProtegidaDto Projetar(Conhecimento item, AcessoAoBrain acesso, FinalidadeDeLeitura finalidade)
    {
        if (item.IdEspacoDeConhecimento != acesso.IdEspacoDeConhecimento || item.IdProjeto != acesso.IdProjeto)
            throw new UnauthorizedAccessException("Conhecimento fora do escopo autorizado.");
        var permitido = PermiteConteudo(item, acesso, finalidade);
        // Não exportar histórico: uma revisão antiga pode ter classificação/conteúdo mais sensível.
        return new(item.Id, item.IdEspacoDeConhecimento, item.IdProjeto, item.Tipo, item.Status, item.Sensibilidade, item.Revisao,
            permitido ? ProtecaoDeSegredos.Redigir(item.Conteudo) : null,
            permitido ? ProtecaoDeSegredos.Redigir(item.DadosEstruturados) : null,
            permitido ? item.Tags.Select(x => ProtecaoDeSegredos.Redigir(x)!).ToArray() : [],
            permitido ? ProtecaoDeSegredos.Redigir(item.Proveniencia.Origem) : null,
            permitido ? ProtecaoDeSegredos.Redigir(item.Proveniencia.ReferenciaDaFonte) : null, !permitido);
    }
}
