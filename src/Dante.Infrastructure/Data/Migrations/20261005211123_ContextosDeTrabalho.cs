using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ContextosDeTrabalho : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contextos_de_trabalho",
                schema: "brain_data",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_espaco_de_conhecimento = table.Column<Guid>(type: "uuid", nullable: false),
                    id_projeto = table.Column<Guid>(type: "uuid", nullable: true),
                    dados = table.Column<string>(type: "jsonb", nullable: false),
                    sensibilidade = table.Column<int>(type: "integer", nullable: false),
                    revisao = table.Column<int>(type: "integer", nullable: false),
                    id_responsavel = table.Column<Guid>(type: "uuid", nullable: false),
                    origem = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    atualizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expira_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    auditoria_anterior = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contextos_de_trabalho", x => x.id);
                    table.ForeignKey(
                        name: "FK_contextos_de_trabalho_espacos_de_conhecimento_id_espaco_de_~",
                        column: x => x.id_espaco_de_conhecimento,
                        principalSchema: "brain_data",
                        principalTable: "espacos_de_conhecimento",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_contextos_de_trabalho_projetos_id_projeto_id_espaco_de_conh~",
                        columns: x => new { x.id_projeto, x.id_espaco_de_conhecimento },
                        principalSchema: "brain_data",
                        principalTable: "projetos",
                        principalColumns: new[] { "id", "id_espaco_de_conhecimento" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_contextos_de_trabalho_id_espaco_de_conhecimento_id_projeto",
                schema: "brain_data",
                table: "contextos_de_trabalho",
                columns: new[] { "id_espaco_de_conhecimento", "id_projeto" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_contextos_de_trabalho_id_projeto_id_espaco_de_conhecimento",
                schema: "brain_data",
                table: "contextos_de_trabalho",
                columns: new[] { "id_projeto", "id_espaco_de_conhecimento" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contextos_de_trabalho",
                schema: "brain_data");
        }
    }
}
