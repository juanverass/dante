using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.ContextosDeTrabalho;
using Dante.Domain.DocumentosFonte;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Dante.Domain.RelacoesDeConhecimento;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Data;

internal static class FiltrosDoBrain
{
    public static void Aplicar(ModelBuilder modelo, DanteDbContext contexto)
    {
        modelo.Entity<EspacoDeConhecimento>().HasQueryFilter(x => contexto.Administracao ||
            x.IdTenant == contexto.IdTenant && x.IdUsuario == contexto.IdUsuario &&
            (contexto.IdEspaco == Guid.Empty || x.Id == contexto.IdEspaco));
        modelo.Entity<Projeto>().HasQueryFilter(x => contexto.Administracao ||
            x.IdEspacoDeConhecimento == contexto.IdEspaco && (contexto.IdProjeto == null || x.Id == contexto.IdProjeto) &&
            contexto.EspacosDeConhecimento.Any(e => e.Id == x.IdEspacoDeConhecimento));
        AplicarEscopoESensibilidade<DocumentoFonte>(modelo, contexto);
        AplicarEscopoESensibilidade<Conhecimento>(modelo, contexto);
        AplicarEscopoESensibilidade<CandidatoDeConhecimento>(modelo, contexto);
        // Snapshot nunca expõe Secreto, mesmo com permissão de leitura do conhecimento canônico.
        AplicarEscopoESensibilidade<ContextoDeTrabalho>(modelo, contexto, permitirSecreto: false);
        modelo.Entity<RelacaoDeConhecimento>().HasQueryFilter(x => contexto.Administracao ||
            x.IdEspacoDeConhecimento == contexto.IdEspaco && x.IdProjeto == contexto.IdProjeto &&
            contexto.Conhecimentos.Any(c => c.Id == x.IdOrigem) && contexto.Conhecimentos.Any(c => c.Id == x.IdDestino));
    }

    private static void AplicarEscopoESensibilidade<TEntity>(ModelBuilder modelo, DanteDbContext contexto,
        bool permitirSecreto = true) where TEntity : class
    {
        modelo.Entity<TEntity>().HasQueryFilter(x => contexto.Administracao ||
            EF.Property<Guid>(x, "IdEspacoDeConhecimento") == contexto.IdEspaco &&
            EF.Property<Guid?>(x, "IdProjeto") == contexto.IdProjeto &&
            contexto.EspacosDeConhecimento.Any(e => e.Id == EF.Property<Guid>(x, "IdEspacoDeConhecimento")) &&
            (EF.Property<Sensibilidade>(x, "Sensibilidade") < Sensibilidade.Confidencial ||
             EF.Property<Sensibilidade>(x, "Sensibilidade") == Sensibilidade.Confidencial && contexto.Confidencial ||
             permitirSecreto && EF.Property<Sensibilidade>(x, "Sensibilidade") == Sensibilidade.Secreto && contexto.Secreto));
    }
}
