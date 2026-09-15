using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Flint.Migrations
{
    /// <inheritdoc />
    public partial class UnilateralExitRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UnilateralExitRecords",
                schema: "BTCPayServer.Plugins.Flint",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DestinationAddress = table.Column<string>(type: "text", nullable: false),
                    FeeRateSatPerVbyte = table.Column<long>(type: "bigint", nullable: false),
                    LeafIdsJson = table.Column<string>(type: "text", nullable: false),
                    RecoverableValueSat = table.Column<long>(type: "bigint", nullable: false),
                    TotalFeeSat = table.Column<long>(type: "bigint", nullable: false),
                    SingleUtxoFundingSat = table.Column<long>(type: "bigint", nullable: false),
                    FundingAddress = table.Column<string>(type: "text", nullable: false),
                    FundingKeyIndex = table.Column<long>(type: "bigint", nullable: false),
                    FundingUtxosJson = table.Column<string>(type: "text", nullable: true),
                    TransactionsJson = table.Column<string>(type: "text", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnilateralExitRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UnilateralExitRecords_StoreId_CreatedUtc",
                schema: "BTCPayServer.Plugins.Flint",
                table: "UnilateralExitRecords",
                columns: new[] { "StoreId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UnilateralExitRecords_StoreId_Status",
                schema: "BTCPayServer.Plugins.Flint",
                table: "UnilateralExitRecords",
                columns: new[] { "StoreId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_UnilateralExitRecords_ActiveStore",
                schema: "BTCPayServer.Plugins.Flint",
                table: "UnilateralExitRecords",
                column: "StoreId",
                unique: true,
                filter: "\"Status\" IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UnilateralExitRecords",
                schema: "BTCPayServer.Plugins.Flint");
        }
    }
}
