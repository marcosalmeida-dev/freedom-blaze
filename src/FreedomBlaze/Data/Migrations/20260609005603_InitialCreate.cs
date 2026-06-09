using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FreedomBlaze.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NewsDays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ArticleCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsDays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NewsArticles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NewsDayId = table.Column<int>(type: "int", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SourceRegion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ArticleUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ThumbnailUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    PublishedDate = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsArticles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NewsArticles_NewsDays_NewsDayId",
                        column: x => x.NewsDayId,
                        principalTable: "NewsDays",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticles_NewsDayId_Position",
                table: "NewsArticles",
                columns: new[] { "NewsDayId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsDays_Date",
                table: "NewsDays",
                column: "Date",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NewsArticles");

            migrationBuilder.DropTable(
                name: "NewsDays");
        }
    }
}
