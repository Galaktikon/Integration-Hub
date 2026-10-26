using System.Security.Cryptography;

namespace IntegrationHub.Infrastructure;

public sealed class SecretProtector
{
    private readonly byte[] _key;

    public SecretProtector(string base64Key)
    {
        try { _key = Convert.FromBase64String(base64Key); }
        catch (FormatException ex) { throw new InvalidOperationException("Security:EncryptionKey must be base64.", ex); }
        if (_key.Length != 32)
            throw new InvalidOperationException("Security:EncryptionKey must contain exactly 32 bytes.");
    }

    public string Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var data = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, data, cipher, tag);
        var envelope = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(envelope, 0);
        tag.CopyTo(envelope, nonce.Length);
        cipher.CopyTo(envelope, nonce.Length + tag.Length);
        return Convert.ToBase64String(envelope);
    }

    public string Decrypt(string envelope)
    {
        var bytes = Convert.FromBase64String(envelope);
        if (bytes.Length < 28) throw new CryptographicException("Invalid secret envelope.");
        var data = new byte[bytes.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), data);
        return System.Text.Encoding.UTF8.GetString(data);
    }
}
