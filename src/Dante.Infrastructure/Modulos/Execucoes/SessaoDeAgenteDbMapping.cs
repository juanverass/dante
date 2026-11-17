using Dante.Domain.Execucoes;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class SessaoDeAgenteDbMapping : EntidadeConfiguration<SessaoDeAgente>
{
    public override void Configure(EntityTypeBuilder<SessaoDeAgente> b)
    {
        base.Configure(b);
        b.ToTable("sessoes_de_agente", t => t.HasCheckConstraint("ck_sessoes_de_agente_numero_positivo", "numero > 0"));
        b.Property(x => x.Numero).HasColumnName("numero");
        b.Property(x => x.Agente).HasColumnName("agente");
        b.Property(x => x.Estado).HasColumnName("estado");
        b.Property(x => x.Canal).HasColumnName("canal").HasMaxLength(Execucao.TamanhoMaximoDoCanal);
        b.Property(x => x.IdDoDono).HasColumnName("id_do_dono");
        b.Property(x => x.Contexto).HasColumnName("contexto").HasColumnType("jsonb");
        b.Property(x => x.Perfil).HasColumnName("perfil");
        b.Property(x => x.Modelo).HasColumnName("modelo").HasMaxLength(EtapaDaExecucao.TamanhoMaximoDoModelo);
        b.Property(x => x.Esforco).HasColumnName("esforco").HasMaxLength(EtapaDaExecucao.TamanhoMaximoDoModelo);
        b.Property(x => x.ModeloInformado).HasColumnName("modelo_informado").HasMaxLength(EtapaDaExecucao.TamanhoMaximoDoModelo);
        b.Property(x => x.IdUpstream).HasColumnName("id_upstream").HasMaxLength(SessaoDeAgente.TamanhoMaximoDoUpstream);
        b.Property(x => x.IdExecucao).HasColumnName("id_execucao");
        b.Property(x => x.BootDoWorker).HasColumnName("boot_do_worker");
        b.Property(x => x.MaquinaDoWorker).HasColumnName("maquina_do_worker").HasMaxLength(SessaoDeAgente.TamanhoMaximoDaMaquina);
        b.Property(x => x.UnidadeDeContencao).HasColumnName("unidade_de_contencao").HasMaxLength(SessaoDeAgente.TamanhoMaximoDaUnidade);
        b.Property(x => x.MotivoDoFim).HasColumnName("motivo_do_fim").HasMaxLength(Execucao.TamanhoMaximoDoTexto);
        b.Property(x => x.CriadaEm).HasColumnName("criada_em");
        b.Property(x => x.IniciadaEm).HasColumnName("iniciada_em");
        b.Property(x => x.EncerradaEm).HasColumnName("encerrada_em");
        b.Property(x => x.HandleDoAgente).HasColumnName("handle_do_agente")
            .HasMaxLength(Dante.Domain.Agentes.Agente.TamanhoMaximoDoHandle);
        b.Property(x => x.HashDaConfiguracaoDoAgente).HasColumnName("hash_da_configuracao_do_agente")
            .HasMaxLength(Dante.Domain.Agentes.VersaoDoAgente.TamanhoDoHash).IsFixedLength();
        b.Property(x => x.IdVersaoDoAgente).HasColumnName("id_versao_do_agente");
        b.Ignore(x => x.Codigo);
        b.Ignore(x => x.Terminal);
        b.HasOne<Execucao>().WithMany().HasForeignKey(x => x.IdExecucao).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Dante.Domain.Agentes.VersaoDoAgente>().WithMany().HasForeignKey(x => x.IdVersaoDoAgente)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Numero).IsUnique();
        b.HasIndex(x => x.Estado);
        b.HasIndex(x => x.UnidadeDeContencao);
    }
}
