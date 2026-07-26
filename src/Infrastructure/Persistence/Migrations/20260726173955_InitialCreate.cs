using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KartPaymentService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gateway_webhook_events",
                columns: table => new
                {
                    gateway_event_id = table.Column<string>(type: "text", nullable: false),
                    gateway = table.Column<string>(type: "text", nullable: false),
                    payment_intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    applied_transition = table.Column<string>(type: "text", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_webhook_events", x => x.gateway_event_id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    endpoint = table.Column<string>(type: "text", nullable: false),
                    request_payload_hash = table.Column<string>(type: "text", nullable: false),
                    stored_response = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_keys", x => new { x.idempotency_key, x.endpoint });
                });

            migrationBuilder.CreateTable(
                name: "payment_intents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<string>(type: "text", nullable: false),
                    gateway_token = table.Column<string>(type: "text", nullable: false),
                    txn_id = table.Column<string>(type: "text", nullable: true),
                    captured_amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    chargeback_id = table.Column<string>(type: "text", nullable: true),
                    chargeback_amount = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    chargeback_reason = table.Column<string>(type: "text", nullable: true),
                    chargeback_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_intents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payment_outbox_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_outbox_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "refunds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refunds", x => x.id);
                    table.ForeignKey(
                        name: "FK_refunds_payment_intents_payment_intent_id",
                        column: x => x.payment_intent_id,
                        principalTable: "payment_intents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_gateway_webhook_events_intent",
                table: "gateway_webhook_events",
                columns: new[] { "payment_intent_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "idx_idempotency_keys_expiry",
                table: "idempotency_keys",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_payment_intents_order_id",
                table: "payment_intents",
                column: "order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_payment_outbox_unpublished",
                table: "payment_outbox_events",
                column: "occurred_at",
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "idx_refunds_intent_succeeded",
                table: "refunds",
                column: "payment_intent_id",
                filter: "status = 'succeeded'");

            // database-design.md: CHECK constraints the DDL sample states inline but EF Core's
            // fluent API has no first-class support for - added here verbatim.
            migrationBuilder.Sql(
                "ALTER TABLE payment_intents ADD CONSTRAINT ck_payment_intents_status CHECK (status IN ('pending', 'completed', 'failed', 'disputed'));");
            migrationBuilder.Sql(
                "ALTER TABLE refunds ADD CONSTRAINT ck_refunds_status CHECK (status IN ('pending', 'succeeded', 'failed'));");
            migrationBuilder.Sql(
                "ALTER TABLE idempotency_keys ADD CONSTRAINT ck_idempotency_keys_endpoint CHECK (endpoint IN ('charge', 'refund'));");

            // database-design.md: enforces PaymentIntentStatus's monotonic transition rule as a
            // database-level backstop, so a coding bug can't silently violate it even if the
            // application-layer guard in Domain/Payments/PaymentIntent.cs is ever bypassed.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION enforce_payment_intent_status_transition() RETURNS trigger AS $$
                BEGIN
                    IF OLD.status IN ('completed', 'failed') AND NEW.status NOT IN (OLD.status, 'disputed') THEN
                        RAISE EXCEPTION 'illegal PaymentIntentStatus transition: % -> %', OLD.status, NEW.status;
                    END IF;
                    IF OLD.status = 'disputed' AND NEW.status <> 'disputed' THEN
                        RAISE EXCEPTION 'illegal PaymentIntentStatus transition: disputed is terminal for this minimal (ADR-0012) scope';
                    END IF;
                    IF NEW.status = 'disputed' AND OLD.status <> 'completed' THEN
                        RAISE EXCEPTION 'disputed is only reachable from completed (a charge that never completed cannot be charged back)';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_payment_intents_status_guard
                    BEFORE UPDATE OF status ON payment_intents
                    FOR EACH ROW EXECUTE FUNCTION enforce_payment_intent_status_transition();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_payment_intents_status_guard ON payment_intents;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS enforce_payment_intent_status_transition();");

            migrationBuilder.DropTable(
                name: "gateway_webhook_events");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "payment_outbox_events");

            migrationBuilder.DropTable(
                name: "refunds");

            migrationBuilder.DropTable(
                name: "payment_intents");
        }
    }
}
