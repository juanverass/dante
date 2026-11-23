using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Orquestracao.Migrations
{
    /// <inheritdoc />
    public partial class AprovacoesDeAcao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "aprovacoes_de_acao",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_execucao = table.Column<Guid>(type: "uuid", nullable: false),
                    id_execucao_raiz = table.Column<Guid>(type: "uuid", nullable: false),
                    id_etapa = table.Column<Guid>(type: "uuid", nullable: true),
                    acao = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    risco = table.Column<int>(type: "integer", nullable: false),
                    escopo = table.Column<int>(type: "integer", nullable: false),
                    alvo = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    id_versao_do_solicitante = table.Column<Guid>(type: "uuid", nullable: true),
                    hash_da_versao_do_solicitante = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    canal_do_dono = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    id_do_dono = table.Column<long>(type: "bigint", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    estado = table.Column<int>(type: "integer", nullable: false),
                    criada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expira_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    canal_do_decisor = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    id_do_decisor = table.Column<long>(type: "bigint", nullable: true),
                    decidida_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    motivo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    iniciada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    encerrada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resultado = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_aprovacoes_de_acao", x => x.id);
                    table.CheckConstraint("ck_aprovacoes_de_acao_acao", "(acao, risco) IN (('dante.entregar_artefato_do_roteiro', 1))");
                    table.CheckConstraint("ck_aprovacoes_de_acao_decisao", "(canal_do_decisor IS NULL) = (id_do_decisor IS NULL) AND (id_do_decisor IS NULL) = (decidida_em IS NULL) AND (estado <> 0 OR decidida_em IS NULL) AND (estado NOT IN (1, 2, 5, 6, 7, 8) OR decidida_em IS NOT NULL)");
                    table.CheckConstraint("ck_aprovacoes_de_acao_encerramento", "(encerrada_em IS NOT NULL) = (estado IN (2, 3, 4, 6, 7, 8))");
                    table.CheckConstraint("ck_aprovacoes_de_acao_escopo", "escopo = 0");
                    table.CheckConstraint("ck_aprovacoes_de_acao_estado", "estado BETWEEN 0 AND 8");
                    table.CheckConstraint("ck_aprovacoes_de_acao_execucao", "(iniciada_em IS NOT NULL) = (estado IN (5, 6, 7, 8))");
                    table.CheckConstraint("ck_aprovacoes_de_acao_hash", "hash ~ '^[0-9a-f]{64}$' AND hash = encode(sha256(convert_to(payload, 'UTF8')), 'hex')");
                    table.CheckConstraint("ck_aprovacoes_de_acao_payload", "payload::jsonb ->> 'acao' = acao AND payload::jsonb ->> 'execucao' = id_execucao::text AND (payload::jsonb ->> 'etapa') IS NOT DISTINCT FROM id_etapa::text AND (payload::jsonb ->> 'alvo') IS NOT DISTINCT FROM alvo AND payload::jsonb ->> 'escopo' = 'UmaVez' AND payload::jsonb ->> 'formato' = '1' AND (payload::jsonb -> 'solicitante' ->> 'versao') IS NOT DISTINCT FROM id_versao_do_solicitante::text AND (payload::jsonb -> 'solicitante' ->> 'hash') IS NOT DISTINCT FROM hash_da_versao_do_solicitante::text AND (id_versao_do_solicitante IS NOT NULL OR (payload::jsonb -> 'solicitante' ->> 'tipo' = 'dono' AND payload::jsonb -> 'solicitante' ->> 'canal' = canal_do_dono AND payload::jsonb -> 'solicitante' ->> 'usuario' = id_do_dono::text))");
                    table.CheckConstraint("ck_aprovacoes_de_acao_solicitante", "(id_versao_do_solicitante IS NULL) = (hash_da_versao_do_solicitante IS NULL) AND (hash_da_versao_do_solicitante IS NULL OR hash_da_versao_do_solicitante ~ '^[0-9a-f]{64}$')");
                    table.CheckConstraint("ck_aprovacoes_de_acao_validade", "expira_em > criada_em");
                    table.ForeignKey(
                        name: "FK_aprovacoes_de_acao_etapas_da_execucao_id_etapa_id_execucao",
                        columns: x => new { x.id_etapa, x.id_execucao },
                        principalSchema: "orquestracao",
                        principalTable: "etapas_da_execucao",
                        principalColumns: new[] { "id", "id_execucao" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_aprovacoes_de_acao_execucoes_id_execucao",
                        column: x => x.id_execucao,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_aprovacoes_de_acao_execucoes_id_execucao_raiz",
                        column: x => x.id_execucao_raiz,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_aprovacoes_de_acao_versoes_de_agente_id_versao_do_solicitan~",
                        column: x => x.id_versao_do_solicitante,
                        principalSchema: "orquestracao",
                        principalTable: "versoes_de_agente",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_aprovacoes_de_acao_em_aberto_do_dono",
                schema: "orquestracao",
                table: "aprovacoes_de_acao",
                columns: new[] { "canal_do_dono", "id_do_dono" },
                filter: "estado IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_aprovacoes_de_acao_id_etapa_id_execucao",
                schema: "orquestracao",
                table: "aprovacoes_de_acao",
                columns: new[] { "id_etapa", "id_execucao" });

            migrationBuilder.CreateIndex(
                name: "IX_aprovacoes_de_acao_id_execucao",
                schema: "orquestracao",
                table: "aprovacoes_de_acao",
                column: "id_execucao");

            migrationBuilder.CreateIndex(
                name: "IX_aprovacoes_de_acao_id_execucao_raiz",
                schema: "orquestracao",
                table: "aprovacoes_de_acao",
                column: "id_execucao_raiz");

            migrationBuilder.CreateIndex(
                name: "IX_aprovacoes_de_acao_id_versao_do_solicitante",
                schema: "orquestracao",
                table: "aprovacoes_de_acao",
                column: "id_versao_do_solicitante");

            migrationBuilder.CreateIndex(
                name: "ux_aprovacoes_de_acao_hash_vigente",
                schema: "orquestracao",
                table: "aprovacoes_de_acao",
                column: "hash",
                unique: true,
                filter: "estado IN (0, 1, 5, 6, 8)");

            // Gateway (AD-72, #311): além do domínio, o banco confere o dono da raiz na inserção, as transições
            // (Pendente 0, Aprovada 1, Recusada 2, Expirada 3, Cancelada 4, EmExecucao 5, Executada 6, Falhou 7,
            // Incerto 8), o decisor (o dono), os prazos e a imutabilidade do pedido, da decisão e do início da execução.
            // Todo UPDATE é uma transição: sem ela (inclusive num estado terminal), nada muda.
            migrationBuilder.Sql("""
                CREATE FUNCTION orquestracao.proteger_aprovacao_de_acao() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    raiz orquestracao.execucoes%ROWTYPE;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'aprovações de ação não são removidas' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.estado <> 0 THEN
                            RAISE EXCEPTION 'uma aprovação de ação nasce pendente' USING ERRCODE = 'restrict_violation';
                        END IF;
                        SELECT r.* INTO raiz
                        FROM orquestracao.execucoes e
                        JOIN orquestracao.execucoes r ON r.id = e.id_execucao_raiz
                        WHERE e.id = NEW.id_execucao;
                        IF NOT FOUND OR raiz.id <> NEW.id_execucao_raiz OR raiz.canal <> NEW.canal_do_dono
                           OR raiz.id_do_usuario_no_canal <> NEW.id_do_dono THEN
                            RAISE EXCEPTION 'a aprovação de ação é do dono da execução raiz' USING ERRCODE = 'restrict_violation';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF (NEW.id_execucao, NEW.id_execucao_raiz, NEW.id_etapa, NEW.acao, NEW.risco, NEW.escopo, NEW.alvo,
                        NEW.id_versao_do_solicitante, NEW.hash_da_versao_do_solicitante, NEW.canal_do_dono, NEW.id_do_dono,
                        NEW.payload, NEW.hash, NEW.criada_em, NEW.expira_em)
                       IS DISTINCT FROM
                       (OLD.id_execucao, OLD.id_execucao_raiz, OLD.id_etapa, OLD.acao, OLD.risco, OLD.escopo, OLD.alvo,
                        OLD.id_versao_do_solicitante, OLD.hash_da_versao_do_solicitante, OLD.canal_do_dono, OLD.id_do_dono,
                        OLD.payload, OLD.hash, OLD.criada_em, OLD.expira_em) THEN
                        RAISE EXCEPTION 'o pedido de uma ação é imutável' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.decidida_em IS NOT NULL AND (NEW.canal_do_decisor, NEW.id_do_decisor, NEW.decidida_em)
                       IS DISTINCT FROM (OLD.canal_do_decisor, OLD.id_do_decisor, OLD.decidida_em) THEN
                        RAISE EXCEPTION 'a decisão de uma ação é imutável' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.iniciada_em IS NOT NULL AND NEW.iniciada_em IS DISTINCT FROM OLD.iniciada_em THEN
                        RAISE EXCEPTION 'o início da execução de uma ação é imutável' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF (OLD.estado, NEW.estado) NOT IN ((0, 1), (0, 2), (0, 3), (0, 4), (1, 5), (1, 3), (1, 4), (5, 6), (5, 7), (5, 8)) THEN
                        RAISE EXCEPTION 'transição inválida de aprovação de ação: % → %', OLD.estado, NEW.estado
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.estado IN (1, 2) AND (NEW.canal_do_decisor <> OLD.canal_do_dono OR NEW.id_do_decisor <> OLD.id_do_dono) THEN
                        RAISE EXCEPTION 'só o dono da execução raiz decide a ação' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.estado = 1 AND NEW.decidida_em >= OLD.expira_em THEN
                        RAISE EXCEPTION 'aprovação depois do prazo' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.estado = 5 AND NEW.iniciada_em >= OLD.expira_em THEN
                        RAISE EXCEPTION 'execução depois do prazo da aprovação' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.estado = 3 AND NEW.encerrada_em < OLD.expira_em THEN
                        RAISE EXCEPTION 'aprovação expirada antes do prazo' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_aprovacoes_de_acao_protegidas BEFORE INSERT OR UPDATE OR DELETE ON orquestracao.aprovacoes_de_acao
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.proteger_aprovacao_de_acao();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_aprovacoes_de_acao_protegidas ON orquestracao.aprovacoes_de_acao;
                DROP FUNCTION IF EXISTS orquestracao.proteger_aprovacao_de_acao();
                """);

            migrationBuilder.DropTable(
                name: "aprovacoes_de_acao",
                schema: "orquestracao");
        }
    }
}
