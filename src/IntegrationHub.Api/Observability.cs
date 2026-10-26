using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace IntegrationHub.Api;

public static class HubTelemetry
{
    public static readonly Meter Meter = new("IntegrationHub", "1.0.0");
    public static readonly ActivitySource Activities = new("IntegrationHub");
    public static readonly Counter<long> HttpRequests = Meter.CreateCounter<long>("integration_hub.http.requests");
    public static readonly Histogram<double> HttpDuration = Meter.CreateHistogram<double>("integration_hub.http.duration", "ms");
    public static readonly Counter<long> RunClaims = Meter.CreateCounter<long>("integration_hub.run.claims");
    public static readonly Counter<long> RunFinished = Meter.CreateCounter<long>("integration_hub.run.finished");
    public static readonly Histogram<double> RunDuration = Meter.CreateHistogram<double>("integration_hub.run.duration", "s");
}

public static class ObservabilityMiddleware
{
    public static void UseHubObservability(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var supplied = context.Request.Headers["X-Correlation-ID"].ToString();
            var correlationId = ValidCorrelationId(supplied) ? supplied : Guid.NewGuid().ToString("N");
            context.TraceIdentifier = correlationId;
            context.Response.Headers["X-Correlation-ID"] = correlationId;
            using var scope = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("IntegrationHub.Request").BeginScope(new Dictionary<string, object>
                {
                    ["CorrelationId"] = correlationId
                });
            using var activity = HubTelemetry.Activities.StartActivity("http.request");
            activity?.SetTag("correlation.id", correlationId);
            var started = Stopwatch.GetTimestamp();
            try { await next(context); }
            finally
            {
                var route = context.GetEndpoint()?.DisplayName ?? "unmatched";
                var tags = new TagList { { "method", context.Request.Method }, { "route", route },
                    { "status_code", context.Response.StatusCode } };
                HubTelemetry.HttpRequests.Add(1, tags);
                HubTelemetry.HttpDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
            }
        });
    }

    private static bool ValidCorrelationId(string value) => value.Length is > 0 and <= 64 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
