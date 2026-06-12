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
        Assembly assembly = typeof(EmbeddedShader).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"Embedded shader '{logicalName}' was not found in {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
