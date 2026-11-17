using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Orquestracao.Migrations
{
    /// <inheritdoc />
    public partial class ReservasDeWorktree : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reservas_de_worktree",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_execucao = table.Column<Guid>(type: "uuid", nullable: false),
                    id_etapa = table.Column<Guid>(type: "uuid", nullable: false),
                    alias = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    principal = table.Column<bool>(type: "boolean", nullable: false),
                    caminho_do_repositorio = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    caminho = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    branch = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sha_base = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    estado = table.Column<int>(type: "integer", nullable: false),
                    maquina_do_worker = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    motivo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    criada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    pronta_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    liberada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    encerrada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reservas_de_worktree", x => x.id);
                    table.CheckConstraint("ck_reservas_de_worktree_estado", "estado BETWEEN 0 AND 5");
                    table.CheckConstraint("ck_reservas_de_worktree_fora_do_checkout", "caminho <> caminho_do_repositorio");
                    table.CheckConstraint("ck_reservas_de_worktree_sha", "char_length(sha_base) = 40");
                    table.ForeignKey(
                        name: "FK_reservas_de_worktree_etapas_da_execucao_id_etapa_id_execucao",
                        columns: x => new { x.id_etapa, x.id_execucao },
                        principalSchema: "orquestracao",
                        principalTable: "etapas_da_execucao",
                        principalColumns: new[] { "id", "id_execucao" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reservas_de_worktree_execucoes_id_execucao",
                        column: x => x.id_execucao,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reservas_de_worktree_id_etapa_id_execucao",
                schema: "orquestracao",
                table: "reservas_de_worktree",
                columns: new[] { "id_etapa", "id_execucao" });

            migrationBuilder.CreateIndex(
                name: "IX_reservas_de_worktree_maquina_do_worker_estado",
                schema: "orquestracao",
                table: "reservas_de_worktree",
                columns: new[] { "maquina_do_worker", "estado" });

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reservas_de_worktree",
                schema: "orquestracao");
        }
    }
}
