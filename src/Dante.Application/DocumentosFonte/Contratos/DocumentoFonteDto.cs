using Dante.Domain.Conhecimentos;
namespace Dante.Application.DocumentosFonte;

public sealed record DocumentoFonteDto(Guid Id, Guid IdEspacoDeConhecimento, Guid? IdProjeto, string Origem,
    string Formato, string Hash, int Revisao, Sensibilidade Sensibilidade, DateTimeOffset AtualizadoEm, bool Removido,
    AutoriaDaFonte Autoria = AutoriaDaFonte.Desconhecida)
{
    public string Rotulo => Dante.Application.Conhecimentos.RotulagemDoConhecimento.RotularFonte(Autoria);
}
