using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dante.Infrastructure.Modulos.Execucoes;

// Aprovação de ação protegida do gateway (AD-72, #311). Os checks prendem a linha ao payload: o hash é o SHA-256 dos
// bytes do payload (recalculado pelo banco), e ação, execução, etapa, alvo, escopo e solicitante do payload são os das
// colunas; a ação está entre as protegidas do catálogo, com o risco do catálogo. Um índice único impede dois pedidos
// vigentes (ou um já executado ou incerto) para o mesmo payload: replay por pedido novo é recusado pelo banco. O trigger
// (migration AprovacoesDeAcao) confere o dono da raiz na inserção, as transições, o decisor, os prazos e a imutabilidade.
public sealed class AprovacaoDeAcaoDbMapping : EntidadeConfiguration<AprovacaoDeAcao>
{
    // Estados em que o payload ainda pode ter efeito ou já teve: um segundo pedido igual é replay.
    internal static readonly EstadoDaAprovacaoDeAcao[] Vigentes =
    [
        EstadoDaAprovacaoDeAcao.Pendente, EstadoDaAprovacaoDeAcao.Aprovada, EstadoDaAprovacaoDeAcao.EmExecucao,
        EstadoDaAprovacaoDeAcao.Executada, EstadoDaAprovacaoDeAcao.Incerto
    ];

    public override void Configure(EntityTypeBuilder<AprovacaoDeAcao> b)
    {
        base.Configure(b);
        b.ToTable("aprovacoes_de_acao", t =>
        {
            t.HasCheckConstraint("ck_aprovacoes_de_acao_hash",
                "hash ~ '^[0-9a-f]{64}$' AND hash = encode(sha256(convert_to(payload, 'UTF8')), 'hex')");
            t.HasCheckConstraint("ck_aprovacoes_de_acao_payload",
                "payload::jsonb ->> 'acao' = acao AND payload::jsonb ->> 'execucao' = id_execucao::text AND " +
                "(payload::jsonb ->> 'etapa') IS NOT DISTINCT FROM id_etapa::text AND " +
                "(payload::jsonb ->> 'alvo') IS NOT DISTINCT FROM alvo AND " +
                $"payload::jsonb ->> 'escopo' = '{PayloadDaAcao.Escopo}' AND " +
                $"payload::jsonb ->> 'formato' = '{PayloadDaAcao.Formato}' AND " +
                "(payload::jsonb -> 'solicitante' ->> 'versao') IS NOT DISTINCT FROM id_versao_do_solicitante::text AND " +
                "(payload::jsonb -> 'solicitante' ->> 'hash') IS NOT DISTINCT FROM hash_da_versao_do_solicitante::text AND " +
                // Tipo explícito: dono (canal e usuário do dono, sem versão) ou agente (com versão). O objeto inteiro e o
                // hash da versão referenciada são conferidos pelo trigger, que consulta versoes_de_agente. COALESCE:
                // chave ausente (NULL) não passa pelo check.
                "COALESCE(CASE WHEN id_versao_do_solicitante IS NULL THEN payload::jsonb -> 'solicitante' ->> 'tipo' = 'dono' AND " +
                "payload::jsonb -> 'solicitante' ->> 'canal' = canal_do_dono AND " +
                "payload::jsonb -> 'solicitante' ->> 'usuario' = id_do_dono::text " +
                "ELSE payload::jsonb -> 'solicitante' ->> 'tipo' = 'agente' END, false)");
            t.HasCheckConstraint("ck_aprovacoes_de_acao_acao", $"(acao, risco) IN ({AcoesProtegidas()})");
            t.HasCheckConstraint("ck_aprovacoes_de_acao_escopo", $"escopo = {(int)EscopoDaAprovacao.UmaVez}");
            t.HasCheckConstraint("ck_aprovacoes_de_acao_estado",
                $"estado BETWEEN 0 AND {(int)EstadoDaAprovacaoDeAcao.Incerto}");
            t.HasCheckConstraint("ck_aprovacoes_de_acao_validade", "expira_em > criada_em");
            t.HasCheckConstraint("ck_aprovacoes_de_acao_solicitante",
                "(id_versao_do_solicitante IS NULL) = (hash_da_versao_do_solicitante IS NULL) AND " +
                "(hash_da_versao_do_solicitante IS NULL OR hash_da_versao_do_solicitante ~ '^[0-9a-f]{64}$')");
            // Decisor: os três juntos; nunca no pendente; sempre depois de aprovada ou recusada.
            t.HasCheckConstraint("ck_aprovacoes_de_acao_decisao",
                "(canal_do_decisor IS NULL) = (id_do_decisor IS NULL) AND (id_do_decisor IS NULL) = (decidida_em IS NULL) AND " +
                $"(estado <> {(int)EstadoDaAprovacaoDeAcao.Pendente} OR decidida_em IS NULL) AND " +
                $"(estado NOT IN ({Estados(EstadoDaAprovacaoDeAcao.Aprovada, EstadoDaAprovacaoDeAcao.Recusada,
                    EstadoDaAprovacaoDeAcao.EmExecucao, EstadoDaAprovacaoDeAcao.Executada, EstadoDaAprovacaoDeAcao.Falhou,
                    EstadoDaAprovacaoDeAcao.Incerto)}) OR decidida_em IS NOT NULL)");
            t.HasCheckConstraint("ck_aprovacoes_de_acao_execucao",
                $"(iniciada_em IS NOT NULL) = (estado IN ({Estados(EstadoDaAprovacaoDeAcao.EmExecucao,
                    EstadoDaAprovacaoDeAcao.Executada, EstadoDaAprovacaoDeAcao.Falhou, EstadoDaAprovacaoDeAcao.Incerto)}))");
            t.HasCheckConstraint("ck_aprovacoes_de_acao_encerramento",
                $"(encerrada_em IS NOT NULL) = (estado IN ({Estados(EstadoDaAprovacaoDeAcao.Recusada,
                    EstadoDaAprovacaoDeAcao.Expirada, EstadoDaAprovacaoDeAcao.Cancelada, EstadoDaAprovacaoDeAcao.Executada,
                    EstadoDaAprovacaoDeAcao.Falhou, EstadoDaAprovacaoDeAcao.Incerto)}))");
        });
        b.Property(x => x.IdExecucao).HasColumnName("id_execucao");
        b.Property(x => x.IdExecucaoRaiz).HasColumnName("id_execucao_raiz");
        b.Property(x => x.IdEtapa).HasColumnName("id_etapa");
        b.Property(x => x.Acao).HasColumnName("acao").HasMaxLength(CatalogoDeAcoes.TamanhoMaximoDoNome);
        b.Property(x => x.Risco).HasColumnName("risco");
        b.Property(x => x.Escopo).HasColumnName("escopo");
        b.Property(x => x.Alvo).HasColumnName("alvo").HasMaxLength(ReservaDeWorktree.TamanhoMaximoDoAlias);
        b.Property(x => x.IdVersaoDoSolicitante).HasColumnName("id_versao_do_solicitante");
        b.Property(x => x.HashDaVersaoDoSolicitante).HasColumnName("hash_da_versao_do_solicitante")
            .HasMaxLength(AprovacaoDeAcao.TamanhoDoHash).IsFixedLength();
        b.Property(x => x.CanalDoDono).HasColumnName("canal_do_dono").HasMaxLength(Execucao.TamanhoMaximoDoCanal);
        b.Property(x => x.IdDoDono).HasColumnName("id_do_dono");
        // Texto, não jsonb: o hash é dos bytes exatos da forma canônica.
        b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("text");
        b.Property(x => x.Hash).HasColumnName("hash").HasMaxLength(AprovacaoDeAcao.TamanhoDoHash).IsFixedLength();
        b.Property(x => x.Estado).HasColumnName("estado");
        b.Property(x => x.CriadaEm).HasColumnName("criada_em");
        b.Property(x => x.ExpiraEm).HasColumnName("expira_em");
        b.Property(x => x.CanalDoDecisor).HasColumnName("canal_do_decisor").HasMaxLength(Execucao.TamanhoMaximoDoCanal);
        b.Property(x => x.IdDoDecisor).HasColumnName("id_do_decisor");
        b.Property(x => x.DecididaEm).HasColumnName("decidida_em");
        b.Property(x => x.Motivo).HasColumnName("motivo").HasMaxLength(AprovacaoDeAcao.TamanhoMaximoDoMotivo);
        b.Property(x => x.IniciadaEm).HasColumnName("iniciada_em");
        b.Property(x => x.EncerradaEm).HasColumnName("encerrada_em");
        b.Property(x => x.Resultado).HasColumnName("resultado").HasMaxLength(AprovacaoDeAcao.TamanhoMaximoDoResultado);
        b.Ignore(x => x.Terminal);
        b.HasOne<Execucao>().WithMany().HasForeignKey(x => x.IdExecucao).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Execucao>().WithMany().HasForeignKey(x => x.IdExecucaoRaiz).OnDelete(DeleteBehavior.Restrict);
        // Etapa da própria execução (chave alternativa da etapa): nunca a de outra.
        b.HasOne<EtapaDaExecucao>().WithMany().HasForeignKey(x => new { x.IdEtapa, x.IdExecucao })
            .HasPrincipalKey(x => new { x.Id, x.IdExecucao }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VersaoDoAgente>().WithMany().HasForeignKey(x => x.IdVersaoDoSolicitante).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Hash).IsUnique().HasFilter($"estado IN ({Estados(Vigentes)})")
            .HasDatabaseName("ux_aprovacoes_de_acao_hash_vigente");
        // Pedidos em aberto do dono (listar e expirar).
        b.HasIndex(x => new { x.CanalDoDono, x.IdDoDono })
            .HasFilter($"estado IN ({Estados(EstadoDaAprovacaoDeAcao.Pendente, EstadoDaAprovacaoDeAcao.Aprovada)})")
            .HasDatabaseName("ix_aprovacoes_de_acao_em_aberto_do_dono");
    }

    private static string Estados(params EstadoDaAprovacaoDeAcao[] estados) =>
        string.Join(", ", estados.Select(e => ((int)e).ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static string AcoesProtegidas() => string.Join(", ", CatalogoDeAcoes.Protegidas.Select(a =>
        $"('{a.Nome}', {((int)a.Risco).ToString(System.Globalization.CultureInfo.InvariantCulture)})"));
}
