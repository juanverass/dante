using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Orquestracao.Migrations
{
    /// <inheritdoc />
    public partial class VereditosDeRevisao : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vereditos_de_revisao",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_execucao = table.Column<Guid>(type: "uuid", nullable: false),
                    id_etapa = table.Column<Guid>(type: "uuid", nullable: false),
                    rodada = table.Column<int>(type: "integer", nullable: false),
                    hash_do_artefato = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    veredito = table.Column<int>(type: "integer", nullable: false),
                    resumo = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    itens = table.Column<string[]>(type: "text[]", nullable: false),
                    sessao = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    hash_do_conteudo = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    registrado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vereditos_de_revisao", x => x.id);
                    table.CheckConstraint("ck_vereditos_de_revisao_hashes", "hash_do_artefato ~ '^[0-9a-f]{64}$' AND hash_do_conteudo ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_vereditos_de_revisao_rodada", "rodada >= 0");
                    table.ForeignKey(
                        name: "FK_vereditos_de_revisao_etapas_da_execucao_id_etapa",
                        column: x => x.id_etapa,
                        principalSchema: "orquestracao",
                        principalTable: "etapas_da_execucao",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_vereditos_de_revisao_execucoes_id_execucao",
                        column: x => x.id_execucao,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_vereditos_de_revisao_id_etapa",
                schema: "orquestracao",
                table: "vereditos_de_revisao",
                column: "id_etapa",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vereditos_de_revisao_id_execucao",
                schema: "orquestracao",
                table: "vereditos_de_revisao",
                column: "id_execucao");

            // Um veredito por etapa e imutável (#308, decisão D).
            migrationBuilder.Sql("""
                CREATE FUNCTION orquestracao.recusar_alteracao_de_veredito() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'vereditos de revisão são imutáveis' USING ERRCODE = 'restrict_violation';
                END $$;
                CREATE TRIGGER trg_vereditos_de_revisao_imutaveis BEFORE UPDATE OR DELETE ON orquestracao.vereditos_de_revisao
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.recusar_alteracao_de_veredito();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_vereditos_de_revisao_imutaveis ON orquestracao.vereditos_de_revisao;
                DROP FUNCTION IF EXISTS orquestracao.recusar_alteracao_de_veredito();
                """);

            migrationBuilder.DropTable(
                name: "vereditos_de_revisao",
                schema: "orquestracao");
        }
    }
}
