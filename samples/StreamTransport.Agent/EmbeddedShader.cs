using System.Reflection;

namespace StreamTransport.Agent;

/// <summary>
/// Loads a shader's source text from an embedded resource by its logical name (set in the csproj),
/// so shaders live in editable <c>.metal</c>/<c>.hlsl</c> files with full editor support rather than
/// inline string literals, while still shipping inside the assembly with no file dependency.
/// </summary>
internal static class EmbeddedShader
{
    public static string Load(string logicalName)
    {
        using Stream stream = Open(logicalName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Loads a shader's raw bytes (e.g. precompiled SPIR-V) from an embedded resource.</summary>
    public static byte[] LoadBytes(string logicalName)
    {
        using Stream stream = Open(logicalName);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static Stream Open(string logicalName)
    {
        Assembly assembly = typeof(EmbeddedShader).Assembly;
        return assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"Embedded shader '{logicalName}' was not found in {assembly.GetName().Name}.");
    }
}
