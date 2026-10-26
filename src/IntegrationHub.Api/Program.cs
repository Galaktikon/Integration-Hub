using IntegrationHub.Api;
using IntegrationHub.Infrastructure;
using IntegrationHub.Application;
using IntegrationHub.Rest;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

if (!builder.Environment.IsDevelopment() && args.FirstOrDefault() is not "migrate")
    throw new InvalidOperationException("Admin authentication arrives in Phase 5. Run this host only in Development until then.");

var connectionString = builder.Configuration.GetConnectionString("Hub");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:Hub is required.");

builder.Services.AddDbContext<HubDbContext>(options => options.UseNpgsql(connectionString,
    npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "hub")));
builder.Services.AddSingleton(_ => new SecretProtector(
    builder.Configuration["Security:EncryptionKey"] ?? throw new InvalidOperationException("Security:EncryptionKey is required.")));
builder.Services.AddHostedService<WorkerHost>();
builder.Services.AddScoped<SyncProcessor>();
builder.Services.AddScoped<RunLease>();
builder.Services.AddScoped<IOrderConnectorFactory, RestOrderConnectorFactory>();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddCors(options => options.AddPolicy("admin-ui", policy =>
{
    var origin = builder.Configuration["Security:FrontendOrigin"];
    if (!string.IsNullOrWhiteSpace(origin)) policy.WithOrigins(origin).AllowAnyHeader().AllowAnyMethod();
}));

var app = builder.Build();

if (args.FirstOrDefault() == "migrate")
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<HubDbContext>().Database.MigrateAsync();
    return;
}
if (args.FirstOrDefault() == "seed-demo")
{
    if (!app.Environment.IsDevelopment()) throw new InvalidOperationException("Demo seed is development only.");
    using var scope = app.Services.CreateScope();
    await DemoSeed.RunAsync(scope.ServiceProvider, app.Configuration);
    return;
}

app.UseCors("admin-ui");
app.MapOpenApi();
app.MapGet("/api/v1/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/api/v1/health/ready", async (HubDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken) &&
    !(await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any()
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapConfigurationEndpoints();
app.MapRunEndpoints();
app.Run();

public partial class Program;
