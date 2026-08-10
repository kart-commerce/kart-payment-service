using Kart.Shared.Auditing;
using Kart.Shared.Configuration;
using Kart.Shared.ErrorHandling;
using Kart.Shared.Observability;
using KartPaymentService.Api.Security;
using KartPaymentService.Application;
using KartPaymentService.Application.Common.Exceptions;
using KartPaymentService.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// kart-conventions.md Configuration Management: GlobalConfig external-secrets-file bootstrap,
// shared across every service - never reimplemented per service. See appsettings.Local.json.example.
// Must run before AddKartObservability below, since observability's own LogFile:Directory setting
// is read from the layered-in GlobalConfig file too.
builder.AddKartGlobalConfig("kart-payment-service");

// kart-conventions.md Observability section: Serilog + OpenTelemetry SDK behind one DI call.
// Payment is one of the platform's 100%-trace-coverage services (requirement-spec.md's
// Observability NFR row - a required Order Saga participant on the "never double-charge" path).
// No extra sampler configuration is needed to get there: the OpenTelemetry SDK's own default
// (ParentBased(AlwaysOnSampler)) already samples 100% of traces unless a service explicitly
// dials it down, which this one deliberately does not.
builder.AddKartObservability("kart-payment-service");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddPaymentAuthentication();
builder.Services.AddAuthorization();

// kart-conventions.md Error Handling section: the single global exception handler +
// ProblemDetails factory, wired once via the shared package - no local try/catch for translation
// anywhere in this service's handler/controller/domain code.
builder.Services.AddKartErrorHandling(options => options
    .Map<DuplicateKeyException>(StatusCodes.Status409Conflict, "conflict")
    .Map<ConcurrencyConflictException>(StatusCodes.Status412PreconditionFailed, "stale_version"));

// No dedicated audit sink beyond the createdBy/updatedBy columns already stamped inline at each
// PostgreSQL write site (BRD §24.3) - registers the safe NullAuditLogWriter default.
builder.Services.AddKartAuditing();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Per-HTTP-request Information log (method/path/status/elapsed) - registered outermost, wrapping
// UseKartErrorHandling below, so this always logs the *final* status code a client actually
// received.
app.UseSerilogRequestLogging();

// The single global error handler - every unhandled exception is translated to the platform's
// ProblemDetails envelope and logged here.
app.UseKartErrorHandling();

app.UseHttpsRedirection();

// Routing must run before authentication here: GatewaySignatureAuthenticationHandler reads the
// `{gateway}` route value to resolve which signing secret to verify against.
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Prometheus scrape target (observability-standards.md's mandatory /metrics).
app.MapPrometheusScrapingEndpoint();

app.MapControllers();

app.Run();

// Exposed for WebApplicationFactory<Program> in IntegrationTests/ContractTests.
public partial class Program
{
}
