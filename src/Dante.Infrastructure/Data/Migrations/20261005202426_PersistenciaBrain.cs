using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersistenciaBrain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "brain_data");

            migrationBuilder.CreateTable(
                name: "espacos_de_conhecimento",
                schema: "brain_data",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_usuario = table.Column<Guid>(type: "uuid", nullable: false),
                    nome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    descricao = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    estado = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_espacos_de_conhecimento", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "projetos",
                schema: "brain_data",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_espaco_de_conhecimento = table.Column<Guid>(type: "uuid", nullable: false),
                    nome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    descricao = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    alias_do_repositorio = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    estado = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projetos", x => x.id);
                    table.UniqueConstraint("AK_projetos_id_id_espaco_de_conhecimento", x => new { x.id, x.id_espaco_de_conhecimento });
                    table.ForeignKey(
                        name: "FK_projetos_espacos_de_conhecimento_id_espaco_de_conhecimento",
                        column: x => x.id_espaco_de_conhecimento,
                        principalSchema: "brain_data",
                        principalTable: "espacos_de_conhecimento",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "conhecimentos",
                schema: "brain_data",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_espaco_de_conhecimento = table.Column<Guid>(type: "uuid", nullable: false),
                    id_projeto = table.Column<Guid>(type: "uuid", nullable: true),
                    id_autor = table.Column<Guid>(type: "uuid", nullable: false),
                    tipo = table.Column<int>(type: "integer", nullable: false),
                    conteudo = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: true),
                    dados_estruturados = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    confianca = table.Column<double>(type: "double precision", nullable: true),
                    sensibilidade = table.Column<int>(type: "integer", nullable: false),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    atualizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valido_desde = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    valido_ate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tags = table.Column<string[]>(type: "text[]", nullable: false),
                    id_conhecimento_substituto = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    historico = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conhecimentos", x => x.id);
                    table.UniqueConstraint("AK_conhecimentos_id_id_espaco_de_conhecimento", x => new { x.id, x.id_espaco_de_conhecimento });
                    table.ForeignKey(
                        name: "FK_conhecimentos_conhecimentos_id_conhecimento_substituto_id_e~",
                        columns: x => new { x.id_conhecimento_substituto, x.id_espaco_de_conhecimento },
                        principalSchema: "brain_data",
                        principalTable: "conhecimentos",
                        principalColumns: new[] { "id", "id_espaco_de_conhecimento" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_conhecimentos_espacos_de_conhecimento_id_espaco_de_conhecim~",
                        column: x => x.id_espaco_de_conhecimento,
                        principalSchema: "brain_data",
                        principalTable: "espacos_de_conhecimento",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_conhecimentos_projetos_id_projeto_id_espaco_de_conhecimento",
                        columns: x => new { x.id_projeto, x.id_espaco_de_conhecimento },
                        principalSchema: "brain_data",
                        principalTable: "projetos",
                        principalColumns: new[] { "id", "id_espaco_de_conhecimento" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conhecimentos_id_conhecimento_substituto_id_espaco_de_conhe~",
                schema: "brain_data",
                table: "conhecimentos",
                columns: new[] { "id_conhecimento_substituto", "id_espaco_de_conhecimento" });

            migrationBuilder.CreateIndex(
                name: "IX_conhecimentos_id_espaco_de_conhecimento_id_projeto_status",
                schema: "brain_data",
                table: "conhecimentos",
                columns: new[] { "id_espaco_de_conhecimento", "id_projeto", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_conhecimentos_id_projeto_id_espaco_de_conhecimento",
                schema: "brain_data",
                table: "conhecimentos",
                columns: new[] { "id_projeto", "id_espaco_de_conhecimento" });

            migrationBuilder.CreateIndex(
                name: "IX_espacos_de_conhecimento_id_usuario_estado",
                schema: "brain_data",
                table: "espacos_de_conhecimento",
                columns: new[] { "id_usuario", "estado" });

            migrationBuilder.CreateIndex(
                name: "IX_projetos_id_espaco_de_conhecimento_estado",
                schema: "brain_data",
                table: "projetos",
                columns: new[] { "id_espaco_de_conhecimento", "estado" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conhecimentos",
                schema: "brain_data");

            migrationBuilder.DropTable(
                name: "projetos",
                schema: "brain_data");

            migrationBuilder.DropTable(
                name: "espacos_de_conhecimento",
                schema: "brain_data");
        }
    }
}
