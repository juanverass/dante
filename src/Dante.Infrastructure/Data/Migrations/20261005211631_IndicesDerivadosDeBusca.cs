using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class IndicesDerivadosDeBusca : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE SCHEMA IF NOT EXISTS brain_index;
                CREATE TABLE brain_index.trabalhos (
                    id_conhecimento uuid PRIMARY KEY REFERENCES brain_data.conhecimentos(id) ON DELETE CASCADE,
                    revisao integer NOT NULL,
                    documento tsvector NOT NULL
                );
                CREATE INDEX ix_trabalhos_documento ON brain_index.trabalhos USING GIN(documento);
                CREATE TABLE brain_index.representacoes (
                    id_conhecimento uuid NOT NULL REFERENCES brain_data.conhecimentos(id) ON DELETE CASCADE,
                    revisao integer NOT NULL,
                    modelo text NOT NULL,
                    provedor text NOT NULL,
                    nome text NOT NULL,
                    versao text NOT NULL,
                    dimensao integer NOT NULL CHECK (dimensao BETWEEN 1 AND 16000),
                    metrica text NOT NULL DEFAULT 'cosine',
                    normalizado boolean NOT NULL DEFAULT true,
                    versao_indexador integer NOT NULL DEFAULT 1,
                    gerado_em timestamptz NOT NULL,
                    PRIMARY KEY(id_conhecimento,modelo)
                );
                CREATE FUNCTION brain_index.atualizar_conhecimento() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    INSERT INTO brain_index.trabalhos(id_conhecimento,revisao,documento)
                    VALUES(NEW.id,jsonb_array_length(NEW.historico),
                        to_tsvector('portuguese',coalesce(NEW.conteudo,'') || ' ' || coalesce(NEW.dados_estruturados::text,'') || ' ' || array_to_string(NEW.tags,' ')))
                    ON CONFLICT(id_conhecimento) DO UPDATE SET revisao=EXCLUDED.revisao,documento=EXCLUDED.documento;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER indice_conhecimento AFTER INSERT OR UPDATE ON brain_data.conhecimentos
                    FOR EACH ROW EXECUTE FUNCTION brain_index.atualizar_conhecimento();
                INSERT INTO brain_index.trabalhos(id_conhecimento,revisao,documento)
                    SELECT id,jsonb_array_length(historico),to_tsvector('portuguese',coalesce(conteudo,'') || ' ' || coalesce(dados_estruturados::text,'') || ' ' || array_to_string(tags,' '))
                    FROM brain_data.conhecimentos;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER indice_conhecimento ON brain_data.conhecimentos;
                DROP FUNCTION brain_index.atualizar_conhecimento();
                DROP TABLE brain_index.representacoes;
                DROP TABLE brain_index.trabalhos;
                """);

        }
    }
}
