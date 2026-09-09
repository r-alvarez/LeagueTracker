using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeagueTracker.Api.Registry.Migrations
{
    /// <inheritdoc />
    public partial class AccountAddedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddedByUserId",
                table: "Accounts",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddedByUserId",
                table: "Accounts");
        }
    }
}
