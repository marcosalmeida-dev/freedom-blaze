using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FreedomBlaze.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyIsMaster : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsMaster",
                table: "ApiKeys",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsMaster",
                table: "ApiKeys");
        }
    }
}
