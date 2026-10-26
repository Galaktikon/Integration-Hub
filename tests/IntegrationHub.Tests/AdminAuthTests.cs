using System.Security.Cryptography;
using IntegrationHub.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace IntegrationHub.Tests;

public sealed class AdminAuthTests
{
    [Fact]
    public void ProductionUsesPasswordHashAndRejectsDevelopmentPassword()
    {
        const string password = "correct-production-password";
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:AdminUsername"] = "operator",
            ["Security:AdminPassword"] = "local-demo-password",
            ["Security:AdminPasswordHash"] = $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}",
            ["Security:AdminTokenKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        }).Build();
        var auth = new AdminAuth(config, new ProductionEnvironment());
        Assert.True(auth.CheckPassword("operator", password));
        Assert.False(auth.CheckPassword("operator", "local-demo-password"));
        Assert.False(auth.CheckPassword("other", password));
        Assert.True(auth.Valid(auth.Issue().Token));
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "IntegrationHub.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
