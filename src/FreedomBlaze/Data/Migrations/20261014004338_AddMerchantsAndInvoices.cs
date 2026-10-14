using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FreedomBlaze.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchantsAndInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MerchantId",
                table: "ApiKeys",
                type: "int",
                nullable: true);

            // Keys issued before scopes existed keep what they could do: convert (1). Master keys
            // authorize everything regardless; their column says so too (7 = all scopes).
            migrationBuilder.AddColumn<int>(
                name: "Scopes",
                table: "ApiKeys",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.Sql("UPDATE [ApiKeys] SET [Scopes] = 7 WHERE [IsMaster] = 1;");

            migrationBuilder.CreateTable(
                name: "Merchants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeactivatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Merchants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LightningInvoices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MerchantId = table.Column<int>(type: "int", nullable: false),
                    ApiKeyId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PaymentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Bolt11 = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    ExternalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    AmountSat = table.Column<long>(type: "bigint", nullable: false),
                    BitcoinPrice = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: false),
                    BitcoinPriceUsd = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: true),
                    RateSources = table.Column<int>(type: "int", nullable: false),
                    FiatRateTimestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    QuotedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PaidAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReceivedSat = table.Column<long>(type: "bigint", nullable: true),
                    FeeSat = table.Column<long>(type: "bigint", nullable: true),
                    WebhookUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RequestHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LightningInvoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LightningInvoices_ApiKeys_ApiKeyId",
                        column: x => x.ApiKeyId,
                        principalTable: "ApiKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LightningInvoices_Merchants_MerchantId",
                        column: x => x.MerchantId,
                        principalTable: "Merchants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_MerchantId",
                table: "ApiKeys",
                column: "MerchantId");

            migrationBuilder.CreateIndex(
                name: "IX_LightningInvoices_ApiKeyId",
                table: "LightningInvoices",
                column: "ApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_LightningInvoices_MerchantId_ExternalId",
                table: "LightningInvoices",
                columns: new[] { "MerchantId", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_LightningInvoices_MerchantId_IdempotencyKey",
                table: "LightningInvoices",
                columns: new[] { "MerchantId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LightningInvoices_PaymentHash",
                table: "LightningInvoices",
                column: "PaymentHash",
                unique: true,
                filter: "[PaymentHash] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_ApiKeys_Merchants_MerchantId",
                table: "ApiKeys",
                column: "MerchantId",
                principalTable: "Merchants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApiKeys_Merchants_MerchantId",
                table: "ApiKeys");

            migrationBuilder.DropTable(
                name: "LightningInvoices");

            migrationBuilder.DropTable(
                name: "Merchants");

            migrationBuilder.DropIndex(
                name: "IX_ApiKeys_MerchantId",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "MerchantId",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "Scopes",
                table: "ApiKeys");
        }
    }
}
