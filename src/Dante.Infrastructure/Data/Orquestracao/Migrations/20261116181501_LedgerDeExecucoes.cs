using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Orquestracao.Migrations
{
    /// <inheritdoc />
    public partial class LedgerDeExecucoes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "orquestracao");

            migrationBuilder.CreateSequence(
                name: "seq_eventos",
                schema: "orquestracao");

            migrationBuilder.CreateSequence(
                name: "seq_numero_da_execucao",
                schema: "orquestracao");

            migrationBuilder.CreateSequence(
                name: "seq_numero_da_sessao",
                schema: "orquestracao");

            migrationBuilder.CreateTable(
                name: "execucoes",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    numero = table.Column<long>(type: "bigint", nullable: false),
                    tipo = table.Column<int>(type: "integer", nullable: false),
                    estado = table.Column<int>(type: "integer", nullable: false),
                    canal = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    id_do_usuario_no_canal = table.Column<long>(type: "bigint", nullable: false),
                    contexto = table.Column<string>(type: "jsonb", nullable: false),
                    objetivo = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    erro = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    motivo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    criada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    iniciada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    encerrada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execucoes", x => x.id);
                    table.CheckConstraint("ck_execucoes_numero_positivo", "numero > 0");
                });

            migrationBuilder.CreateTable(
                name: "sessoes_de_agente",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    numero = table.Column<long>(type: "bigint", nullable: false),
                    agente = table.Column<int>(type: "integer", nullable: false),
                    estado = table.Column<int>(type: "integer", nullable: false),
                    canal = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    id_do_dono = table.Column<long>(type: "bigint", nullable: false),
                    contexto = table.Column<string>(type: "jsonb", nullable: false),
                    perfil = table.Column<int>(type: "integer", nullable: false),
                    modelo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    esforco = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    modelo_informado = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    id_upstream = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    id_execucao = table.Column<Guid>(type: "uuid", nullable: true),
                    motivo_do_fim = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    criada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    iniciada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    encerrada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessoes_de_agente", x => x.id);
                    table.CheckConstraint("ck_sessoes_de_agente_numero_positivo", "numero > 0");
                    table.ForeignKey(
                        name: "FK_sessoes_de_agente_execucoes_id_execucao",
                        column: x => x.id_execucao,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "etapas_da_execucao",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_execucao = table.Column<Guid>(type: "uuid", nullable: false),
                    numero = table.Column<int>(type: "integer", nullable: false),
                    tipo = table.Column<int>(type: "integer", nullable: false),
                    estado = table.Column<int>(type: "integer", nullable: false),
                    escrita = table.Column<bool>(type: "boolean", nullable: false),
                    politica_de_retomada = table.Column<int>(type: "integer", nullable: false),
                    tentativa = table.Column<int>(type: "integer", nullable: false),
                    chave_de_idempotencia = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    agente = table.Column<int>(type: "integer", nullable: true),
                    modelo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    esforco = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    id_sessao_de_agente = table.Column<Guid>(type: "uuid", nullable: true),
                    id_turno = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    codigo_de_saida = table.Column<int>(type: "integer", nullable: true),
                    erro = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    motivo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    criada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    iniciada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    encerrada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_etapas_da_execucao", x => x.id);
                    table.UniqueConstraint("AK_etapas_da_execucao_id_id_execucao", x => new { x.id, x.id_execucao });
                    table.CheckConstraint("ck_etapas_da_execucao_numero_positivo", "numero > 0");
                    table.CheckConstraint("ck_etapas_da_execucao_tentativa_positiva", "tentativa > 0");
                    table.ForeignKey(
                        name: "FK_etapas_da_execucao_execucoes_id_execucao",
                        column: x => x.id_execucao,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_etapas_da_execucao_sessoes_de_agente_id_sessao_de_agente",
                        column: x => x.id_sessao_de_agente,
                        principalSchema: "orquestracao",
                        principalTable: "sessoes_de_agente",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dependencias_de_etapa",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_execucao = table.Column<Guid>(type: "uuid", nullable: false),
                    id_etapa = table.Column<Guid>(type: "uuid", nullable: false),
                    id_etapa_requerida = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dependencias_de_etapa", x => x.id);
                    table.CheckConstraint("ck_dependencias_de_etapa_distintas", "id_etapa <> id_etapa_requerida");
                    table.ForeignKey(
                        name: "FK_dependencias_de_etapa_etapas_da_execucao_id_etapa_id_execuc~",
                        columns: x => new { x.id_etapa, x.id_execucao },
                        principalSchema: "orquestracao",
                        principalTable: "etapas_da_execucao",
                        principalColumns: new[] { "id", "id_execucao" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dependencias_de_etapa_etapas_da_execucao_id_etapa_requerida~",
                        columns: x => new { x.id_etapa_requerida, x.id_execucao },
                        principalSchema: "orquestracao",
                        principalTable: "etapas_da_execucao",
                        principalColumns: new[] { "id", "id_execucao" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dependencias_de_etapa_execucoes_id_execucao",
                        column: x => x.id_execucao,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "eventos",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequencia = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('orquestracao.seq_eventos')"),
                    tipo = table.Column<int>(type: "integer", nullable: false),
                    id_execucao = table.Column<Guid>(type: "uuid", nullable: true),
                    id_etapa = table.Column<Guid>(type: "uuid", nullable: true),
                    id_sessao_de_agente = table.Column<Guid>(type: "uuid", nullable: true),
                    id_turno = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    dados = table.Column<string>(type: "jsonb", nullable: true),
                    ocorrido_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eventos", x => x.id);
                    table.CheckConstraint("ck_eventos_com_dono", "id_execucao IS NOT NULL OR id_sessao_de_agente IS NOT NULL");
                    table.CheckConstraint("ck_eventos_etapa_com_execucao", "id_etapa IS NULL OR id_execucao IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_eventos_etapas_da_execucao_id_etapa_id_execucao",
                        columns: x => new { x.id_etapa, x.id_execucao },
                        principalSchema: "orquestracao",
                        principalTable: "etapas_da_execucao",
                        principalColumns: new[] { "id", "id_execucao" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_eventos_execucoes_id_execucao",
                        column: x => x.id_execucao,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_eventos_sessoes_de_agente_id_sessao_de_agente",
                        column: x => x.id_sessao_de_agente,
                        principalSchema: "orquestracao",
                        principalTable: "sessoes_de_agente",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dependencias_de_etapa_id_etapa_id_etapa_requerida",
                schema: "orquestracao",
                table: "dependencias_de_etapa",
                columns: new[] { "id_etapa", "id_etapa_requerida" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dependencias_de_etapa_id_etapa_id_execucao",
                schema: "orquestracao",
                table: "dependencias_de_etapa",
                columns: new[] { "id_etapa", "id_execucao" });

            migrationBuilder.CreateIndex(
                name: "IX_dependencias_de_etapa_id_etapa_requerida_id_execucao",
                schema: "orquestracao",
                table: "dependencias_de_etapa",
                columns: new[] { "id_etapa_requerida", "id_execucao" });

            migrationBuilder.CreateIndex(
                name: "IX_dependencias_de_etapa_id_execucao",
                schema: "orquestracao",
                table: "dependencias_de_etapa",
                column: "id_execucao");

            migrationBuilder.CreateIndex(
                name: "IX_etapas_da_execucao_chave_de_idempotencia",
                schema: "orquestracao",
                table: "etapas_da_execucao",
                column: "chave_de_idempotencia",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_etapas_da_execucao_id_execucao_numero",
                schema: "orquestracao",
                table: "etapas_da_execucao",
                columns: new[] { "id_execucao", "numero" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_etapas_da_execucao_id_sessao_de_agente",
                schema: "orquestracao",
                table: "etapas_da_execucao",
                column: "id_sessao_de_agente");

            migrationBuilder.CreateIndex(
                name: "IX_eventos_id_etapa_id_execucao",
                schema: "orquestracao",
                table: "eventos",
                columns: new[] { "id_etapa", "id_execucao" });

            migrationBuilder.CreateIndex(
                name: "IX_eventos_id_execucao_sequencia",
                schema: "orquestracao",
                table: "eventos",
                columns: new[] { "id_execucao", "sequencia" });

            migrationBuilder.CreateIndex(
                name: "IX_eventos_id_sessao_de_agente_sequencia",
                schema: "orquestracao",
                table: "eventos",
                columns: new[] { "id_sessao_de_agente", "sequencia" });

            migrationBuilder.CreateIndex(
                name: "IX_eventos_sequencia",
                schema: "orquestracao",
                table: "eventos",
                column: "sequencia",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_execucoes_estado",
                schema: "orquestracao",
                table: "execucoes",
                column: "estado");

            migrationBuilder.CreateIndex(
                name: "IX_execucoes_numero",
                schema: "orquestracao",
                table: "execucoes",
                column: "numero",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessoes_de_agente_estado",
                schema: "orquestracao",
                table: "sessoes_de_agente",
                column: "estado");

            migrationBuilder.CreateIndex(
                name: "IX_sessoes_de_agente_id_execucao",
                schema: "orquestracao",
                table: "sessoes_de_agente",
                column: "id_execucao");

            migrationBuilder.CreateIndex(
                name: "IX_sessoes_de_agente_numero",
                schema: "orquestracao",
                table: "sessoes_de_agente",
                column: "numero",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dependencias_de_etapa",
                schema: "orquestracao");

            migrationBuilder.DropTable(
                name: "eventos",
                schema: "orquestracao");

            migrationBuilder.DropTable(
                name: "etapas_da_execucao",
                schema: "orquestracao");

            migrationBuilder.DropTable(
                name: "sessoes_de_agente",
                schema: "orquestracao");

            migrationBuilder.DropTable(
                name: "execucoes",
                schema: "orquestracao");

            migrationBuilder.DropSequence(
                name: "seq_eventos",
                schema: "orquestracao");

            migrationBuilder.DropSequence(
                name: "seq_numero_da_execucao",
                schema: "orquestracao");

            migrationBuilder.DropSequence(
                name: "seq_numero_da_sessao",
                schema: "orquestracao");
        }
    }
}
