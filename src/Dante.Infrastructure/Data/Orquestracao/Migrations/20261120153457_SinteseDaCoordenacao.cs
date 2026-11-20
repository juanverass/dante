using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Orquestracao.Migrations
{
    /// <inheritdoc />
    public partial class SinteseDaCoordenacao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sinteses_da_coordenacao",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_execucao = table.Column<Guid>(type: "uuid", nullable: false),
                    id_etapa = table.Column<Guid>(type: "uuid", nullable: false),
                    id_sessao_de_agente = table.Column<Guid>(type: "uuid", nullable: false),
                    conteudo = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    registrada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sinteses_da_coordenacao", x => x.id);
                    table.CheckConstraint("ck_sinteses_da_coordenacao_hash", "hash ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_sinteses_da_coordenacao_etapas_da_execucao_id_etapa_id_exec~",
                        columns: x => new { x.id_etapa, x.id_execucao },
                        principalSchema: "orquestracao",
                        principalTable: "etapas_da_execucao",
                        principalColumns: new[] { "id", "id_execucao" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sinteses_da_coordenacao_execucoes_id_execucao",
                        column: x => x.id_execucao,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sinteses_da_coordenacao_sessoes_de_agente_id_sessao_de_agen~",
                        column: x => x.id_sessao_de_agente,
                        principalSchema: "orquestracao",
                        principalTable: "sessoes_de_agente",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sinteses_da_coordenacao_id_etapa",
                schema: "orquestracao",
                table: "sinteses_da_coordenacao",
                column: "id_etapa",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sinteses_da_coordenacao_id_etapa_id_execucao",
                schema: "orquestracao",
                table: "sinteses_da_coordenacao",
                columns: new[] { "id_etapa", "id_execucao" });

            migrationBuilder.CreateIndex(
                name: "IX_sinteses_da_coordenacao_id_execucao",
                schema: "orquestracao",
                table: "sinteses_da_coordenacao",
                column: "id_execucao",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sinteses_da_coordenacao_id_sessao_de_agente",
                schema: "orquestracao",
                table: "sinteses_da_coordenacao",
                column: "id_sessao_de_agente");

            // Síntese do coordenador (AD-75, D7): só numa Coordenada, pela etapa de síntese dela (turno do coordenador
            // congelado no snapshot, fora de plano), com a sessão vinculada à etapa e o hash dos bytes do conteúdo.
            // Depois de gravada, nunca muda nem é removida.
            migrationBuilder.Sql("""
                CREATE FUNCTION orquestracao.proteger_sintese_da_coordenacao() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP <> 'INSERT' THEN
                        RAISE EXCEPTION 'a síntese da coordenação é imutável e não é removida' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.hash <> encode(sha256(convert_to(NEW.conteudo, 'UTF8')), 'hex') THEN
                        RAISE EXCEPTION 'o hash da síntese não é o do conteúdo' USING ERRCODE = 'check_violation';
                    END IF;
                    IF NOT EXISTS (
                           SELECT 1 FROM orquestracao.etapas_da_execucao et
                           JOIN orquestracao.execucoes e ON e.id = et.id_execucao AND e.tipo = 4
                           JOIN orquestracao.coordenacoes_da_execucao c ON c.id_execucao = e.id
                           JOIN orquestracao.participantes_da_execucao pe
                             ON pe.id_execucao = e.id AND pe.papel = c.papel_do_coordenador
                           WHERE et.id = NEW.id_etapa AND et.id_execucao = NEW.id_execucao AND et.tipo = 1
                             AND et.id_plano IS NULL AND et.papel = pe.papel
                             AND et.id_versao_do_agente = pe.id_versao_do_agente
                             AND et.id_sessao_de_agente = NEW.id_sessao_de_agente) THEN
                        RAISE EXCEPTION 'síntese só pela etapa de síntese do coordenador congelado, com a sessão vinculada'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_sinteses_da_coordenacao_protegidas BEFORE INSERT OR UPDATE OR DELETE
                    ON orquestracao.sinteses_da_coordenacao
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.proteger_sintese_da_coordenacao();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_sinteses_da_coordenacao_protegidas ON orquestracao.sinteses_da_coordenacao;
                DROP FUNCTION IF EXISTS orquestracao.proteger_sintese_da_coordenacao();
                """);

            migrationBuilder.DropTable(
                name: "sinteses_da_coordenacao",
                schema: "orquestracao");
        }
    }
}
