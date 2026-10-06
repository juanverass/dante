using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Dante.Domain.RelacoesDeConhecimento;
using Dante.Domain.DocumentosFonte;
namespace Dante.Application.AuditoriaDoBrain;

public sealed record DadosDeAuditoria(EspacoDeConhecimento Espaco, IReadOnlyList<Projeto> Projetos,
    IReadOnlyList<Conhecimento> Conhecimentos, IReadOnlyList<RelacaoDeConhecimento> Relacoes,
    IReadOnlyList<DocumentoFonte> Fontes, bool TemMais);
