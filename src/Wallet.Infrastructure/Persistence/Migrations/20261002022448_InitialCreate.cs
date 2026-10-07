using System;
using Microsoft.EntityFrameworkCore.Migrations;

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
                name: "accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Balance = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.Id);
                    table.CheckConstraint("ck_accounts_balance_non_negative", " \"Type\" <> 'UserWallet' OR \"Balance\" >= 0 ");
                });

            migrationBuilder.CreateTable(
                name: "ledger_transactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_transactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_entries", x => x.Id);
                    table.CheckConstraint("ck_ledger_entries_amount_positive", " \"Amount\" > 0 ");
                    table.ForeignKey(
                        name: "FK_ledger_entries_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ledger_entries_ledger_transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "ledger_transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_accounts_OwnerId",
                table: "accounts",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_AccountId",
                table: "ledger_entries",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_TransactionId",
                table: "ledger_entries",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_transactions_Reference",
                table: "ledger_transactions",
                column: "Reference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ledger_entries");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "ledger_transactions");
        }
    }
}