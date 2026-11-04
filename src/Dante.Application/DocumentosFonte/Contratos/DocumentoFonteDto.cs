using Dante.Domain.Conhecimentos;
namespace Dante.Application.DocumentosFonte;

public sealed record DocumentoFonteDto(Guid Id, Guid IdEspacoDeConhecimento, Guid? IdProjeto, string Origem,
    string Formato, string Hash, int Revisao, Sensibilidade Sensibilidade, DateTimeOffset AtualizadoEm, bool Removido,
    AutoriaDaFonte Autoria = AutoriaDaFonte.Desconhecida, string? Titulo = null, IReadOnlyList<string>? Tags = null,
    string? Justificativa = null, string? Proveniencia = null)
{
    // Referência tipada aceita em DadosDoContexto.Referencias e resolvida pelo Context Builder (#255).
    public string Referencia => $"fonte:{Id:D}@{Revisao}";
    public string Rotulo => Dante.Application.Conhecimentos.RotulagemDoConhecimento.RotularFonte(Autoria);
}
