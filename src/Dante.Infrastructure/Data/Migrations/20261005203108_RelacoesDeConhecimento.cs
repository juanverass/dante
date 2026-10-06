using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RelacoesDeConhecimento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "relacoes_de_conhecimento",
                schema: "brain_data",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_espaco_de_conhecimento = table.Column<Guid>(type: "uuid", nullable: false),
                    id_projeto = table.Column<Guid>(type: "uuid", nullable: true),
                    id_origem = table.Column<Guid>(type: "uuid", nullable: false),
                    id_destino = table.Column<Guid>(type: "uuid", nullable: false),
                    tipo = table.Column<int>(type: "integer", nullable: false),
                    proveniencia = table.Column<string>(type: "jsonb", nullable: false),
                    criada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relacoes_de_conhecimento", x => x.id);
                    table.ForeignKey(
                        name: "FK_relacoes_de_conhecimento_conhecimentos_id_destino_id_espaco~",
                        columns: x => new { x.id_destino, x.id_espaco_de_conhecimento },
                        principalSchema: "brain_data",
                        principalTable: "conhecimentos",
                        principalColumns: new[] { "id", "id_espaco_de_conhecimento" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_relacoes_de_conhecimento_conhecimentos_id_origem_id_espaco_~",
                        columns: x => new { x.id_origem, x.id_espaco_de_conhecimento },
                        principalSchema: "brain_data",
                        principalTable: "conhecimentos",
                        principalColumns: new[] { "id", "id_espaco_de_conhecimento" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_relacoes_de_conhecimento_id_destino_id_espaco_de_conhecimen~",
                schema: "brain_data",
                table: "relacoes_de_conhecimento",
                columns: new[] { "id_destino", "id_espaco_de_conhecimento" });

            migrationBuilder.CreateIndex(
                name: "IX_relacoes_de_conhecimento_id_espaco_de_conhecimento_id_desti~",
                schema: "brain_data",
                table: "relacoes_de_conhecimento",
                columns: new[] { "id_espaco_de_conhecimento", "id_destino" });

            migrationBuilder.CreateIndex(
                name: "IX_relacoes_de_conhecimento_id_espaco_de_conhecimento_id_orige~",
                schema: "brain_data",
                table: "relacoes_de_conhecimento",
                columns: new[] { "id_espaco_de_conhecimento", "id_origem", "id_destino", "tipo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_relacoes_de_conhecimento_id_origem_id_espaco_de_conhecimento",
                schema: "brain_data",
                table: "relacoes_de_conhecimento",
                columns: new[] { "id_origem", "id_espaco_de_conhecimento" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "relacoes_de_conhecimento",
                schema: "brain_data");
        }
    }
}
