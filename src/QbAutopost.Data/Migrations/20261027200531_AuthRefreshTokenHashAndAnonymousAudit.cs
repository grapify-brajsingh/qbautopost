using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QbAutopost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuthRefreshTokenHashAndAnonymousAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "user_id",
                table: "user_audit",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_sha256",
                table: "refresh_tokens",
                column: "token_sha256",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_refresh_tokens_token_sha256",
                table: "refresh_tokens");

            migrationBuilder.AlterColumn<string>(
                name: "user_id",
                table: "user_audit",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
