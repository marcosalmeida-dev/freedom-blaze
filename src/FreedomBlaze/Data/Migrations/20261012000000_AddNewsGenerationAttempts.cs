using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FreedomBlaze.Data.Migrations
{
    public partial class AddNewsGenerationAttempts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NewsGenerationAttempts",
                columns: table => new
                {
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LastAttemptUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastCompletedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsRunning = table.Column<bool>(type: "bit", nullable: false),
                    LastFailure = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_NewsGenerationAttempts", row => row.Date));
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "NewsGenerationAttempts");
        }
    }
}
