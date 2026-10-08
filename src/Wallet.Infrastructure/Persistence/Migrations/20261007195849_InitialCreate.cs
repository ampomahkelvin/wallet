using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Wallet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wallets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Balance = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallets", x => x.Id);
                    table.UniqueConstraint("AK_wallets_Id_Currency", x => new { x.Id, x.Currency });
                    table.CheckConstraint("ck_wallets_balance_non_negative", " \"Kind\" <> 1 OR \"Balance\" >= 0 ");
                    table.CheckConstraint("ck_wallets_owner_matches_kind", " (\"Kind\" = 1) = (\"OwnerId\" IS NOT NULL) ");
                });

            migrationBuilder.CreateTable(
                name: "transfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    SourceWalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationWalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    ReversesTransferId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfers", x => x.Id);
                    table.CheckConstraint("ck_transfers_amount_positive", " \"Amount\" > 0 ");
                    table.CheckConstraint("ck_transfers_distinct_wallets", " \"SourceWalletId\" <> \"DestinationWalletId\" ");
                    table.CheckConstraint("ck_transfers_reversal_link", " (\"Type\" = 4) = (\"ReversesTransferId\" IS NOT NULL) ");
                    table.ForeignKey(
                        name: "FK_transfers_wallets_DestinationWalletId_Currency",
                        columns: x => new { x.DestinationWalletId, x.Currency },
                        principalTable: "wallets",
                        principalColumns: new[] { "Id", "Currency" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_transfers_wallets_SourceWalletId_Currency",
                        columns: x => new { x.SourceWalletId, x.Currency },
                        principalTable: "wallets",
                        principalColumns: new[] { "Id", "Currency" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    TransferId = table.Column<Guid>(type: "uuid", nullable: false),
                    WalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    BalanceAfter = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_entries", x => x.Id);
                    table.CheckConstraint("ck_ledger_entries_amount_nonzero", " \"Amount\" <> 0 ");
                    table.ForeignKey(
                        name: "FK_ledger_entries_transfers_TransferId",
                        column: x => x.TransferId,
                        principalTable: "transfers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ledger_entries_wallets_WalletId",
                        column: x => x.WalletId,
                        principalTable: "wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_TransferId_WalletId",
                table: "ledger_entries",
                columns: new[] { "TransferId", "WalletId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_WalletId_Id",
                table: "ledger_entries",
                columns: new[] { "WalletId", "Id" },
                descending: new[] { true, false });

            migrationBuilder.CreateIndex(
                name: "IX_transfers_DestinationWalletId_Currency",
                table: "transfers",
                columns: new[] { "DestinationWalletId", "Currency" });

            migrationBuilder.CreateIndex(
                name: "IX_transfers_ReversesTransferId",
                table: "transfers",
                column: "ReversesTransferId",
                unique: true,
                filter: " \"ReversesTransferId\" IS NOT NULL ");

            migrationBuilder.CreateIndex(
                name: "IX_transfers_SourceWalletId_Currency",
                table: "transfers",
                columns: new[] { "SourceWalletId", "Currency" });

            migrationBuilder.CreateIndex(
                name: "IX_wallets_Currency",
                table: "wallets",
                column: "Currency",
                unique: true,
                filter: " \"Kind\" = 2 ");

            migrationBuilder.CreateIndex(
                name: "IX_wallets_OwnerId_Currency",
                table: "wallets",
                columns: new[] { "OwnerId", "Currency" },
                unique: true,
                filter: " \"Kind\" = 1 ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ledger_entries");

            migrationBuilder.DropTable(
                name: "transfers");

            migrationBuilder.DropTable(
                name: "wallets");
        }
    }
}
