using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Orquestracao.Migrations
{
    /// <inheritdoc />
    public partial class GruposDeAgentes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "grupos_de_agentes",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    nome = table.Column<string>(type: "character varying(31)", maxLength: 31, nullable: false),
                    descricao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    estado = table.Column<int>(type: "integer", nullable: false),
                    id_versao_atual = table.Column<Guid>(type: "uuid", nullable: true),
                    numero_da_versao_atual = table.Column<int>(type: "integer", nullable: false),
                    id_versao_ativa = table.Column<Guid>(type: "uuid", nullable: true),
                    numero_da_versao_ativa = table.Column<int>(type: "integer", nullable: true),
                    id_do_criador = table.Column<long>(type: "bigint", nullable: false),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    atualizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grupos_de_agentes", x => x.id);
                    table.CheckConstraint("ck_grupos_de_agentes_nome", "nome ~ '^[a-z][a-z0-9-]{1,30}$'");
                    table.CheckConstraint("ck_grupos_de_agentes_versao_ativa", "(id_versao_ativa IS NULL) = (numero_da_versao_ativa IS NULL) AND (numero_da_versao_ativa IS NULL OR numero_da_versao_ativa BETWEEN 1 AND numero_da_versao_atual)");
                    table.CheckConstraint("ck_grupos_de_agentes_versao_atual", "numero_da_versao_atual >= 0");
                });

            migrationBuilder.CreateTable(
                name: "versoes_de_grupo",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_grupo = table.Column<Guid>(type: "uuid", nullable: false),
                    numero = table.Column<int>(type: "integer", nullable: false),
                    modo = table.Column<int>(type: "integer", nullable: false),
                    roteiro = table.Column<string[]>(type: "text[]", nullable: false),
                    rodadas_de_revisao_maximas = table.Column<int>(type: "integer", nullable: false),
                    paralelismo_maximo = table.Column<int>(type: "integer", nullable: false),
                    repositorios_permitidos = table.Column<string[]>(type: "text[]", nullable: false),
                    duracao_maxima_em_minutos = table.Column<int>(type: "integer", nullable: true),
                    hash_da_configuracao = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    id_do_autor = table.Column<long>(type: "bigint", nullable: false),
                    criada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_versoes_de_grupo", x => x.id);
                    table.CheckConstraint("ck_versoes_de_grupo_duracao", "duracao_maxima_em_minutos IS NULL OR duracao_maxima_em_minutos > 0");
                    table.CheckConstraint("ck_versoes_de_grupo_hash", "hash_da_configuracao ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_versoes_de_grupo_numero_positivo", "numero > 0");
                    table.CheckConstraint("ck_versoes_de_grupo_paralelismo", "paralelismo_maximo = 1");
                    table.CheckConstraint("ck_versoes_de_grupo_rodadas", "rodadas_de_revisao_maximas BETWEEN 0 AND 5");
                    table.ForeignKey(
                        name: "FK_versoes_de_grupo_grupos_de_agentes_id_grupo",
                        column: x => x.id_grupo,
                        principalSchema: "orquestracao",
                        principalTable: "grupos_de_agentes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ativacoes_de_grupo",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_grupo = table.Column<Guid>(type: "uuid", nullable: false),
                    id_versao_do_grupo = table.Column<Guid>(type: "uuid", nullable: false),
                    numero_da_versao = table.Column<int>(type: "integer", nullable: false),
                    hash_aprovado = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    id_do_autor = table.Column<long>(type: "bigint", nullable: false),
                    em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ativacoes_de_grupo", x => x.id);
                    table.CheckConstraint("ck_ativacoes_de_grupo_hash", "hash_aprovado ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_ativacoes_de_grupo_grupos_de_agentes_id_grupo",
                        column: x => x.id_grupo,
                        principalSchema: "orquestracao",
                        principalTable: "grupos_de_agentes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ativacoes_de_grupo_versoes_de_grupo_id_versao_do_grupo",
                        column: x => x.id_versao_do_grupo,
                        principalSchema: "orquestracao",
                        principalTable: "versoes_de_grupo",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "participantes_de_grupo",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_versao_do_grupo = table.Column<Guid>(type: "uuid", nullable: false),
                    id_agente = table.Column<Guid>(type: "uuid", nullable: false),
                    papel = table.Column<string>(type: "character varying(31)", maxLength: 31, nullable: false),
                    responsabilidades = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    pode_escrever = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_participantes_de_grupo", x => x.id);
                    table.CheckConstraint("ck_participantes_de_grupo_papel", "papel ~ '^[a-z][a-z0-9-]{1,30}$'");
                    table.ForeignKey(
                        name: "FK_participantes_de_grupo_agentes_id_agente",
                        column: x => x.id_agente,
                        principalSchema: "orquestracao",
                        principalTable: "agentes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_participantes_de_grupo_versoes_de_grupo_id_versao_do_grupo",
                        column: x => x.id_versao_do_grupo,
                        principalSchema: "orquestracao",
                        principalTable: "versoes_de_grupo",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ativacoes_de_grupo_id_grupo_em",
                schema: "orquestracao",
                table: "ativacoes_de_grupo",
                columns: new[] { "id_grupo", "em" });

            migrationBuilder.CreateIndex(
                name: "IX_ativacoes_de_grupo_id_versao_do_grupo",
                schema: "orquestracao",
                table: "ativacoes_de_grupo",
                column: "id_versao_do_grupo");

            migrationBuilder.CreateIndex(
                name: "IX_grupos_de_agentes_estado",
                schema: "orquestracao",
                table: "grupos_de_agentes",
                column: "estado");

            migrationBuilder.CreateIndex(
                name: "IX_grupos_de_agentes_nome",
                schema: "orquestracao",
                table: "grupos_de_agentes",
                column: "nome",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_participantes_de_grupo_id_agente",
                schema: "orquestracao",
                table: "participantes_de_grupo",
                column: "id_agente");

            migrationBuilder.CreateIndex(
                name: "IX_participantes_de_grupo_id_versao_do_grupo_papel",
                schema: "orquestracao",
                table: "participantes_de_grupo",
                columns: new[] { "id_versao_do_grupo", "papel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_versoes_de_grupo_id_grupo_numero",
                schema: "orquestracao",
                table: "versoes_de_grupo",
                columns: new[] { "id_grupo", "numero" },
                unique: true);

            // Imutabilidade e integridade da ativação ficam no banco, além do domínio (AD-69, #308).
            migrationBuilder.Sql("""
                CREATE FUNCTION orquestracao.recusar_alteracao_de_grupo_versionado() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION '% são imutáveis', TG_ARGV[0] USING ERRCODE = 'restrict_violation';
                END $$;
                CREATE TRIGGER trg_versoes_de_grupo_imutaveis BEFORE UPDATE OR DELETE ON orquestracao.versoes_de_grupo
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.recusar_alteracao_de_grupo_versionado('versões de grupo');
                CREATE TRIGGER trg_participantes_de_grupo_imutaveis BEFORE UPDATE OR DELETE ON orquestracao.participantes_de_grupo
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.recusar_alteracao_de_grupo_versionado('participantes de grupo');
                CREATE TRIGGER trg_ativacoes_de_grupo_imutaveis BEFORE UPDATE OR DELETE ON orquestracao.ativacoes_de_grupo
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.recusar_alteracao_de_grupo_versionado('decisões de ativação de grupo');

                -- A decisão grava exatamente a versão, o número e o hash gravados daquela versão do mesmo grupo.
                CREATE FUNCTION orquestracao.conferir_ativacao_de_grupo() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM orquestracao.versoes_de_grupo v
                                   WHERE v.id = NEW.id_versao_do_grupo AND v.id_grupo = NEW.id_grupo
                                     AND v.numero = NEW.numero_da_versao AND v.hash_da_configuracao = NEW.hash_aprovado) THEN
                        RAISE EXCEPTION 'a ativação não confere com a versão gravada' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_ativacoes_de_grupo_conferidas BEFORE INSERT ON orquestracao.ativacoes_de_grupo
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.conferir_ativacao_de_grupo();

                CREATE FUNCTION orquestracao.proteger_identidade_do_grupo() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'grupos não são removidos; arquive' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.nome <> OLD.nome THEN
                        RAISE EXCEPTION 'o nome de um grupo é imutável' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_grupos_de_agentes_identidade BEFORE UPDATE OR DELETE ON orquestracao.grupos_de_agentes
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.proteger_identidade_do_grupo();

                -- Versão ativa só com decisão humana gravada para ela. Conferido no commit (constraint trigger adiada):
                -- a ordem entre o UPDATE do grupo e o INSERT da decisão, na mesma transação, não importa.
                CREATE FUNCTION orquestracao.conferir_versao_ativa_do_grupo() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    atual orquestracao.grupos_de_agentes%ROWTYPE;
                BEGIN
                    SELECT * INTO atual FROM orquestracao.grupos_de_agentes WHERE id = NEW.id;
                    IF NOT FOUND OR atual.id_versao_ativa IS NULL THEN
                        RETURN NULL;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM orquestracao.ativacoes_de_grupo a
                                   JOIN orquestracao.versoes_de_grupo v ON v.id = a.id_versao_do_grupo
                                   WHERE a.id_grupo = atual.id AND a.id_versao_do_grupo = atual.id_versao_ativa
                                     AND v.numero = atual.numero_da_versao_ativa) THEN
                        RAISE EXCEPTION 'versão ativa sem decisão de ativação' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NULL;
                END $$;
                CREATE CONSTRAINT TRIGGER trg_grupos_de_agentes_versao_ativa
                    AFTER INSERT OR UPDATE OF id_versao_ativa, numero_da_versao_ativa ON orquestracao.grupos_de_agentes
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.conferir_versao_ativa_do_grupo();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_grupos_de_agentes_versao_ativa ON orquestracao.grupos_de_agentes;
                DROP FUNCTION IF EXISTS orquestracao.conferir_versao_ativa_do_grupo();
                DROP TRIGGER IF EXISTS trg_grupos_de_agentes_identidade ON orquestracao.grupos_de_agentes;
                DROP FUNCTION IF EXISTS orquestracao.proteger_identidade_do_grupo();
                DROP TRIGGER IF EXISTS trg_ativacoes_de_grupo_conferidas ON orquestracao.ativacoes_de_grupo;
                DROP FUNCTION IF EXISTS orquestracao.conferir_ativacao_de_grupo();
                DROP TRIGGER IF EXISTS trg_ativacoes_de_grupo_imutaveis ON orquestracao.ativacoes_de_grupo;
                DROP TRIGGER IF EXISTS trg_participantes_de_grupo_imutaveis ON orquestracao.participantes_de_grupo;
                DROP TRIGGER IF EXISTS trg_versoes_de_grupo_imutaveis ON orquestracao.versoes_de_grupo;
                DROP FUNCTION IF EXISTS orquestracao.recusar_alteracao_de_grupo_versionado();
                """);

            migrationBuilder.DropTable(
                name: "ativacoes_de_grupo",
                schema: "orquestracao");

            migrationBuilder.DropTable(
                name: "participantes_de_grupo",
                schema: "orquestracao");

            migrationBuilder.DropTable(
                name: "versoes_de_grupo",
                schema: "orquestracao");

            migrationBuilder.DropTable(
                name: "grupos_de_agentes",
                schema: "orquestracao");
        }
    }
}
