using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AutoriaDasFontes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 0 = AutoriaDaFonte.Desconhecida: fontes existentes não recebem autoria deduzida (AD-59 D3, #264).
            migrationBuilder.AddColumn<int>(
                name: "autoria",
                schema: "brain_data",
                table: "documentos_fonte",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "autoria",
                schema: "brain_data",
                table: "documentos_fonte");
        }
    }
}
