using System.Security.Cryptography;

namespace Pult.Core.Admin;

public interface IFileHasher { string? Sha256(string path); }

public sealed class FileHasher : IFileHasher
{
    public string? Sha256(string path)
    {
        if (!File.Exists(path)) return null;
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }
}
