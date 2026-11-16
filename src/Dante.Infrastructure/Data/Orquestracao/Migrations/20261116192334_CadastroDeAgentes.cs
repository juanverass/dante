using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Orquestracao.Migrations
{
    /// <inheritdoc />
    public partial class CadastroDeAgentes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "handle_do_agente",
                schema: "orquestracao",
                table: "sessoes_de_agente",
                type: "character varying(31)",
                maxLength: 31,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hash_da_configuracao_do_agente",
                schema: "orquestracao",
                table: "sessoes_de_agente",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "id_versao_do_agente",
                schema: "orquestracao",
                table: "sessoes_de_agente",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "id_versao_do_agente",
                schema: "orquestracao",
                table: "execucoes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agentes",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    handle = table.Column<string>(type: "character varying(31)", maxLength: 31, nullable: false),
                    nome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    descricao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    estado = table.Column<int>(type: "integer", nullable: false),
                    id_versao_atual = table.Column<Guid>(type: "uuid", nullable: true),
                    numero_da_versao_atual = table.Column<int>(type: "integer", nullable: false),
                    id_do_criador = table.Column<long>(type: "bigint", nullable: false),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    atualizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agentes", x => x.id);
                    table.CheckConstraint("ck_agentes_handle", "handle ~ '^[a-z][a-z0-9-]{1,30}$'");
                    table.CheckConstraint("ck_agentes_versao_atual", "numero_da_versao_atual >= 0");
                });

            migrationBuilder.CreateTable(
                name: "versoes_de_agente",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_agente = table.Column<Guid>(type: "uuid", nullable: false),
                    numero = table.Column<int>(type: "integer", nullable: false),
                    driver = table.Column<int>(type: "integer", nullable: false),
                    modelo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    esforco = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    instrucoes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    capacidades = table.Column<string[]>(type: "text[]", nullable: false),
                    perfil_padrao = table.Column<int>(type: "integer", nullable: false),
                    perfil_maximo = table.Column<int>(type: "integer", nullable: false),
                    pode_escrever = table.Column<bool>(type: "boolean", nullable: false),
                    ferramentas = table.Column<string[]>(type: "text[]", nullable: false),
                    repositorios = table.Column<string[]>(type: "text[]", nullable: false),
                    execucoes_simultaneas = table.Column<int>(type: "integer", nullable: true),
                    tokens_por_execucao = table.Column<long>(type: "bigint", nullable: true),
                    turnos_por_etapa = table.Column<int>(type: "integer", nullable: true),
                    minutos_por_etapa = table.Column<int>(type: "integer", nullable: true),
                    hash_da_configuracao = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    id_do_autor = table.Column<long>(type: "bigint", nullable: false),
                    criada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_versoes_de_agente", x => x.id);
                    table.CheckConstraint("ck_versoes_de_agente_hash", "hash_da_configuracao ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_versoes_de_agente_numero_positivo", "numero > 0");
                    table.ForeignKey(
                        name: "FK_versoes_de_agente_agentes_id_agente",
                        column: x => x.id_agente,
                        principalSchema: "orquestracao",
                        principalTable: "agentes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sessoes_de_agente_id_versao_do_agente",
                schema: "orquestracao",
                table: "sessoes_de_agente",
                column: "id_versao_do_agente");

            migrationBuilder.CreateIndex(
                name: "IX_execucoes_id_versao_do_agente",
                schema: "orquestracao",
                table: "execucoes",
                column: "id_versao_do_agente");

            migrationBuilder.AddCheckConstraint(
                name: "ck_execucoes_tarefa_com_versao",
                schema: "orquestracao",
                table: "execucoes",
                sql: "tipo <> 1 OR id_versao_do_agente IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_agentes_estado",
                schema: "orquestracao",
                table: "agentes",
                column: "estado");

            migrationBuilder.CreateIndex(
                name: "IX_agentes_handle",
                schema: "orquestracao",
                table: "agentes",
                column: "handle",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_versoes_de_agente_id_agente_numero",
                schema: "orquestracao",
                table: "versoes_de_agente",
                columns: new[] { "id_agente", "numero" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_execucoes_versoes_de_agente_id_versao_do_agente",
                schema: "orquestracao",
                table: "execucoes",
                column: "id_versao_do_agente",
                principalSchema: "orquestracao",
                principalTable: "versoes_de_agente",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sessoes_de_agente_versoes_de_agente_id_versao_do_agente",
                schema: "orquestracao",
                table: "sessoes_de_agente",
                column: "id_versao_do_agente",
                principalSchema: "orquestracao",
                principalTable: "versoes_de_agente",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // AD-68: versão é imutável e o handle nunca muda, nem por SQL direto. Remoção física de agente ou versão
            // também é recusada: arquivar é o fim do ciclo de vida.
            migrationBuilder.Sql("""
                CREATE FUNCTION orquestracao.recusar_alteracao_de_versao_de_agente() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'versões de agente são imutáveis' USING ERRCODE = 'restrict_violation';
                END $$;
                CREATE TRIGGER trg_versoes_de_agente_imutaveis BEFORE UPDATE OR DELETE ON orquestracao.versoes_de_agente
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.recusar_alteracao_de_versao_de_agente();
                CREATE FUNCTION orquestracao.proteger_identidade_do_agente() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'agentes não são removidos; arquive' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.handle <> OLD.handle THEN
                        RAISE EXCEPTION 'o handle de um agente é imutável' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_agentes_identidade BEFORE UPDATE OR DELETE ON orquestracao.agentes
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.proteger_identidade_do_agente();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_agentes_identidade ON orquestracao.agentes;
                DROP FUNCTION IF EXISTS orquestracao.proteger_identidade_do_agente();
                DROP TRIGGER IF EXISTS trg_versoes_de_agente_imutaveis ON orquestracao.versoes_de_agente;
                DROP FUNCTION IF EXISTS orquestracao.recusar_alteracao_de_versao_de_agente();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_execucoes_versoes_de_agente_id_versao_do_agente",
                schema: "orquestracao",
                table: "execucoes");

            migrationBuilder.DropForeignKey(
                name: "FK_sessoes_de_agente_versoes_de_agente_id_versao_do_agente",
                schema: "orquestracao",
                table: "sessoes_de_agente");

            migrationBuilder.DropTable(
                name: "versoes_de_agente",
                schema: "orquestracao");

            migrationBuilder.DropTable(
                name: "agentes",
                schema: "orquestracao");

            migrationBuilder.DropIndex(
                name: "IX_sessoes_de_agente_id_versao_do_agente",
                schema: "orquestracao",
                table: "sessoes_de_agente");

            migrationBuilder.DropIndex(
                name: "IX_execucoes_id_versao_do_agente",
                schema: "orquestracao",
                table: "execucoes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_execucoes_tarefa_com_versao",
                schema: "orquestracao",
                table: "execucoes");

            migrationBuilder.DropColumn(
                name: "handle_do_agente",
                schema: "orquestracao",
                table: "sessoes_de_agente");

            migrationBuilder.DropColumn(
                name: "hash_da_configuracao_do_agente",
                schema: "orquestracao",
                table: "sessoes_de_agente");

            migrationBuilder.DropColumn(
                name: "id_versao_do_agente",
                schema: "orquestracao",
                table: "sessoes_de_agente");

            migrationBuilder.DropColumn(
                name: "id_versao_do_agente",
                schema: "orquestracao",
                table: "execucoes");
        }
    }
}
