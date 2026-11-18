using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Orquestracao.Migrations
{
    /// <inheritdoc />
    public partial class GateDoRoteiro : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_reservas_de_worktree_alias_ativa",
                schema: "orquestracao",
                table: "reservas_de_worktree");

            migrationBuilder.DropIndex(
                name: "ux_reservas_de_worktree_branch_ativa",
                schema: "orquestracao",
                table: "reservas_de_worktree");

            migrationBuilder.DropIndex(
                name: "ux_reservas_de_worktree_caminho_ativa",
                schema: "orquestracao",
                table: "reservas_de_worktree");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reservas_de_worktree_estado",
                schema: "orquestracao",
                table: "reservas_de_worktree");

            migrationBuilder.AddColumn<Guid>(
                name: "id_versao_do_grupo",
                schema: "orquestracao",
                table: "execucoes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "id_versao_do_agente",
                schema: "orquestracao",
                table: "etapas_da_execucao",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "papel",
                schema: "orquestracao",
                table: "etapas_da_execucao",
                type: "character varying(31)",
                maxLength: 31,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "passo",
                schema: "orquestracao",
                table: "etapas_da_execucao",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "rodada",
                schema: "orquestracao",
                table: "etapas_da_execucao",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "participantes_da_execucao",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_execucao = table.Column<Guid>(type: "uuid", nullable: false),
                    papel = table.Column<string>(type: "character varying(31)", maxLength: 31, nullable: false),
                    handle_do_agente = table.Column<string>(type: "character varying(31)", maxLength: 31, nullable: false),
                    id_versao_do_agente = table.Column<Guid>(type: "uuid", nullable: false),
                    numero_da_versao_do_agente = table.Column<int>(type: "integer", nullable: false),
                    hash_da_configuracao_do_agente = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    pode_escrever = table.Column<bool>(type: "boolean", nullable: false),
                    responsabilidades = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_participantes_da_execucao", x => x.id);
                    table.CheckConstraint("ck_participantes_da_execucao_hash", "hash_da_configuracao_do_agente ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_participantes_da_execucao_versao", "numero_da_versao_do_agente > 0");
                    table.ForeignKey(
                        name: "FK_participantes_da_execucao_execucoes_id_execucao",
                        column: x => x.id_execucao,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_participantes_da_execucao_versoes_de_agente_id_versao_do_ag~",
                        column: x => x.id_versao_do_agente,
                        principalSchema: "orquestracao",
                        principalTable: "versoes_de_agente",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pedidos_de_aprovacao",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_execucao = table.Column<Guid>(type: "uuid", nullable: false),
                    id_etapa = table.Column<Guid>(type: "uuid", nullable: false),
                    rodada = table.Column<int>(type: "integer", nullable: false),
                    hash_do_snapshot = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    hash_do_artefato = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    estado = table.Column<int>(type: "integer", nullable: false),
                    canal_do_decisor = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    id_do_decisor = table.Column<long>(type: "bigint", nullable: true),
                    decidido_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    motivo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pedidos_de_aprovacao", x => x.id);
                    table.CheckConstraint("ck_pedidos_de_aprovacao_decisao", "(estado = 0) = (decidido_em IS NULL) AND (estado IN (0, 4)) = (id_do_decisor IS NULL)");
                    table.CheckConstraint("ck_pedidos_de_aprovacao_hashes", "hash_do_snapshot ~ '^[0-9a-f]{64}$' AND hash_do_artefato ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_pedidos_de_aprovacao_etapas_da_execucao_id_etapa",
                        column: x => x.id_etapa,
                        principalSchema: "orquestracao",
                        principalTable: "etapas_da_execucao",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pedidos_de_aprovacao_execucoes_id_execucao",
                        column: x => x.id_execucao,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "roteiros_da_execucao",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_execucao = table.Column<Guid>(type: "uuid", nullable: false),
                    id_grupo = table.Column<Guid>(type: "uuid", nullable: false),
                    nome_do_grupo = table.Column<string>(type: "character varying(31)", maxLength: 31, nullable: false),
                    id_versao_do_grupo = table.Column<Guid>(type: "uuid", nullable: false),
                    numero_da_versao_do_grupo = table.Column<int>(type: "integer", nullable: false),
                    hash_da_versao_do_grupo = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    passos = table.Column<string[]>(type: "text[]", nullable: false),
                    rodadas_de_revisao_maximas = table.Column<int>(type: "integer", nullable: false),
                    hash_do_snapshot = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    objetivo_redigido = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    objetivo_alterado_pela_redacao = table.Column<bool>(type: "boolean", nullable: false),
                    hash_do_objetivo_original = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roteiros_da_execucao", x => x.id);
                    table.CheckConstraint("ck_roteiros_da_execucao_hashes", "hash_da_versao_do_grupo ~ '^[0-9a-f]{64}$' AND hash_do_snapshot ~ '^[0-9a-f]{64}$' AND hash_do_objetivo_original ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_roteiros_da_execucao_rodadas", "rodadas_de_revisao_maximas >= 0");
                    table.ForeignKey(
                        name: "FK_roteiros_da_execucao_execucoes_id_execucao",
                        column: x => x.id_execucao,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_roteiros_da_execucao_grupos_de_agentes_id_grupo",
                        column: x => x.id_grupo,
                        principalSchema: "orquestracao",
                        principalTable: "grupos_de_agentes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_roteiros_da_execucao_versoes_de_grupo_id_versao_do_grupo",
                        column: x => x.id_versao_do_grupo,
                        principalSchema: "orquestracao",
                        principalTable: "versoes_de_grupo",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_reservas_de_worktree_alias_ativa",
                schema: "orquestracao",
                table: "reservas_de_worktree",
                columns: new[] { "id_execucao", "alias" },
                unique: true,
                filter: "estado IN (0, 1, 6)");

            migrationBuilder.CreateIndex(
                name: "ux_reservas_de_worktree_branch_ativa",
                schema: "orquestracao",
                table: "reservas_de_worktree",
                columns: new[] { "caminho_do_repositorio", "branch" },
                unique: true,
                filter: "estado IN (0, 1, 6)");

            migrationBuilder.CreateIndex(
                name: "ux_reservas_de_worktree_caminho_ativa",
                schema: "orquestracao",
                table: "reservas_de_worktree",
                column: "caminho",
                unique: true,
                filter: "estado IN (0, 1, 6)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reservas_de_worktree_estado",
                schema: "orquestracao",
                table: "reservas_de_worktree",
                sql: "estado BETWEEN 0 AND 6");

            migrationBuilder.CreateIndex(
                name: "IX_execucoes_id_versao_do_grupo",
                schema: "orquestracao",
                table: "execucoes",
                column: "id_versao_do_grupo");

            migrationBuilder.AddCheckConstraint(
                name: "ck_execucoes_roteiro_com_grupo",
                schema: "orquestracao",
                table: "execucoes",
                sql: "(tipo = 2) = (id_versao_do_grupo IS NOT NULL) AND (tipo <> 2 OR id_versao_do_agente IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_etapas_da_execucao_id_versao_do_agente",
                schema: "orquestracao",
                table: "etapas_da_execucao",
                column: "id_versao_do_agente");

            migrationBuilder.AddCheckConstraint(
                name: "ck_etapas_da_execucao_passo_do_roteiro",
                schema: "orquestracao",
                table: "etapas_da_execucao",
                sql: "(passo IS NULL) = (rodada IS NULL) AND (rodada IS NULL OR rodada >= 0) AND (papel IS NULL) = (id_versao_do_agente IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_participantes_da_execucao_id_execucao_papel",
                schema: "orquestracao",
                table: "participantes_da_execucao",
                columns: new[] { "id_execucao", "papel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_participantes_da_execucao_id_versao_do_agente",
                schema: "orquestracao",
                table: "participantes_da_execucao",
                column: "id_versao_do_agente");

            migrationBuilder.CreateIndex(
                name: "IX_pedidos_de_aprovacao_id_etapa",
                schema: "orquestracao",
                table: "pedidos_de_aprovacao",
                column: "id_etapa",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_pedidos_de_aprovacao_pendente_por_execucao",
                schema: "orquestracao",
                table: "pedidos_de_aprovacao",
                column: "id_execucao",
                unique: true,
                filter: "estado = 0");

            migrationBuilder.CreateIndex(
                name: "IX_roteiros_da_execucao_id_execucao",
                schema: "orquestracao",
                table: "roteiros_da_execucao",
                column: "id_execucao",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_roteiros_da_execucao_id_grupo",
                schema: "orquestracao",
                table: "roteiros_da_execucao",
                column: "id_grupo");

            migrationBuilder.CreateIndex(
                name: "IX_roteiros_da_execucao_id_versao_do_grupo",
                schema: "orquestracao",
                table: "roteiros_da_execucao",
                column: "id_versao_do_grupo");

            migrationBuilder.AddForeignKey(
                name: "FK_etapas_da_execucao_versoes_de_agente_id_versao_do_agente",
                schema: "orquestracao",
                table: "etapas_da_execucao",
                column: "id_versao_do_agente",
                principalSchema: "orquestracao",
                principalTable: "versoes_de_agente",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_execucoes_versoes_de_grupo_id_versao_do_grupo",
                schema: "orquestracao",
                table: "execucoes",
                column: "id_versao_do_grupo",
                principalSchema: "orquestracao",
                principalTable: "versoes_de_grupo",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Snapshot, participantes e decisões imutáveis no banco, além do domínio (#308).
            migrationBuilder.Sql("""
                CREATE FUNCTION orquestracao.recusar_alteracao_de_participante_da_execucao() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'participantes da execução são imutáveis' USING ERRCODE = 'restrict_violation';
                END $$;
                CREATE TRIGGER trg_participantes_da_execucao_imutaveis BEFORE UPDATE OR DELETE ON orquestracao.participantes_da_execucao
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.recusar_alteracao_de_participante_da_execucao();

                -- O snapshot nunca muda; só o objetivo alterado pela redação pode ser substituído por um utilizável.
                CREATE FUNCTION orquestracao.proteger_roteiro_da_execucao() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'roteiros não são removidos' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF (NEW.id_execucao, NEW.id_grupo, NEW.nome_do_grupo, NEW.id_versao_do_grupo, NEW.numero_da_versao_do_grupo,
                        NEW.hash_da_versao_do_grupo, NEW.passos, NEW.rodadas_de_revisao_maximas, NEW.hash_do_snapshot)
                       IS DISTINCT FROM
                       (OLD.id_execucao, OLD.id_grupo, OLD.nome_do_grupo, OLD.id_versao_do_grupo, OLD.numero_da_versao_do_grupo,
                        OLD.hash_da_versao_do_grupo, OLD.passos, OLD.rodadas_de_revisao_maximas, OLD.hash_do_snapshot) THEN
                        RAISE EXCEPTION 'o snapshot do roteiro é imutável' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF (NEW.objetivo_redigido, NEW.objetivo_alterado_pela_redacao, NEW.hash_do_objetivo_original)
                       IS DISTINCT FROM (OLD.objetivo_redigido, OLD.objetivo_alterado_pela_redacao, OLD.hash_do_objetivo_original)
                       AND (NOT OLD.objetivo_alterado_pela_redacao OR NEW.objetivo_alterado_pela_redacao) THEN
                        RAISE EXCEPTION 'só um objetivo protegido pela redação é substituído, e por um utilizável'
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_roteiros_da_execucao_snapshot BEFORE UPDATE OR DELETE ON orquestracao.roteiros_da_execucao
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.proteger_roteiro_da_execucao();

                -- Uma decisão só: pedido decidido não muda, e nenhum pedido troca de execução, etapa ou hashes.
                CREATE FUNCTION orquestracao.proteger_pedido_de_aprovacao() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'pedidos de aprovação não são removidos' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.estado <> 0 THEN
                        RAISE EXCEPTION 'pedido de aprovação já decidido' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF (NEW.id_execucao, NEW.id_etapa, NEW.rodada, NEW.hash_do_snapshot, NEW.hash_do_artefato, NEW.criado_em)
                       IS DISTINCT FROM (OLD.id_execucao, OLD.id_etapa, OLD.rodada, OLD.hash_do_snapshot, OLD.hash_do_artefato, OLD.criado_em) THEN
                        RAISE EXCEPTION 'o artefato de um pedido de aprovação é imutável' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_pedidos_de_aprovacao_decisao_unica BEFORE UPDATE OR DELETE ON orquestracao.pedidos_de_aprovacao
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.proteger_pedido_de_aprovacao();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_pedidos_de_aprovacao_decisao_unica ON orquestracao.pedidos_de_aprovacao;
                DROP FUNCTION IF EXISTS orquestracao.proteger_pedido_de_aprovacao();
                DROP TRIGGER IF EXISTS trg_roteiros_da_execucao_snapshot ON orquestracao.roteiros_da_execucao;
                DROP FUNCTION IF EXISTS orquestracao.proteger_roteiro_da_execucao();
                DROP TRIGGER IF EXISTS trg_participantes_da_execucao_imutaveis ON orquestracao.participantes_da_execucao;
                DROP FUNCTION IF EXISTS orquestracao.recusar_alteracao_de_participante_da_execucao();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_etapas_da_execucao_versoes_de_agente_id_versao_do_agente",
                schema: "orquestracao",
                table: "etapas_da_execucao");

            migrationBuilder.DropForeignKey(
                name: "FK_execucoes_versoes_de_grupo_id_versao_do_grupo",
                schema: "orquestracao",
                table: "execucoes");

            migrationBuilder.DropTable(
                name: "participantes_da_execucao",
                schema: "orquestracao");

            migrationBuilder.DropTable(
                name: "pedidos_de_aprovacao",
                schema: "orquestracao");

            migrationBuilder.DropTable(
                name: "roteiros_da_execucao",
                schema: "orquestracao");

            migrationBuilder.DropIndex(
                name: "ux_reservas_de_worktree_alias_ativa",
                schema: "orquestracao",
                table: "reservas_de_worktree");

            migrationBuilder.DropIndex(
                name: "ux_reservas_de_worktree_branch_ativa",
                schema: "orquestracao",
                table: "reservas_de_worktree");

            migrationBuilder.DropIndex(
                name: "ux_reservas_de_worktree_caminho_ativa",
                schema: "orquestracao",
                table: "reservas_de_worktree");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reservas_de_worktree_estado",
                schema: "orquestracao",
                table: "reservas_de_worktree");

            migrationBuilder.DropIndex(
                name: "IX_execucoes_id_versao_do_grupo",
                schema: "orquestracao",
                table: "execucoes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_execucoes_roteiro_com_grupo",
                schema: "orquestracao",
                table: "execucoes");

            migrationBuilder.DropIndex(
                name: "IX_etapas_da_execucao_id_versao_do_agente",
                schema: "orquestracao",
                table: "etapas_da_execucao");

            migrationBuilder.DropCheckConstraint(
                name: "ck_etapas_da_execucao_passo_do_roteiro",
                schema: "orquestracao",
                table: "etapas_da_execucao");

            migrationBuilder.DropColumn(
                name: "id_versao_do_grupo",
                schema: "orquestracao",
                table: "execucoes");

            migrationBuilder.DropColumn(
                name: "id_versao_do_agente",
                schema: "orquestracao",
                table: "etapas_da_execucao");

            migrationBuilder.DropColumn(
                name: "papel",
                schema: "orquestracao",
                table: "etapas_da_execucao");

            migrationBuilder.DropColumn(
                name: "passo",
                schema: "orquestracao",
                table: "etapas_da_execucao");

            migrationBuilder.DropColumn(
                name: "rodada",
                schema: "orquestracao",
                table: "etapas_da_execucao");

            migrationBuilder.CreateIndex(
                name: "ux_reservas_de_worktree_alias_ativa",
                schema: "orquestracao",
                table: "reservas_de_worktree",
                columns: new[] { "id_execucao", "alias" },
                unique: true,
                filter: "estado IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "ux_reservas_de_worktree_branch_ativa",
                schema: "orquestracao",
                table: "reservas_de_worktree",
                columns: new[] { "caminho_do_repositorio", "branch" },
                unique: true,
                filter: "estado IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "ux_reservas_de_worktree_caminho_ativa",
                schema: "orquestracao",
                table: "reservas_de_worktree",
                column: "caminho",
                unique: true,
                filter: "estado IN (0, 1)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reservas_de_worktree_estado",
                schema: "orquestracao",
                table: "reservas_de_worktree",
                sql: "estado BETWEEN 0 AND 5");
        }
    }
}
