using Dante.Domain.Execucoes;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class EtapaDaExecucaoDbMapping : EntidadeConfiguration<EtapaDaExecucao>
{
    public override void Configure(EntityTypeBuilder<EtapaDaExecucao> b)
    {
        base.Configure(b);
        b.ToTable("etapas_da_execucao", t =>
        {
            t.HasCheckConstraint("ck_etapas_da_execucao_numero_positivo", "numero > 0");
            t.HasCheckConstraint("ck_etapas_da_execucao_tentativa_positiva", "tentativa > 0");
            // Passo de roteiro (AD-69): passo e rodada juntos; papel e versão do agente juntos, e só fora da aprovação.
            t.HasCheckConstraint("ck_etapas_da_execucao_passo_do_roteiro",
                "(passo IS NULL) = (rodada IS NULL) AND (rodada IS NULL OR rodada >= 0) AND " +
                "(papel IS NULL) = (id_versao_do_agente IS NULL)");
            // Unidade de plano (AD-75): só no tipo UnidadeDoPlano, sempre com plano, unidade, papel e versão, nunca
            // escrita nem passo de roteiro.
            t.HasCheckConstraint("ck_etapas_da_execucao_unidade_do_plano",
                "(id_plano IS NULL) = (unidade IS NULL) AND " +
                $"(tipo = {(int)TipoDeEtapa.UnidadeDoPlano}) = (id_plano IS NOT NULL) AND " +
                "(id_plano IS NULL OR (NOT escrita AND passo IS NULL AND papel IS NOT NULL))");
        });
        b.Property(x => x.IdExecucao).HasColumnName("id_execucao");
        b.Property(x => x.Numero).HasColumnName("numero");
        b.Property(x => x.Tipo).HasColumnName("tipo");
        b.Property(x => x.Estado).HasColumnName("estado");
        b.Property(x => x.Escrita).HasColumnName("escrita");
        b.Property(x => x.PoliticaDeRetomada).HasColumnName("politica_de_retomada");
        b.Property(x => x.Tentativa).HasColumnName("tentativa");
        b.Property(x => x.ChaveDeIdempotencia).HasColumnName("chave_de_idempotencia").HasMaxLength(100);
        b.Property(x => x.Agente).HasColumnName("agente");
        b.Property(x => x.Modelo).HasColumnName("modelo").HasMaxLength(EtapaDaExecucao.TamanhoMaximoDoModelo);
        b.Property(x => x.Esforco).HasColumnName("esforco").HasMaxLength(EtapaDaExecucao.TamanhoMaximoDoModelo);
        b.Property(x => x.IdSessaoDeAgente).HasColumnName("id_sessao_de_agente");
        b.Property(x => x.IdTurno).HasColumnName("id_turno").HasMaxLength(EtapaDaExecucao.TamanhoMaximoDoTurno);
        b.Property(x => x.CodigoDeSaida).HasColumnName("codigo_de_saida");
        b.Property(x => x.Erro).HasColumnName("erro").HasMaxLength(Execucao.TamanhoMaximoDoTexto);
        b.Property(x => x.Motivo).HasColumnName("motivo").HasMaxLength(Execucao.TamanhoMaximoDoTexto);
        b.Property(x => x.CriadaEm).HasColumnName("criada_em");
        b.Property(x => x.IniciadaEm).HasColumnName("iniciada_em");
        b.Property(x => x.EncerradaEm).HasColumnName("encerrada_em");
        b.Property(x => x.Passo).HasColumnName("passo");
        b.Property(x => x.Rodada).HasColumnName("rodada");
        b.Property(x => x.Papel).HasColumnName("papel").HasMaxLength(Dante.Domain.Grupos.ConfiguracaoDoGrupo.TamanhoMaximoDoPapel);
        b.Property(x => x.IdVersaoDoAgente).HasColumnName("id_versao_do_agente");
        b.Property(x => x.IdPlano).HasColumnName("id_plano");
        b.Property(x => x.Unidade).HasColumnName("unidade").HasMaxLength(16);
        b.Ignore(x => x.Terminal);
        // Chave alternativa: dependências apontam para (etapa, execução), e o banco recusa dependência entre execuções.
        b.HasAlternateKey(x => new { x.Id, x.IdExecucao });
        b.HasOne<Execucao>().WithMany().HasForeignKey(x => x.IdExecucao).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SessaoDeAgente>().WithMany().HasForeignKey(x => x.IdSessaoDeAgente).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Dante.Domain.Agentes.VersaoDoAgente>().WithMany().HasForeignKey(x => x.IdVersaoDoAgente)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.IdExecucao, x.Numero }).IsUnique();
        b.HasIndex(x => x.ChaveDeIdempotencia).IsUnique();
        b.HasOne<PlanoDaCoordenacao>().WithMany().HasForeignKey(x => x.IdPlano).OnDelete(DeleteBehavior.Restrict);
        // Cada unidade de um plano vira uma etapa só: materializar de novo o mesmo plano é recusado.
        b.HasIndex(x => new { x.IdPlano, x.Unidade }).IsUnique().HasFilter("id_plano IS NOT NULL");
    }
}
