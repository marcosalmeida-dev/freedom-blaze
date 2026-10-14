using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FreedomBlaze.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLightningBackends : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LightningBackends",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MerchantId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Url = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    ProtectedPassword = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RetiredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NodeId = table.Column<string>(type: "nvarchar(66)", maxLength: 66, nullable: true),
                    Chain = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CheckedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Reachable = table.Column<bool>(type: "bit", nullable: true),
                    CheckError = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LightningBackends", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LightningBackends_Merchants_MerchantId",
                        column: x => x.MerchantId,
                        principalTable: "Merchants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LightningBackends_MerchantId",
                table: "LightningBackends",
                column: "MerchantId",
                unique: true,
                filter: "[IsActive] = 1");

            // Merchants that existed before backends were all served by the operator's node; they keep it.
            // Its node id is pinned on first use. Merchants registered later start with no backend.
            migrationBuilder.Sql(
                "INSERT INTO [LightningBackends] ([MerchantId], [Kind], [IsActive], [CreatedAtUtc]) " +
                "SELECT [Id], 0, 1, SYSUTCDATETIME() FROM [Merchants];");

            migrationBuilder.AddColumn<int>(
                name: "BackendId",
                table: "LightningInvoices",
                type: "int",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE [i] SET [BackendId] = [b].[Id] FROM [LightningInvoices] AS [i] " +
                "INNER JOIN [LightningBackends] AS [b] ON [b].[MerchantId] = [i].[MerchantId];");

            migrationBuilder.AlterColumn<int>(
                name: "BackendId",
                table: "LightningInvoices",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LightningInvoices_BackendId",
                table: "LightningInvoices",
                column: "BackendId");

            migrationBuilder.AddForeignKey(
                name: "FK_LightningInvoices_LightningBackends_BackendId",
                table: "LightningInvoices",
                column: "BackendId",
                principalTable: "LightningBackends",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LightningInvoices_LightningBackends_BackendId",
                table: "LightningInvoices");

            migrationBuilder.DropTable(
                name: "LightningBackends");

            migrationBuilder.DropIndex(
                name: "IX_LightningInvoices_BackendId",
                table: "LightningInvoices");

            migrationBuilder.DropColumn(
                name: "BackendId",
                table: "LightningInvoices");
        }
    }
}
