using System.Globalization;
using System.Text.Json;

namespace IntegrationHub.Application;

public sealed record MapResult(ErpOrder? Order, string? Code, string? Message)
{
    public bool Valid => Order is not null;
}

public static class OrderMapping
{
    public static MapResult Map(JsonElement source, MappingConfiguration mapping, ValidationConfiguration validation)
    {
        if (mapping.SchemaVersion != 1 || validation.SchemaVersion != 1)
            return Failure("unsupported_schema", "The mapping or validation schema is unsupported.");
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in mapping.Fields)
        {
            if (!TryPath(source, field.Source, out var raw) || raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (field.Required) return Failure("required_field_missing", $"Required source field {field.Source} is missing.");
                continue;
            }
            if (!TryConvert(raw, field.Transform, out var value))
                return Failure("conversion_failed", $"Source field {field.Source} cannot be converted.");
            values[field.Destination] = value!;
        }
        if (!GetString(values, "externalOrderId", out var externalId) || externalId.Length > 80)
            return Failure("invalid_external_order_id", "The external order ID is missing or invalid.");
        if (!GetString(values, "customerEmail", out var email) || email.Length > 320 ||
            !email.Contains('@') || email.StartsWith('@') || email.EndsWith('@'))
            return Failure("invalid_customer_email", "The customer email is missing or invalid.");
        if (!values.TryGetValue("amount", out var amountValue) || !TryDecimal(amountValue, out var amount) ||
            amount < validation.AmountMinimum || amount > 9999999999999999.99m)
            return Failure("invalid_amount", "The order amount is missing or outside the accepted range.");
        if (!values.TryGetValue("placedAtUtc", out var dateValue) || !TryDate(dateValue, out var date))
            return Failure("invalid_placed_at", "The order date is missing or invalid.");
        return new(new ErpOrder(externalId, email, amount, date.ToUniversalTime()), null, null);
    }

    private static MapResult Failure(string code, string message) => new(null, code, message);
    private static bool TryPath(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        foreach (var part in path.Split('.'))
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) return false;
        return true;
    }
    private static bool TryConvert(JsonElement raw, string? transform, out object? value)
    {
        value = null;
        switch (transform)
        {
            case null:
                value = raw.ValueKind switch
                {
                    JsonValueKind.String => raw.GetString(),
                    JsonValueKind.Number when raw.TryGetDecimal(out var number) => number,
                    _ => null
                };
                return value is not null;
            case "trim":
                if (raw.ValueKind != JsonValueKind.String) return false;
                value = raw.GetString()?.Trim(); return value is not null;
            case "decimal":
                if (raw.ValueKind == JsonValueKind.Number && raw.TryGetDecimal(out var decimalNumber))
                { value = decimalNumber; return true; }
                if (raw.ValueKind == JsonValueKind.String && decimal.TryParse(raw.GetString(), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var parsed)) { value = parsed; return true; }
                return false;
            case "utcDate":
                if (raw.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(raw.GetString(),
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                { value = date.ToUniversalTime(); return true; }
                return false;
            default: return false;
        }
    }
    private static bool GetString(Dictionary<string, object> values, string key, out string value)
    {
        value = values.TryGetValue(key, out var raw) ? raw as string ?? "" : "";
        return !string.IsNullOrWhiteSpace(value);
    }
    private static bool TryDecimal(object value, out decimal result)
    {
        if (value is decimal number) { result = number; return true; }
        return decimal.TryParse(value as string, NumberStyles.Number, CultureInfo.InvariantCulture, out result);
    }
    private static bool TryDate(object value, out DateTimeOffset result)
    {
        if (value is DateTimeOffset date) { result = date; return true; }
        return DateTimeOffset.TryParse(value as string, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result);
    }
}
