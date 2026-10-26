using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;

namespace IntegrationHub.Api;

public sealed record LoginRequest(string Username, string Password);

public sealed class AdminAuth
{
    private readonly byte[] tokenKey;
    private readonly string username;
    private readonly string? developmentPassword;
    private readonly string? passwordHash;

    public AdminAuth(IConfiguration config, IHostEnvironment environment)
    {
        username = config["Security:AdminUsername"] ?? throw new InvalidOperationException("Admin username is required.");
        if (string.IsNullOrWhiteSpace(username)) throw new InvalidOperationException("Admin username cannot be blank.");
        developmentPassword = environment.IsDevelopment() ? config["Security:AdminPassword"] : null;
        passwordHash = config["Security:AdminPasswordHash"];
        if (string.IsNullOrEmpty(developmentPassword) && string.IsNullOrEmpty(passwordHash))
            throw new InvalidOperationException("An admin password hash is required outside Development.");
        if (developmentPassword is null && !ValidHashFormat(passwordHash))
            throw new InvalidOperationException("Admin password hash must contain a 16-byte salt and 32-byte hash.");
        tokenKey = Convert.FromBase64String(config["Security:AdminTokenKey"] ??
            throw new InvalidOperationException("Admin token signing key is required."));
        if (tokenKey.Length < 32) throw new InvalidOperationException("Admin token signing key must be at least 32 bytes.");
    }

    public bool CheckPassword(string candidateUsername, string candidatePassword)
    {
        if (candidateUsername is null || candidatePassword is null || candidateUsername.Length > 160 ||
            candidatePassword.Length > 1024) return false;
        var validName = FixedEqual(username, candidateUsername);
        var validPassword = false;
        if (developmentPassword is not null)
            validPassword = FixedEqual(developmentPassword, candidatePassword);
        else if (passwordHash is not null)
        {
            try
            {
                var parts = passwordHash.Split(':');
                if (parts.Length == 2)
                {
                    var salt = Convert.FromBase64String(parts[0]);
                    var expected = Convert.FromBase64String(parts[1]);
                    if (salt.Length >= 16 && expected.Length == 32)
                    {
                        var actual = Rfc2898DeriveBytes.Pbkdf2(candidatePassword, salt, 210_000,
                            HashAlgorithmName.SHA256, expected.Length);
                        validPassword = CryptographicOperations.FixedTimeEquals(expected, actual);
                    }
                }
            }
            catch (FormatException) { }
        }
        return validName && validPassword;
    }

    public (string Token, DateTimeOffset ExpiresAtUtc) Issue()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(8);
        var payload = $"admin|{expires.ToUnixTimeSeconds()}|{Guid.NewGuid():N}";
        var data = Encoding.UTF8.GetBytes(payload);
        var signature = HMACSHA256.HashData(tokenKey, data);
        return ($"{Base64Url(data)}.{Base64Url(signature)}", expires);
    }

    public bool Valid(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048) return false;
        var parts = token.Split('.');
        if (parts.Length != 2) return false;
        try
        {
            var data = Decode(parts[0]);
            var signature = Decode(parts[1]);
            if (signature.Length != 32 || !CryptographicOperations.FixedTimeEquals(
                    signature, HMACSHA256.HashData(tokenKey, data))) return false;
            var fields = Encoding.UTF8.GetString(data).Split('|');
            return fields.Length == 3 && fields[0] == "admin" &&
                long.TryParse(fields[1], out var expiry) && expiry > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        catch (FormatException) { return false; }
    }

    private static bool FixedEqual(string expected, string supplied) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));

    private static bool ValidHashFormat(string? value)
    {
        try
        {
            var parts = value?.Split(':');
            return parts is { Length: 2 } && Convert.FromBase64String(parts[0]).Length >= 16 &&
                Convert.FromBase64String(parts[1]).Length == 32;
        }
        catch (FormatException) { return false; }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value) => Convert.FromBase64String(
        value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
}

public static class AdminAuthEndpoints
{
    public static void UseAdminAuth(this WebApplication app)
    {
        var auth = app.Services.GetRequiredService<AdminAuth>();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Method == "OPTIONS" ||
                context.Request.Path.StartsWithSegments("/api/v1/health") ||
                context.Request.Path == "/api/v1/auth/login")
            {
                await next(context);
                return;
            }
            var authorization = context.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                !auth.Valid(authorization[7..].Trim()))
            {
                await Results.Problem(statusCode: 401, title: "Authentication required",
                    extensions: new Dictionary<string, object?> { ["code"] = "authentication_required" })
                    .ExecuteAsync(context);
                return;
            }
            await next(context);
        });
        app.MapPost("/api/v1/auth/login", (LoginRequest request) =>
        {
            if (!auth.CheckPassword(request.Username, request.Password))
                return Results.Problem(statusCode: 401, title: "Invalid credentials",
                    extensions: new Dictionary<string, object?> { ["code"] = "invalid_credentials" });
            var issued = auth.Issue();
            return Results.Ok(new { accessToken = issued.Token, expiresAtUtc = issued.ExpiresAtUtc });
        }).RequireRateLimiting("admin-login");
    }
}
