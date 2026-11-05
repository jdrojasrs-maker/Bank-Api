using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bank.Transaction.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCorrelationIdUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "CorrelationId",
                table: "Transaction",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.CreateIndex(
                name: "IX_Transaction_CorrelationId",
                table: "Transaction",
                column: "CorrelationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transaction_CorrelationId",
                table: "Transaction");

            migrationBuilder.AlterColumn<string>(
                name: "CorrelationId",
                table: "Transaction",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);
        }
    }
}
