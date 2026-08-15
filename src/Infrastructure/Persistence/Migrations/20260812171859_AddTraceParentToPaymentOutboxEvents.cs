using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KartPaymentService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTraceParentToPaymentOutboxEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trace_parent",
                table: "payment_outbox_events",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "trace_parent",
                table: "payment_outbox_events");
        }
    }
}
