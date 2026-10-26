using System.Security.Cryptography;
using IntegrationHub.Infrastructure;

namespace IntegrationHub.Tests;

public sealed class SecretProtectorTests
{
    [Fact]
    public void CiphertextDoesNotExposeSecretAndCanBeDecrypted()
    {
        var protector = new SecretProtector(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var cipher = protector.Encrypt("sensitive-api-key");

        Assert.DoesNotContain("sensitive-api-key", cipher);
        Assert.Equal("sensitive-api-key", protector.Decrypt(cipher));
        Assert.NotEqual(cipher, protector.Encrypt("sensitive-api-key"));
    }
}
