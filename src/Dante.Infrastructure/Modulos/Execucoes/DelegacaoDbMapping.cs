using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Execucoes;

// Delegações (AD-70, #309). A etapa de origem é da execução de origem (FK composta); a subordinada é única; a mesma
// unidade de trabalho (raiz + chave) existe no máximo uma vez, em qualquer estado (índice único, não parcial: uma
// delegação encerrada continua sendo a atribuição daquela unidade e nunca libera a chave). Entrada e
// resultado ficam como texto, não jsonb: o hash é do JSON canônico exato, e jsonb reordenaria as chaves. Checks: sem
// escrita, contrato do catálogo, prazos dentro do teto, hashes, cadeia sem o destinatário e resultado presente só na
// resposta e só da versão destinatária. Triggers da migration: hierarquia conferida na inserção, imutabilidade dos
// campos de identidade, transições válidas, resultado imutável e nenhuma exclusão.
public sealed class DelegacaoDbMapping : EntidadeConfiguration<Delegacao>
{
    public override void Configure(EntityTypeBuilder<Delegacao> b)
    {
        base.Configure(b);
        b.ToTable("delegacoes", t =>
        {
            t.HasCheckConstraint("ck_delegacoes_sem_escrita", "escrita = false");
            t.HasCheckConstraint("ck_delegacoes_contrato",
                $"contrato IN ({string.Join(", ", ContratosDeDelegacao.Todos.Select(c => $"'{c.Identificador}'"))})");
            t.HasCheckConstraint("ck_delegacoes_estado",
                $"estado BETWEEN {(int)EstadoDaDelegacao.Solicitada} AND {(int)EstadoDaDelegacao.Recusada}");
            t.HasCheckConstraint("ck_delegacoes_prazos",
                $"minutos_para_iniciar BETWEEN 1 AND {LimitesDeDelegacao.Teto.MinutosParaIniciar} AND " +
                $"minutos_de_duracao BETWEEN 1 AND {LimitesDeDelegacao.Teto.MinutosDeDuracao}");
            t.HasCheckConstraint("ck_delegacoes_hashes",
                "hash_da_versao_solicitante ~ '^[0-9a-f]{64}$' AND hash_da_versao_destinataria ~ '^[0-9a-f]{64}$' AND " +
                "hash_da_entrada ~ '^[0-9a-f]{64}$' AND hash_do_objetivo ~ '^[0-9a-f]{64}$' AND " +
                "chave_de_deduplicacao ~ '^[0-9a-f]{64}$' AND " +
                "(hash_do_resultado IS NULL OR hash_do_resultado ~ '^[0-9a-f]{64}$') AND " +
                "(hash_do_artefato_lido IS NULL OR hash_do_artefato_lido ~ '^[0-9a-f]{64}$')");
            t.HasCheckConstraint("ck_delegacoes_hierarquia",
                "id_execucao_subordinada <> id_execucao_origem AND id_agente_destinatario <> id_agente_solicitante AND " +
                "cardinality(cadeia_de_agentes) >= 1 AND " +
                "cadeia_de_agentes[cardinality(cadeia_de_agentes)] = id_agente_solicitante AND " +
                "array_position(cadeia_de_agentes, NULL) IS NULL AND NOT (id_agente_destinatario = ANY (cadeia_de_agentes))");
            t.HasCheckConstraint("ck_delegacoes_resultado",
                $"(estado = {(int)EstadoDaDelegacao.Respondida}) = (resultado IS NOT NULL) AND " +
                "(resultado IS NULL) = (hash_do_resultado IS NULL) AND " +
                "(resultado IS NULL) = (id_versao_do_autor_do_resultado IS NULL) AND " +
                "(id_versao_do_autor_do_resultado IS NULL OR id_versao_do_autor_do_resultado = id_versao_destinataria) AND " +
                "(resultado IS NOT NULL OR (id_sessao_do_resultado IS NULL AND hash_do_artefato_lido IS NULL))");
        });
        b.Property(x => x.IdExecucaoOrigem).HasColumnName("id_execucao_origem");
        b.Property(x => x.IdEtapaOrigem).HasColumnName("id_etapa_origem");
        b.Property(x => x.IdExecucaoSubordinada).HasColumnName("id_execucao_subordinada");
        b.Property(x => x.IdExecucaoRaiz).HasColumnName("id_execucao_raiz");
        b.Property(x => x.IdAgenteSolicitante).HasColumnName("id_agente_solicitante");
        b.Property(x => x.IdVersaoSolicitante).HasColumnName("id_versao_solicitante");
        b.Property(x => x.HashDaVersaoSolicitante).HasColumnName("hash_da_versao_solicitante")
            .HasMaxLength(Delegacao.TamanhoDoHash).IsFixedLength();
        b.Property(x => x.IdAgenteDestinatario).HasColumnName("id_agente_destinatario");
        b.Property(x => x.HandleDoDestinatario).HasColumnName("handle_do_destinatario")
            .HasMaxLength(Delegacao.TamanhoMaximoDoHandle);
        b.Property(x => x.IdVersaoDestinataria).HasColumnName("id_versao_destinataria");
        b.Property(x => x.HashDaVersaoDestinataria).HasColumnName("hash_da_versao_destinataria")
            .HasMaxLength(Delegacao.TamanhoDoHash).IsFixedLength();
        b.Property(x => x.Contrato).HasColumnName("contrato").HasMaxLength(Delegacao.TamanhoMaximoDoContrato);
        b.Property(x => x.Entrada).HasColumnName("entrada").HasMaxLength(ContratoDeDelegacao.MaximoDoJson);
        b.Property(x => x.HashDaEntrada).HasColumnName("hash_da_entrada").HasMaxLength(Delegacao.TamanhoDoHash).IsFixedLength();
        b.Property(x => x.ObjetivoRedigido).HasColumnName("objetivo_redigido").HasMaxLength(Execucao.TamanhoMaximoDoObjetivo);
        b.Property(x => x.ObjetivoAlteradoPelaRedacao).HasColumnName("objetivo_alterado_pela_redacao");
        b.Property(x => x.HashDoObjetivo).HasColumnName("hash_do_objetivo").HasMaxLength(Delegacao.TamanhoDoHash)
            .IsFixedLength();
        b.Property(x => x.Criterios).HasColumnName("criterios").HasMaxLength(Delegacao.TamanhoMaximoDosCriterios);
        b.Property(x => x.MinutosParaIniciar).HasColumnName("minutos_para_iniciar");
        b.Property(x => x.MinutosDeDuracao).HasColumnName("minutos_de_duracao");
        b.Property(x => x.Escrita).HasColumnName("escrita");
        b.Property(x => x.CadeiaDeAgentes).HasColumnName("cadeia_de_agentes").HasColumnType("uuid[]")
            .HasConversion(x => x.ToArray(), x => Array.AsReadOnly(x), new ValueComparer<IReadOnlyList<Guid>>(
                (a, c) => a!.SequenceEqual(c!), lista => lista.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                lista => Array.AsReadOnly(lista.ToArray())));
        b.Property(x => x.ChaveDeDeduplicacao).HasColumnName("chave_de_deduplicacao").HasMaxLength(Delegacao.TamanhoDoHash)
            .IsFixedLength();
        b.Property(x => x.Estado).HasColumnName("estado");
        b.Property(x => x.Motivo).HasColumnName("motivo").HasMaxLength(Delegacao.TamanhoMaximoDoMotivo);
        b.Property(x => x.Resultado).HasColumnName("resultado").HasMaxLength(ContratoDeDelegacao.MaximoDoJson);
        b.Property(x => x.HashDoResultado).HasColumnName("hash_do_resultado").HasMaxLength(Delegacao.TamanhoDoHash)
            .IsFixedLength();
        b.Property(x => x.IdVersaoDoAutorDoResultado).HasColumnName("id_versao_do_autor_do_resultado");
        b.Property(x => x.IdSessaoDoResultado).HasColumnName("id_sessao_do_resultado");
        b.Property(x => x.HashDoArtefatoLido).HasColumnName("hash_do_artefato_lido").HasMaxLength(Delegacao.TamanhoDoHash)
            .IsFixedLength();
        b.Property(x => x.CriadaEm).HasColumnName("criada_em");
        b.Property(x => x.AceitaEm).HasColumnName("aceita_em");
        b.Property(x => x.IniciadaEm).HasColumnName("iniciada_em");
        b.Property(x => x.EncerradaEm).HasColumnName("encerrada_em");
        b.Ignore(x => x.Terminal);

        // A etapa de origem é da execução de origem: FK composta, como nas dependências entre etapas.
        b.HasOne<EtapaDaExecucao>().WithMany().HasForeignKey(x => new { x.IdEtapaOrigem, x.IdExecucaoOrigem })
            .HasPrincipalKey(x => new { x.Id, x.IdExecucao }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Execucao>().WithMany().HasForeignKey(x => x.IdExecucaoOrigem).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Execucao>().WithMany().HasForeignKey(x => x.IdExecucaoSubordinada).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Execucao>().WithMany().HasForeignKey(x => x.IdExecucaoRaiz).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Agente>().WithMany().HasForeignKey(x => x.IdAgenteSolicitante).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Agente>().WithMany().HasForeignKey(x => x.IdAgenteDestinatario).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VersaoDoAgente>().WithMany().HasForeignKey(x => x.IdVersaoSolicitante).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VersaoDoAgente>().WithMany().HasForeignKey(x => x.IdVersaoDestinataria).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VersaoDoAgente>().WithMany().HasForeignKey(x => x.IdVersaoDoAutorDoResultado)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SessaoDeAgente>().WithMany().HasForeignKey(x => x.IdSessaoDoResultado).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.IdExecucaoSubordinada).IsUnique();
        b.HasIndex(x => new { x.IdExecucaoRaiz, x.ChaveDeDeduplicacao }).IsUnique()
            .HasDatabaseName("ix_delegacoes_unidade");
        b.HasIndex(x => x.IdExecucaoOrigem);
        b.HasIndex(x => x.IdExecucaoRaiz);
    }
}
