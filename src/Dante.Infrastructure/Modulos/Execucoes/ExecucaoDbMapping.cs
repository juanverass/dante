using Dante.Domain.Execucoes;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class ExecucaoDbMapping : EntidadeConfiguration<Execucao>
{
    public override void Configure(EntityTypeBuilder<Execucao> b)
    {
        base.Configure(b);
        b.ToTable("execucoes", t =>
        {
            t.HasCheckConstraint("ck_execucoes_numero_positivo", "numero > 0");
            // Tarefa (AD-68) sempre aponta para a versão exata do agente responsável.
            t.HasCheckConstraint("ck_execucoes_tarefa_com_versao",
                $"tipo <> {(int)TipoDeExecucao.Tarefa} OR id_versao_do_agente IS NOT NULL");
            // Roteiro (AD-69) e Coordenada (AD-75), e só eles, apontam para a versão do grupo; o Roteiro nunca para um
            // agente único, a Coordenada sempre para a versão congelada do coordenador.
            t.HasCheckConstraint("ck_execucoes_roteiro_com_grupo",
                $"(tipo IN ({(int)TipoDeExecucao.Roteiro}, {(int)TipoDeExecucao.Coordenada})) = (id_versao_do_grupo IS NOT NULL) AND " +
                $"(tipo <> {(int)TipoDeExecucao.Roteiro} OR id_versao_do_agente IS NULL) AND " +
                $"(tipo <> {(int)TipoDeExecucao.Coordenada} OR id_versao_do_agente IS NOT NULL)");
            // Hierarquia (AD-70, #309): a raiz não tem pai, tem profundidade 0 e aponta para si; só a Delegada tem pai,
            // sempre com a versão do agente destinatário. Profundidade até o teto global (o trigger da migration confere
            // que ela é a do pai mais um, que a raiz é a do pai e que a autoridade é a do pai).
            t.HasCheckConstraint("ck_execucoes_hierarquia",
                "(id_execucao_pai IS NULL) = (profundidade = 0) AND (profundidade = 0) = (id_execucao_raiz = id) AND " +
                $"profundidade BETWEEN 0 AND {Dante.Domain.Agentes.LimitesDeDelegacao.Teto.ProfundidadeMaxima}");
            t.HasCheckConstraint("ck_execucoes_delegada",
                $"(tipo = {(int)TipoDeExecucao.Delegada}) = (id_execucao_pai IS NOT NULL) AND " +
                $"(tipo <> {(int)TipoDeExecucao.Delegada} OR id_versao_do_agente IS NOT NULL)");
        });
        b.Property(x => x.Numero).HasColumnName("numero");
        b.Property(x => x.Tipo).HasColumnName("tipo");
        b.Property(x => x.Estado).HasColumnName("estado");
        b.Property(x => x.Canal).HasColumnName("canal").HasMaxLength(Execucao.TamanhoMaximoDoCanal);
        b.Property(x => x.IdDoUsuarioNoCanal).HasColumnName("id_do_usuario_no_canal");
        b.Property(x => x.Contexto).HasColumnName("contexto").HasColumnType("jsonb");
        b.Property(x => x.Objetivo).HasColumnName("objetivo").HasMaxLength(Execucao.TamanhoMaximoDoObjetivo);
        b.Property(x => x.Erro).HasColumnName("erro").HasMaxLength(Execucao.TamanhoMaximoDoTexto);
        b.Property(x => x.Motivo).HasColumnName("motivo").HasMaxLength(Execucao.TamanhoMaximoDoTexto);
        b.Property(x => x.CriadaEm).HasColumnName("criada_em");
        b.Property(x => x.IniciadaEm).HasColumnName("iniciada_em");
        b.Property(x => x.EncerradaEm).HasColumnName("encerrada_em");
        b.Property(x => x.IdVersaoDoAgente).HasColumnName("id_versao_do_agente");
        b.Property(x => x.IdVersaoDoGrupo).HasColumnName("id_versao_do_grupo");
        b.Property(x => x.IdExecucaoPai).HasColumnName("id_execucao_pai");
        b.Property(x => x.IdExecucaoRaiz).HasColumnName("id_execucao_raiz");
        b.Property(x => x.Profundidade).HasColumnName("profundidade");
        b.Ignore(x => x.Codigo);
        b.Ignore(x => x.Terminal);
        b.Ignore(x => x.Nova);
        b.Ignore(x => x.Raiz);
        b.HasOne<Dante.Domain.Agentes.VersaoDoAgente>().WithMany().HasForeignKey(x => x.IdVersaoDoAgente)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Dante.Domain.Grupos.VersaoDoGrupo>().WithMany().HasForeignKey(x => x.IdVersaoDoGrupo)
            .OnDelete(DeleteBehavior.Restrict);
        // A raiz aponta para si mesma: essa FK (fk_execucoes_raiz) é criada pela migration, fora do modelo, para o EF
        // não ver um ciclo ao inserir uma raiz. A do pai fica no modelo.
        b.HasOne<Execucao>().WithMany().HasForeignKey(x => x.IdExecucaoPai).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.IdExecucaoPai);
        b.HasIndex(x => x.IdExecucaoRaiz);
        b.HasIndex(x => x.Numero).IsUnique();
        b.HasIndex(x => x.Estado);
    }
}
