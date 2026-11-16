using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Orquestracao.Migrations
{
    /// <inheritdoc />
    public partial class FilaDuravel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "boot_do_worker",
                schema: "orquestracao",
                table: "sessoes_de_agente",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "maquina_do_worker",
                schema: "orquestracao",
                table: "sessoes_de_agente",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "unidade_de_contencao",
                schema: "orquestracao",
                table: "sessoes_de_agente",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "trabalhos_da_fila",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tipo = table.Column<int>(type: "integer", nullable: false),
                    estado = table.Column<int>(type: "integer", nullable: false),
                    id_execucao = table.Column<Guid>(type: "uuid", nullable: true),
                    id_etapa = table.Column<Guid>(type: "uuid", nullable: true),
                    dados = table.Column<string>(type: "jsonb", nullable: true),
                    chave_de_deduplicacao = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    prioridade = table.Column<int>(type: "integer", nullable: false),
                    disponivel_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tentativas = table.Column<int>(type: "integer", nullable: false),
                    maximo_de_tentativas = table.Column<int>(type: "integer", nullable: false),
                    trabalhador = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    boot_do_trabalhador = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_ate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    geracao = table.Column<long>(type: "bigint", nullable: false),
                    ultimo_erro = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    encerrado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trabalhos_da_fila", x => x.id);
                    table.CheckConstraint("ck_trabalhos_da_fila_etapa_com_execucao", "id_etapa IS NULL OR id_execucao IS NOT NULL");
                    table.CheckConstraint("ck_trabalhos_da_fila_geracao", "geracao >= 0");
                    table.CheckConstraint("ck_trabalhos_da_fila_posse", "(estado = 1 AND trabalhador IS NOT NULL AND boot_do_trabalhador IS NOT NULL AND lease_ate IS NOT NULL) OR (estado <> 1 AND lease_ate IS NULL)");
                    table.CheckConstraint("ck_trabalhos_da_fila_tentativas", "tentativas >= 0 AND maximo_de_tentativas > 0");
                    table.ForeignKey(
                        name: "FK_trabalhos_da_fila_etapas_da_execucao_id_etapa_id_execucao",
                        columns: x => new { x.id_etapa, x.id_execucao },
                        principalSchema: "orquestracao",
                        principalTable: "etapas_da_execucao",
                        principalColumns: new[] { "id", "id_execucao" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_trabalhos_da_fila_execucoes_id_execucao",
                        column: x => x.id_execucao,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sessoes_de_agente_unidade_de_contencao",
                schema: "orquestracao",
                table: "sessoes_de_agente",
                column: "unidade_de_contencao");

            migrationBuilder.CreateIndex(
                name: "IX_trabalhos_da_fila_chave_de_deduplicacao",
                schema: "orquestracao",
                table: "trabalhos_da_fila",
                column: "chave_de_deduplicacao",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_trabalhos_da_fila_id_etapa_id_execucao",
                schema: "orquestracao",
                table: "trabalhos_da_fila",
                columns: new[] { "id_etapa", "id_execucao" });

            migrationBuilder.CreateIndex(
                name: "IX_trabalhos_da_fila_id_execucao",
                schema: "orquestracao",
                table: "trabalhos_da_fila",
                column: "id_execucao");

            migrationBuilder.CreateIndex(
                name: "IX_trabalhos_da_fila_lease_ate",
                schema: "orquestracao",
                table: "trabalhos_da_fila",
                column: "lease_ate",
                filter: "estado = 1");

            migrationBuilder.CreateIndex(
                name: "IX_trabalhos_da_fila_prioridade_disponivel_em",
                schema: "orquestracao",
                table: "trabalhos_da_fila",
                columns: new[] { "prioridade", "disponivel_em" },
                filter: "estado = 0");

            // Sinal da fila (AD-67): todo trabalho que fica pendente notifica o canal dante_fila. O PostgreSQL só entrega
            // a notificação no commit, e uma vez por transação para o mesmo canal e payload. O processador também faz
            // polling: o trigger é atalho, não garantia.
            migrationBuilder.Sql("""
                CREATE FUNCTION orquestracao.notificar_fila() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  PERFORM pg_notify('dante_fila', '');
                  RETURN NULL;
                END $$;
                CREATE TRIGGER trg_trabalhos_da_fila_notificar
                  AFTER INSERT OR UPDATE OF estado, disponivel_em ON orquestracao.trabalhos_da_fila
                  FOR EACH ROW WHEN (NEW.estado = 0) EXECUTE FUNCTION orquestracao.notificar_fila();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trabalhos_da_fila",
                schema: "orquestracao");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS orquestracao.notificar_fila();");

            migrationBuilder.DropIndex(
                name: "IX_sessoes_de_agente_unidade_de_contencao",
                schema: "orquestracao",
                table: "sessoes_de_agente");

            migrationBuilder.DropColumn(
                name: "boot_do_worker",
                schema: "orquestracao",
                table: "sessoes_de_agente");

            migrationBuilder.DropColumn(
                name: "maquina_do_worker",
                schema: "orquestracao",
                table: "sessoes_de_agente");

            migrationBuilder.DropColumn(
                name: "unidade_de_contencao",
                schema: "orquestracao",
                table: "sessoes_de_agente");
        }
    }
}
