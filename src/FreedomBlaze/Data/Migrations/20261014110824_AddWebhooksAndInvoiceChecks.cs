using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FreedomBlaze.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhooksAndInvoiceChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextCheckAtUtc",
                table: "LightningInvoices",
                type: "datetimeoffset",
                nullable: true);

            // Invoices already pending are confirmed in the background from the first round on.
            migrationBuilder.Sql("UPDATE [LightningInvoices] SET [NextCheckAtUtc] = SYSUTCDATETIME() WHERE [Status] = 1;");

            migrationBuilder.CreateTable(
                name: "WebhookEndpoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MerchantId = table.Column<int>(type: "int", nullable: false),
                    Url = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ProtectedSecret = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DisabledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookEndpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookEndpoints_Merchants_MerchantId",
                        column: x => x.MerchantId,
                        principalTable: "Merchants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WebhookDeliveries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MerchantId = table.Column<int>(type: "int", nullable: false),
                    EndpointId = table.Column<int>(type: "int", nullable: false),
                    EventId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    InvoiceId = table.Column<long>(type: "bigint", nullable: true),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClaimedUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastStatusCode = table.Column<int>(type: "int", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    DeliveredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookDeliveries_LightningInvoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "LightningInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WebhookDeliveries_Merchants_MerchantId",
                        column: x => x.MerchantId,
                        principalTable: "Merchants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WebhookDeliveries_WebhookEndpoints_EndpointId",
                        column: x => x.EndpointId,
                        principalTable: "WebhookEndpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LightningInvoices_NextCheckAtUtc",
                table: "LightningInvoices",
                column: "NextCheckAtUtc",
                filter: "[NextCheckAtUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_EndpointId",
                table: "WebhookDeliveries",
                column: "EndpointId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_EventId",
                table: "WebhookDeliveries",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_InvoiceId",
                table: "WebhookDeliveries",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_MerchantId_Id",
                table: "WebhookDeliveries",
                columns: new[] { "MerchantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_NextAttemptAtUtc",
                table: "WebhookDeliveries",
                column: "NextAttemptAtUtc",
                filter: "[NextAttemptAtUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEndpoints_MerchantId",
                table: "WebhookEndpoints",
                column: "MerchantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebhookDeliveries");

            migrationBuilder.DropTable(
                name: "WebhookEndpoints");

            migrationBuilder.DropIndex(
                name: "IX_LightningInvoices_NextCheckAtUtc",
                table: "LightningInvoices");

            migrationBuilder.DropColumn(
                name: "NextCheckAtUtc",
                table: "LightningInvoices");
        }
    }
}
