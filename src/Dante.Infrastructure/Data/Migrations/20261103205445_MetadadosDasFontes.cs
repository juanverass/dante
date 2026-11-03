using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MetadadosDasFontes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #257: metadados de registro de evidências. Fontes existentes ficam com valores neutros (null/vazio),
            // sem título, tags, justificativa ou proveniência inventados.
            migrationBuilder.AddColumn<string>(
                name: "justificativa",
                schema: "brain_data",
                table: "documentos_fonte",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "proveniencia",
                schema: "brain_data",
                table: "documentos_fonte",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "tags",
                schema: "brain_data",
                table: "documentos_fonte",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "titulo",
                schema: "brain_data",
                table: "documentos_fonte",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_documentos_fonte_id_espaco_de_conhecimento_id_projeto_hash",
                schema: "brain_data",
                table: "documentos_fonte",
                columns: new[] { "id_espaco_de_conhecimento", "id_projeto", "hash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_documentos_fonte_id_espaco_de_conhecimento_id_projeto_hash",
                schema: "brain_data",
                table: "documentos_fonte");

            migrationBuilder.DropColumn(
                name: "justificativa",
                schema: "brain_data",
                table: "documentos_fonte");

            migrationBuilder.DropColumn(
                name: "proveniencia",
                schema: "brain_data",
                table: "documentos_fonte");

            migrationBuilder.DropColumn(
                name: "tags",
                schema: "brain_data",
                table: "documentos_fonte");

            migrationBuilder.DropColumn(
                name: "titulo",
                schema: "brain_data",
                table: "documentos_fonte");
        }
    }
}
