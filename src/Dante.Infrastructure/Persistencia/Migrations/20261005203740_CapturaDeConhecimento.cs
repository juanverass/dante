using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class CapturaDeConhecimento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "candidatos_de_conhecimento",
                schema: "brain_data",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_espaco_de_conhecimento = table.Column<Guid>(type: "uuid", nullable: false),
                    id_projeto = table.Column<Guid>(type: "uuid", nullable: true),
                    tipo = table.Column<int>(type: "integer", nullable: false),
                    conteudo = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: false),
                    sensibilidade = table.Column<int>(type: "integer", nullable: false),
                    natureza = table.Column<int>(type: "integer", nullable: false),
                    modo = table.Column<int>(type: "integer", nullable: false),
                    justificativa = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    estado = table.Column<int>(type: "integer", nullable: false),
                    impressao = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    id_conhecimento = table.Column<Guid>(type: "uuid", nullable: true),
                    id_incidente = table.Column<Guid>(type: "uuid", nullable: true),
                    id_solucao = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    historico = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidatos_de_conhecimento", x => x.id);
                    table.ForeignKey(
                        name: "FK_candidatos_de_conhecimento_conhecimentos_id_conhecimento_id~",
                        columns: x => new { x.id_conhecimento, x.id_espaco_de_conhecimento },
                        principalSchema: "brain_data",
                        principalTable: "conhecimentos",
                        principalColumns: new[] { "id", "id_espaco_de_conhecimento" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_candidatos_de_conhecimento_conhecimentos_id_incidente_id_es~",
                        columns: x => new { x.id_incidente, x.id_espaco_de_conhecimento },
                        principalSchema: "brain_data",
                        principalTable: "conhecimentos",
                        principalColumns: new[] { "id", "id_espaco_de_conhecimento" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_candidatos_de_conhecimento_conhecimentos_id_solucao_id_espa~",
                        columns: x => new { x.id_solucao, x.id_espaco_de_conhecimento },
                        principalSchema: "brain_data",
                        principalTable: "conhecimentos",
                        principalColumns: new[] { "id", "id_espaco_de_conhecimento" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_candidatos_de_conhecimento_espacos_de_conhecimento_id_espac~",
                        column: x => x.id_espaco_de_conhecimento,
                        principalSchema: "brain_data",
                        principalTable: "espacos_de_conhecimento",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_candidatos_de_conhecimento_projetos_id_projeto_id_espaco_de~",
                        columns: x => new { x.id_projeto, x.id_espaco_de_conhecimento },
                        principalSchema: "brain_data",
                        principalTable: "projetos",
                        principalColumns: new[] { "id", "id_espaco_de_conhecimento" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_candidatos_de_conhecimento_id_conhecimento_id_espaco_de_con~",
                schema: "brain_data",
                table: "candidatos_de_conhecimento",
                columns: new[] { "id_conhecimento", "id_espaco_de_conhecimento" });

            migrationBuilder.CreateIndex(
                name: "IX_candidatos_de_conhecimento_id_espaco_de_conhecimento_id_pr~1",
                schema: "brain_data",
                table: "candidatos_de_conhecimento",
                columns: new[] { "id_espaco_de_conhecimento", "id_projeto", "impressao" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_candidatos_de_conhecimento_id_espaco_de_conhecimento_id_pro~",
                schema: "brain_data",
                table: "candidatos_de_conhecimento",
                columns: new[] { "id_espaco_de_conhecimento", "id_projeto", "estado" });

            migrationBuilder.CreateIndex(
                name: "IX_candidatos_de_conhecimento_id_incidente_id_espaco_de_conhec~",
                schema: "brain_data",
                table: "candidatos_de_conhecimento",
                columns: new[] { "id_incidente", "id_espaco_de_conhecimento" });

            migrationBuilder.CreateIndex(
                name: "IX_candidatos_de_conhecimento_id_projeto_id_espaco_de_conhecim~",
                schema: "brain_data",
                table: "candidatos_de_conhecimento",
                columns: new[] { "id_projeto", "id_espaco_de_conhecimento" });

            migrationBuilder.CreateIndex(
                name: "IX_candidatos_de_conhecimento_id_solucao_id_espaco_de_conhecim~",
                schema: "brain_data",
                table: "candidatos_de_conhecimento",
                columns: new[] { "id_solucao", "id_espaco_de_conhecimento" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "candidatos_de_conhecimento",
                schema: "brain_data");
        }
    }
}
