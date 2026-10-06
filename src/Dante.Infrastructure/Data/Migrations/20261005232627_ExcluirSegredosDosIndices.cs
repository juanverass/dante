using Microsoft.EntityFrameworkCore.Migrations;
using Dante.Domain.Conhecimentos;

#nullable disable

namespace Dante.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExcluirSegredosDosIndices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                LOCK TABLE brain_data.conhecimentos IN SHARE ROW EXCLUSIVE MODE;
                CREATE OR REPLACE FUNCTION brain_index.atualizar_conhecimento() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.sensibilidade = {(int)Sensibilidade.Secreto} THEN
                        DELETE FROM brain_index.trabalhos WHERE id_conhecimento = NEW.id;
                        DELETE FROM brain_index.representacoes WHERE id_conhecimento = NEW.id;
                        RETURN NEW;
                    END IF;
                    INSERT INTO brain_index.trabalhos(id_conhecimento,revisao,documento)
                    VALUES(NEW.id,jsonb_array_length(NEW.historico),
                        to_tsvector('portuguese',coalesce(NEW.conteudo,'') || ' ' || coalesce(NEW.dados_estruturados::text,'') || ' ' || array_to_string(NEW.tags,' ')))
                    ON CONFLICT(id_conhecimento) DO UPDATE SET revisao=EXCLUDED.revisao,documento=EXCLUDED.documento;
                    RETURN NEW;
                END $$;
                DELETE FROM brain_index.trabalhos t USING brain_data.conhecimentos c
                    WHERE t.id_conhecimento = c.id AND c.sensibilidade = {(int)Sensibilidade.Secreto};
                DELETE FROM brain_index.representacoes r USING brain_data.conhecimentos c
                    WHERE r.id_conhecimento = c.id AND c.sensibilidade = {(int)Sensibilidade.Secreto};
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Não reinstalar a função que copiava segredos. A migration anterior remove
            // o trigger e a função normalmente quando o índice inteiro é desfeito.

        }
    }
}
