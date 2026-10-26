using IntegrationHub.Api;
using IntegrationHub.Infrastructure;
using IntegrationHub.Application;
using IntegrationHub.Rest;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
if (builder.Environment.IsDevelopment()) builder.Logging.AddConsole();
else builder.Logging.AddJsonConsole();

var connectionString = builder.Configuration.GetConnectionString("Hub");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:Hub is required.");

builder.Services.AddDbContext<HubDbContext>(options => options.UseNpgsql(connectionString,
    npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "hub")));
builder.Services.AddSingleton(_ => new SecretProtector(
    builder.Configuration["Security:EncryptionKey"] ?? throw new InvalidOperationException("Security:EncryptionKey is required.")));
builder.Services.AddHostedService<WorkerHost>();
builder.Services.AddHostedService<ScheduleWorker>();
builder.Services.AddScoped<SyncProcessor>();
builder.Services.AddScoped<RunLease>();
builder.Services.AddSingleton<AdminAuth>();
builder.Services.AddScoped<IOrderConnectorFactory>(_ => new RestOrderConnectorFactory(!builder.Environment.IsDevelopment()));
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options => options.AddPolicy("admin-login", context =>
    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0, AutoReplenishment = true })));
builder.Services.AddCors(options => options.AddPolicy("admin-ui", policy =>
{
    var origin = builder.Configuration["Security:FrontendOrigin"];
    if (!string.IsNullOrWhiteSpace(origin)) policy.WithOrigins(origin).AllowAnyHeader().AllowAnyMethod()
        .WithExposedHeaders("ETag", "Location", "X-Correlation-ID");
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

app.UseHubObservability();
app.UseCors("admin-ui");
app.UseRateLimiter();
app.UseAdminAuth();
app.MapOpenApi();
app.MapGet("/api/v1/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/api/v1/health/ready", async (HubDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken) &&
    !(await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any()
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapConfigurationEndpoints();
app.MapConnectorTestEndpoint();
app.MapRunEndpoints();
app.MapScheduleEndpoints();
app.MapOperationalMetrics();
app.MapDemoFaultEndpoint();
app.Run();

public partial class Program;
