using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Flint.Migrations
{
    /// <inheritdoc />
    public partial class StablecoinQuotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StablecoinQuotes",
                schema: "BTCPayServer.Plugins.Flint",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    InvoiceId = table.Column<string>(type: "text", nullable: false),
                    PaymentMethodId = table.Column<string>(type: "text", nullable: false),
                    Chain = table.Column<string>(type: "text", nullable: false),
                    ChainId = table.Column<string>(type: "text", nullable: true),
                    Asset = table.Column<string>(type: "text", nullable: false),
                    ContractAddress = table.Column<string>(type: "text", nullable: true),
                    Decimals = table.Column<int>(type: "integer", nullable: false),
                    DepositAddress = table.Column<string>(type: "text", nullable: false),
                    DepositBaseUnits = table.Column<string>(type: "text", nullable: false),
                    AskedBaseUnits = table.Column<string>(type: "text", nullable: false),
                    PaymentRequest = table.Column<string>(type: "text", nullable: false),
                    DueAmount = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    FeeAmount = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    ExpectedReceivedBaseUnits = table.Column<string>(type: "text", nullable: false),
                    DestinationAsset = table.Column<string>(type: "text", nullable: false),
                    ServiceFeeBaseUnits = table.Column<string>(type: "text", nullable: false),
                    ServiceFeeAsset = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SdkPaymentId = table.Column<string>(type: "text", nullable: true),
                    PaidBaseUnits = table.Column<string>(type: "text", nullable: true),
                    DeliveredBaseUnits = table.Column<string>(type: "text", nullable: true),
                    ExternalTxHash = table.Column<string>(type: "text", nullable: true),
                    ProviderOrderId = table.Column<string>(type: "text", nullable: true),
                    ProviderQuoteId = table.Column<string>(type: "text", nullable: true),
                    SettledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreditedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StablecoinQuotes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StablecoinQuotes_InvoiceId",
                schema: "BTCPayServer.Plugins.Flint",
                table: "StablecoinQuotes",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_StablecoinQuotes_SdkPaymentId",
                schema: "BTCPayServer.Plugins.Flint",
                table: "StablecoinQuotes",
                column: "SdkPaymentId",
                unique: true,
                filter: "\"SdkPaymentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StablecoinQuotes_StoreId_ExpiresAt_Open",
                schema: "BTCPayServer.Plugins.Flint",
                table: "StablecoinQuotes",
                columns: new[] { "StoreId", "ExpiresAt" },
                filter: "\"SdkPaymentId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StablecoinQuotes_StoreId_SettledAt_Uncredited",
                schema: "BTCPayServer.Plugins.Flint",
                table: "StablecoinQuotes",
                columns: new[] { "StoreId", "SettledAt" },
                filter: "\"SdkPaymentId\" IS NOT NULL AND \"CreditedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StablecoinQuotes",
                schema: "BTCPayServer.Plugins.Flint");
        }
    }
}
