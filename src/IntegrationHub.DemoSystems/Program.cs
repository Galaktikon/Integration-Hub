using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntegrationHub.Application;
using IntegrationHub.DemoSystems;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
var connection = builder.Configuration.GetConnectionString("Demo")
    ?? throw new InvalidOperationException("ConnectionStrings:Demo is required.");
var apiKey = builder.Configuration["Demo:ApiKey"] ?? throw new InvalidOperationException("Demo:ApiKey is required.");
var adminKey = builder.Configuration["Demo:AdminKey"] ?? throw new InvalidOperationException("Demo:AdminKey is required.");
if (apiKey == adminKey) throw new InvalidOperationException("Demo API and admin keys must differ.");
builder.Services.AddDbContext<DemoDbContext>(options => options.UseNpgsql(connection,
    npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "demo")));
builder.Services.AddSingleton<Faults>();
builder.Services.AddOpenApi();
var app = builder.Build();

if (args.FirstOrDefault() == "migrate")
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<DemoDbContext>().Database.MigrateAsync();
    return;
}
if (args.FirstOrDefault() == "seed")
{
    using var scope = app.Services.CreateScope();
    await SeedAsync(scope.ServiceProvider.GetRequiredService<DemoDbContext>());
    return;
}

app.MapOpenApi();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (DemoDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) && !(await db.Database.GetPendingMigrationsAsync(ct)).Any()
        ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));

var commerce = app.MapGroup("/commerce").AddEndpointFilter(async (context, next) =>
    Authorized(context.HttpContext, apiKey) ? await next(context) : Results.Unauthorized());
commerce.MapGet("/capabilities", () => Results.Ok(new { schemaVersion = 1, role = "source", entityType = "orders" }));
commerce.MapGet("/orders", async (DemoDbContext db, Faults faults, int? pageSize,
    string? cursor, string? snapshot, CancellationToken ct) =>
{
    if (pageSize is < 1 or > 500 || snapshot is not (null or "seed-v1") ||
        !TryCursor(cursor, out var offset)) return Results.BadRequest(new { code = "invalid_cursor_or_page_size" });
    var fault = faults.Take("source");
    var faultResult = await ApplyFaultAsync(fault, ct);
    if (faultResult is not null) return faultResult;
    var size = pageSize ?? 100;
    var rows = await db.SourceOrders.AsNoTracking().OrderBy(x => x.Sequence).Skip(offset).Take(size + 1).ToListAsync(ct);
    var items = rows.Take(size).Select(x => JsonSerializer.SerializeToElement(new CommerceOrder(
        x.SourceId, x.OrderNumber, new CommerceCustomer(x.CustomerEmail), x.Total, x.PlacedAt), new JsonSerializerOptions(JsonSerializerDefaults.Web))).ToList();
    if (fault?.Mode == "malformedRecord" && items.Count > 0)
        items[0] = JsonSerializer.SerializeToElement(new { id = rows[0].SourceId, orderNumber = rows[0].OrderNumber,
            customer = new { email = "invalid" }, placedAt = rows[0].PlacedAt });
    return Results.Ok(new CommercePage(items, "seed-v1", rows.Count > size ? EncodeCursor(offset + size) : null));
});

var erp = app.MapGroup("/erp").AddEndpointFilter(async (context, next) =>
    Authorized(context.HttpContext, apiKey) ? await next(context) : Results.Unauthorized());
erp.MapGet("/capabilities", () => Results.Ok(new { schemaVersion = 1, role = "destination", entityType = "orders" }));
erp.MapPut("/orders/{integrationId:guid}/{sourceRecordId}", async (HttpContext http, DemoDbContext db,
    Faults faults, Guid integrationId, string sourceRecordId, ErpOrder order, CancellationToken ct) =>
{
    if (integrationId == Guid.Empty || sourceRecordId.Length is < 1 or > 80 ||
        !string.Equals(http.Request.Headers["Idempotency-Key"], $"{integrationId:N}:{sourceRecordId}", StringComparison.Ordinal) ||
        string.IsNullOrWhiteSpace(order.ExternalOrderId) || order.ExternalOrderId.Length > 80 ||
        string.IsNullOrWhiteSpace(order.CustomerEmail) || order.CustomerEmail.Length > 320 ||
        order.Amount < 0)
        return Results.BadRequest(new { code = "invalid_order_or_idempotency_key" });
    var fault = faults.Take("destination");
    if (fault?.Mode != "lostResponse")
    {
        var faultResult = await ApplyFaultAsync(fault, ct);
        if (faultResult is not null) return faultResult;
    }
    var key = $"{integrationId:N}:{sourceRecordId}";
    var id = Guid.NewGuid();
    var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO demo.destination_order ("Id", "IntegrationId", "SourceRecordId", "IdempotencyKey", "ExternalOrderId", "CustomerEmail", "Amount", "PlacedAtUtc")
        VALUES ({id}, {integrationId}, {sourceRecordId}, {key}, {order.ExternalOrderId}, {order.CustomerEmail}, {order.Amount}, {order.PlacedAtUtc})
        ON CONFLICT ("IntegrationId", "SourceRecordId") DO NOTHING
        """, ct);
    if (inserted == 1)
    {
        if (fault?.Mode == "lostResponse") return await ApplyFaultAsync(fault, ct) ?? Results.StatusCode(503);
        return Results.Created($"/erp/orders/{integrationId}/{sourceRecordId}", new ErpWriteResult(id, "created"));
    }
    var existing = await db.DestinationOrders.SingleAsync(x => x.IntegrationId == integrationId && x.SourceRecordId == sourceRecordId, ct);
    var changed = existing.ExternalOrderId != order.ExternalOrderId || existing.CustomerEmail != order.CustomerEmail ||
        existing.Amount != order.Amount || existing.PlacedAtUtc != order.PlacedAtUtc;
    if (changed)
    {
        existing.ExternalOrderId = order.ExternalOrderId;
        existing.CustomerEmail = order.CustomerEmail;
        existing.Amount = order.Amount;
        existing.PlacedAtUtc = order.PlacedAtUtc;
        await db.SaveChangesAsync(ct);
    }
    if (fault?.Mode == "lostResponse") return await ApplyFaultAsync(fault, ct) ?? Results.StatusCode(503);
    return Results.Ok(new ErpWriteResult(existing.Id, changed ? "updated" : "unchanged"));
});

var admin = app.MapGroup("/__admin").AddEndpointFilter(async (context, next) =>
    Authorized(context.HttpContext, adminKey) ? await next(context) : Results.Unauthorized());
admin.MapPost("/faults", (FaultRequest request, Faults faults) =>
    faults.Set(request) ? Results.NoContent() : Results.BadRequest(new { code = "invalid_fault" }));
admin.MapDelete("/faults", (Faults faults) => { faults.Clear(); return Results.NoContent(); });
admin.MapPost("/seed", async (DemoDbContext db, CancellationToken ct) =>
{ await SeedAsync(db, ct); return Results.Ok(new { sourceOrders = 125 }); });
admin.MapPost("/reset", async (HttpContext http, DemoDbContext db, Faults faults, CancellationToken ct) =>
{
    if (http.Request.Headers["X-Confirm-Reset"].ToString() != "reset-synthetic-demo")
        return Results.BadRequest(new { code = "reset_confirmation_required" });
    var removed = await db.DestinationOrders.ExecuteDeleteAsync(ct);
    faults.Clear();
    await SeedAsync(db, ct);
    return Results.Ok(new { destinationOrdersRemoved = removed, sourceOrders = 125 });
});
admin.MapGet("/orders/count", async (DemoDbContext db, CancellationToken ct) =>
    Results.Ok(new { count = await db.DestinationOrders.CountAsync(ct) }));
app.Run();

static bool Authorized(HttpContext http, string key)
{
    var supplied = http.Request.Headers["X-Api-Key"].ToString();
    return supplied.Length == key.Length && CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(key));
}
static async Task SeedAsync(DemoDbContext db, CancellationToken ct = default)
{
    if (await db.SourceOrders.AnyAsync(ct)) return;
    for (var i = 1; i <= 125; i++) db.SourceOrders.Add(new SourceOrder
    {
        Sequence = i, SourceId = $"commerce-{i:0000}", OrderNumber = $"ORD-{i:0000}",
        CustomerEmail = $"customer{i:0000}@example.test", Total = 10 + i * 1.25m,
        PlacedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(i)
    });
    await db.SaveChangesAsync(ct);
}
static bool TryCursor(string? cursor, out int offset)
{
    offset = 0;
    if (cursor is null) return true;
    try { return int.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)), out offset) && offset >= 0; }
    catch (FormatException) { return false; }
}
static string EncodeCursor(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString()));
static async Task<IResult?> ApplyFaultAsync(FaultRequest? fault, CancellationToken ct)
{
    if (fault is null) return null;
    return fault.Mode switch
    {
        "429" => Results.StatusCode(429),
        "503" => Results.StatusCode(503),
        "invalidJson" => Results.Text("{invalid", "application/json"),
        "timeout" or "lostResponse" => await DelayAsync(fault.DelayMilliseconds, ct),
        _ => null
    };
}
static async Task<IResult> DelayAsync(int milliseconds, CancellationToken ct)
{
    await Task.Delay(milliseconds, ct);
    return Results.StatusCode(503);
}

public partial class Program;
