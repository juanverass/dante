using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class FontesRastreaveis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "documentos_fonte",
                schema: "brain_data",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_espaco_de_conhecimento = table.Column<Guid>(type: "uuid", nullable: false),
                    id_projeto = table.Column<Guid>(type: "uuid", nullable: true),
                    origem = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    formato = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    conteudo = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sensibilidade = table.Column<int>(type: "integer", nullable: false),
                    revisao = table.Column<int>(type: "integer", nullable: false),
                    id_responsavel = table.Column<Guid>(type: "uuid", nullable: false),
                    atualizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removido = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documentos_fonte", x => x.id);
                    table.ForeignKey(
                        name: "FK_documentos_fonte_espacos_de_conhecimento_id_espaco_de_conhe~",
                        column: x => x.id_espaco_de_conhecimento,
                        principalSchema: "brain_data",
                        principalTable: "espacos_de_conhecimento",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_documentos_fonte_projetos_id_projeto_id_espaco_de_conhecime~",
                        columns: x => new { x.id_projeto, x.id_espaco_de_conhecimento },
                        principalSchema: "brain_data",
                        principalTable: "projetos",
                        principalColumns: new[] { "id", "id_espaco_de_conhecimento" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_documentos_fonte_id_espaco_de_conhecimento_id_projeto_origem",
                schema: "brain_data",
                table: "documentos_fonte",
                columns: new[] { "id_espaco_de_conhecimento", "id_projeto", "origem" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_documentos_fonte_id_projeto_id_espaco_de_conhecimento",
                schema: "brain_data",
                table: "documentos_fonte",
                columns: new[] { "id_projeto", "id_espaco_de_conhecimento" });
            migrationBuilder.Sql("""
                CREATE TABLE brain_index.partes_fontes (
                  id_documento uuid NOT NULL REFERENCES brain_data.documentos_fonte(id) ON DELETE CASCADE,
                  revisao integer NOT NULL, numero integer NOT NULL, inicio integer NOT NULL,
                  conteudo text NOT NULL, documento tsvector NOT NULL, modelo text NULL, dimensao integer NULL,
                  PRIMARY KEY(id_documento,numero));
                CREATE INDEX ix_partes_fontes_documento ON brain_index.partes_fontes USING GIN(documento);
                DO $$ BEGIN IF EXISTS(SELECT 1 FROM pg_extension WHERE extname='vector') THEN
                  ALTER TABLE brain_index.partes_fontes ADD COLUMN vetor vector; END IF; END $$;
                CREATE FUNCTION brain_index.atualizar_partes_fonte() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  DELETE FROM brain_index.partes_fontes WHERE id_documento=NEW.id;
                  IF NOT NEW.removido AND NEW.sensibilidade<>4 THEN
                    INSERT INTO brain_index.partes_fontes(id_documento,revisao,numero,inicio,conteudo,documento)
                    SELECT NEW.id,NEW.revisao,(posicao-1)/1440,posicao-1,substring(NEW.conteudo FROM posicao FOR 1600),
                      to_tsvector('portuguese',substring(NEW.conteudo FROM posicao FOR 1600))
                    FROM generate_series(1,char_length(NEW.conteudo),1440) posicao;
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER tr_partes_fonte AFTER INSERT OR UPDATE ON brain_data.documentos_fonte
                  FOR EACH ROW EXECUTE FUNCTION brain_index.atualizar_partes_fonte();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE brain_index.partes_fontes; DROP FUNCTION brain_index.atualizar_partes_fonte() CASCADE;");
            migrationBuilder.DropTable(
                name: "documentos_fonte",
                schema: "brain_data");
        }
    }
}
