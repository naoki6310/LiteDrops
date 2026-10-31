using System.Security.Cryptography;
using System.Text;

namespace LiteDrops;

static class InstanceId
{
    /// <summary>Short hash of the folder the exe runs from; tells copies of LiteDrops apart.</summary>
    public static readonly string Folder = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\').ToLowerInvariant())))[..12];
}