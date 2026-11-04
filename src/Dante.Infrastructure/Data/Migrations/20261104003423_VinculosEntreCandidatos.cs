using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class VinculosEntreCandidatos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #259: intenção de ResolvidoPor entre candidatos pendentes. Tabela nova, sem dados existentes a migrar;
            // relações e candidatos atuais não mudam. Reaplique os grants do guia (ON ALL TABLES) após migrar.
            migrationBuilder.CreateTable(
                name: "vinculos_entre_candidatos",
                schema: "brain_data",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_espaco_de_conhecimento = table.Column<Guid>(type: "uuid", nullable: false),
                    id_projeto = table.Column<Guid>(type: "uuid", nullable: true),
                    id_candidato_incidente = table.Column<Guid>(type: "uuid", nullable: false),
                    id_candidato_solucao = table.Column<Guid>(type: "uuid", nullable: false),
                    id_conhecimento_incidente = table.Column<Guid>(type: "uuid", nullable: true),
                    id_conhecimento_solucao = table.Column<Guid>(type: "uuid", nullable: true),
                    id_relacao = table.Column<Guid>(type: "uuid", nullable: true),
                    estado = table.Column<int>(type: "integer", nullable: false),
                    motivo_do_encerramento = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    proveniencia = table.Column<string>(type: "jsonb", nullable: false),
                    proveniencia_do_encerramento = table.Column<string>(type: "jsonb", nullable: true),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    atualizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vinculos_entre_candidatos", x => x.id);
                    table.ForeignKey(
                        name: "FK_vinculos_entre_candidatos_candidatos_de_conhecimento_id_can~",
                        column: x => x.id_candidato_incidente,
                        principalSchema: "brain_data",
                        principalTable: "candidatos_de_conhecimento",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_vinculos_entre_candidatos_candidatos_de_conhecimento_id_ca~1",
                        column: x => x.id_candidato_solucao,
                        principalSchema: "brain_data",
                        principalTable: "candidatos_de_conhecimento",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_vinculos_entre_candidatos_conhecimentos_id_conhecimento_inc~",
                        columns: x => new { x.id_conhecimento_incidente, x.id_espaco_de_conhecimento },
                        principalSchema: "brain_data",
                        principalTable: "conhecimentos",
                        principalColumns: new[] { "id", "id_espaco_de_conhecimento" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_vinculos_entre_candidatos_conhecimentos_id_conhecimento_sol~",
                        columns: x => new { x.id_conhecimento_solucao, x.id_espaco_de_conhecimento },
                        principalSchema: "brain_data",
                        principalTable: "conhecimentos",
                        principalColumns: new[] { "id", "id_espaco_de_conhecimento" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_vinculos_entre_candidatos_espacos_de_conhecimento_id_espaco~",
                        column: x => x.id_espaco_de_conhecimento,
                        principalSchema: "brain_data",
                        principalTable: "espacos_de_conhecimento",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_vinculos_entre_candidatos_projetos_id_projeto_id_espaco_de_~",
                        columns: x => new { x.id_projeto, x.id_espaco_de_conhecimento },
                        principalSchema: "brain_data",
                        principalTable: "projetos",
                        principalColumns: new[] { "id", "id_espaco_de_conhecimento" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_vinculos_entre_candidatos_relacoes_de_conhecimento_id_relac~",
                        column: x => x.id_relacao,
                        principalSchema: "brain_data",
                        principalTable: "relacoes_de_conhecimento",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_vinculos_entre_candidatos_id_candidato_incidente",
                schema: "brain_data",
                table: "vinculos_entre_candidatos",
                column: "id_candidato_incidente");

            migrationBuilder.CreateIndex(
                name: "IX_vinculos_entre_candidatos_id_candidato_solucao",
                schema: "brain_data",
                table: "vinculos_entre_candidatos",
                column: "id_candidato_solucao");

            migrationBuilder.CreateIndex(
                name: "IX_vinculos_entre_candidatos_id_conhecimento_incidente_id_espa~",
                schema: "brain_data",
                table: "vinculos_entre_candidatos",
                columns: new[] { "id_conhecimento_incidente", "id_espaco_de_conhecimento" });

            migrationBuilder.CreateIndex(
                name: "IX_vinculos_entre_candidatos_id_conhecimento_solucao_id_espaco~",
                schema: "brain_data",
                table: "vinculos_entre_candidatos",
                columns: new[] { "id_conhecimento_solucao", "id_espaco_de_conhecimento" });

            migrationBuilder.CreateIndex(
                name: "IX_vinculos_entre_candidatos_id_espaco_de_conhecimento_id_can~1",
                schema: "brain_data",
                table: "vinculos_entre_candidatos",
                columns: new[] { "id_espaco_de_conhecimento", "id_candidato_incidente", "id_candidato_solucao" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vinculos_entre_candidatos_id_espaco_de_conhecimento_id_cand~",
                schema: "brain_data",
                table: "vinculos_entre_candidatos",
                columns: new[] { "id_espaco_de_conhecimento", "id_candidato_solucao" });

            migrationBuilder.CreateIndex(
                name: "IX_vinculos_entre_candidatos_id_projeto_id_espaco_de_conhecime~",
                schema: "brain_data",
                table: "vinculos_entre_candidatos",
                columns: new[] { "id_projeto", "id_espaco_de_conhecimento" });

            migrationBuilder.CreateIndex(
                name: "IX_vinculos_entre_candidatos_id_relacao",
                schema: "brain_data",
                table: "vinculos_entre_candidatos",
                column: "id_relacao");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vinculos_entre_candidatos",
                schema: "brain_data");
        }
    }
}
