using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class IsolamentoPorTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "id_tenant",
                schema: "brain_data",
                table: "espacos_de_conhecimento",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("da17e000-0000-0000-0000-000000000001"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "id_tenant",
                schema: "brain_data",
                table: "espacos_de_conhecimento");
        }
    }
}
