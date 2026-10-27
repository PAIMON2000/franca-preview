using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace Franca.Services;

// Only the standalone publish profile embeds distribution assets. Mutable settings,
// downloaded runtimes and generated patches are never included or overwritten here.
internal static class ReleaseAssetsService
{
    private const string Prefix = "Franca.Distribution/";

    public static void EnsureAvailable()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            var relative = name[Prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(root, relative));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid distribution asset path.");
            using var source = assembly.GetManifestResourceStream(name)!;
            if (File.Exists(destination))
            {
                using var existing = File.OpenRead(destination);
                if (SHA256.HashData(existing).AsSpan().SequenceEqual(SHA256.HashData(source)))
                    continue;
                source.Position = 0;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = File.Create(temporary)) source.CopyTo(output);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
