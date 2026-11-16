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
        b.ToTable("execucoes", t => t.HasCheckConstraint("ck_execucoes_numero_positivo", "numero > 0"));
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
        b.Ignore(x => x.Codigo);
        b.Ignore(x => x.Terminal);
        b.HasIndex(x => x.Numero).IsUnique();
        b.HasIndex(x => x.Estado);
    }
}
