using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SolanaWalletService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deposit_cursors",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    signature = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    slot = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deposit_cursors", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "deposit_notifications",
                columns: table => new
                {
                    signature = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    slot = table.Column<long>(type: "bigint", nullable: false),
                    amount_lamports = table.Column<long>(type: "bigint", nullable: false),
                    from_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deposit_notifications", x => x.signature);
                });

            migrationBuilder.CreateTable(
                name: "payout_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    paused = table.Column<bool>(type: "boolean", nullable: false),
                    notify_on_success = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payout_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payouts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    destination_raw = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    destination_friendly = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    amount_lamports = table.Column<long>(type: "bigint", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    signature = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    signed_transaction = table.Column<string>(type: "text", nullable: true),
                    recent_blockhash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_valid_block_height = table.Column<long>(type: "bigint", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    resign_count = table.Column<int>(type: "integer", nullable: false),
                    sign_failures = table.Column<int>(type: "integer", nullable: false),
                    last_send = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_broadcast_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fee_reserve_lamports = table.Column<long>(type: "bigint", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    insufficient_notified = table.Column<bool>(type: "boolean", nullable: false),
                    terminal_notified = table.Column<bool>(type: "boolean", nullable: false),
                    success_notified = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payouts", x => x.id);
                    table.CheckConstraint("ck_payouts_amount_positive", "amount_lamports > 0");
                });

            migrationBuilder.CreateTable(
                name: "payout_tracked_signatures",
                columns: table => new
                {
                    payout_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    signature = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    recent_blockhash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_valid_block_height = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payout_tracked_signatures", x => new { x.payout_id, x.ordinal });
                    table.ForeignKey(
                        name: "FK_payout_tracked_signatures_payouts_payout_id",
                        column: x => x.payout_id,
                        principalTable: "payouts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payouts_created_at_utc",
                table: "payouts",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_payouts_status_created_at_utc",
                table: "payouts",
                columns: new[] { "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_payouts_request_id",
                table: "payouts",
                column: "request_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deposit_cursors");

            migrationBuilder.DropTable(
                name: "deposit_notifications");

            migrationBuilder.DropTable(
                name: "payout_settings");

            migrationBuilder.DropTable(
                name: "payout_tracked_signatures");

            migrationBuilder.DropTable(
                name: "payouts");
        }
    }
}
