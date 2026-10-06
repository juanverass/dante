using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ResolucaoDeConflitos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "id_conhecimento_escolhido",
                schema: "brain_data",
                table: "relacoes_de_conhecimento",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "proveniencia_da_resolucao",
                schema: "brain_data",
                table: "relacoes_de_conhecimento",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "resolvida_em",
                schema: "brain_data",
                table: "relacoes_de_conhecimento",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_relacoes_de_conhecimento_id_conhecimento_escolhido_id_espac~",
                schema: "brain_data",
                table: "relacoes_de_conhecimento",
                columns: new[] { "id_conhecimento_escolhido", "id_espaco_de_conhecimento" });

            migrationBuilder.AddForeignKey(
                name: "FK_relacoes_de_conhecimento_conhecimentos_id_conhecimento_esco~",
                schema: "brain_data",
                table: "relacoes_de_conhecimento",
                columns: new[] { "id_conhecimento_escolhido", "id_espaco_de_conhecimento" },
                principalSchema: "brain_data",
                principalTable: "conhecimentos",
                principalColumns: new[] { "id", "id_espaco_de_conhecimento" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_relacoes_de_conhecimento_conhecimentos_id_conhecimento_esco~",
                schema: "brain_data",
                table: "relacoes_de_conhecimento");

            migrationBuilder.DropIndex(
                name: "IX_relacoes_de_conhecimento_id_conhecimento_escolhido_id_espac~",
                schema: "brain_data",
                table: "relacoes_de_conhecimento");

            migrationBuilder.DropColumn(
                name: "id_conhecimento_escolhido",
                schema: "brain_data",
                table: "relacoes_de_conhecimento");

            migrationBuilder.DropColumn(
                name: "proveniencia_da_resolucao",
                schema: "brain_data",
                table: "relacoes_de_conhecimento");

            migrationBuilder.DropColumn(
                name: "resolvida_em",
                schema: "brain_data",
                table: "relacoes_de_conhecimento");
        }
    }
}
