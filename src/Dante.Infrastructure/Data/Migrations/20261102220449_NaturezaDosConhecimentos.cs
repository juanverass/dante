using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class NaturezaDosConhecimentos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "natureza",
                schema: "brain_data",
                table: "conhecimentos",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "natureza_de_origem",
                schema: "brain_data",
                table: "conhecimentos",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            // D3: só o candidato registrado prova a natureza de origem. Não deduzir de tipo/texto/proveniência.
            // Revisão atual e JSON histórico permanecem desconhecidos/não registrados; status não é reescrito.
            // Mais de um candidato com naturezas divergentes é ambíguo: conserva Desconhecida.
            migrationBuilder.Sql("""
                UPDATE brain_data.conhecimentos AS k
                SET natureza_de_origem = c.natureza
                FROM (
                    SELECT id_conhecimento, id_espaco_de_conhecimento, id_projeto, MIN(natureza) AS natureza
                    FROM brain_data.candidatos_de_conhecimento
                    WHERE id_conhecimento IS NOT NULL
                    GROUP BY id_conhecimento, id_espaco_de_conhecimento, id_projeto
                    HAVING MIN(natureza) = MAX(natureza) AND MIN(natureza) IN (0, 1, 2)
                ) AS c
                WHERE k.id = c.id_conhecimento
                  AND k.id_espaco_de_conhecimento = c.id_espaco_de_conhecimento
                  AND k.id_projeto IS NOT DISTINCT FROM c.id_projeto;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "natureza",
                schema: "brain_data",
                table: "conhecimentos");

            migrationBuilder.DropColumn(
                name: "natureza_de_origem",
                schema: "brain_data",
                table: "conhecimentos");
        }
    }
}
