namespace IntegrationHub.DemoSystems;

public sealed record FaultRequest(string Target, string Mode, int Remaining, int DelayMilliseconds = 30000);

public sealed class Faults
{
    private readonly object gate = new();
    private FaultRequest? current;

    public bool Set(FaultRequest request)
    {
        if (request.Target is not ("source" or "destination") ||
            request.Mode is not ("429" or "503" or "timeout" or "invalidJson" or "malformedRecord" or "lostResponse") ||
            request.Remaining is < 1 or > 100 || request.DelayMilliseconds is < 1 or > 60000 ||
            request.Mode == "malformedRecord" && request.Target != "source" ||
            request.Mode == "lostResponse" && request.Target != "destination") return false;
        lock (gate) current = request;
        return true;
    }

    public FaultRequest? Take(string target)
    {
        lock (gate)
        {
            if (current is null || current.Target != target) return null;
            var fault = current;
            current = current.Remaining == 1 ? null : current with { Remaining = current.Remaining - 1 };
            return fault;
        }
    }

    public void Clear() { lock (gate) current = null; }
}
