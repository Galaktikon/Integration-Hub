var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/", () => Results.Ok(new { service = "IntegrationHub.DemoSystems", phase = 1 }));
app.Run();
