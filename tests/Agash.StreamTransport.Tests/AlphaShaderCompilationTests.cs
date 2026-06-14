using System.Reflection;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Validates the Linux GPU alpha pack/unpack compute shaders. The GLSL <c>.comp</c> sources are compiled to
/// SPIR-V at build time by glslc (see the agent csproj's <c>CompileComputeShadersToSpirV</c> target) - a
/// syntax or stage error fails the build there - and the runtime loads the precompiled <c>.spv</c> directly.
/// This test confirms each embedded <c>.spv</c> is present and a well-formed SPIR-V module.
///
/// <para>It deliberately does <b>not</b> run a runtime GLSL-&gt;SPIR-V compiler (shaderc): shaderc's bundled
/// SPIRV-Tools collides with the Mesa VAAPI driver's SPIRV-Tools when both are loaded in one process (the VA
/// driver pulls in the system libSPIRV-Tools/libLLVM), interposing symbols and crashing - which is exactly
/// why the compilation was moved to build time.</para>
/// </summary>
[TestClass]
public sealed class AlphaShaderCompilationTests
{
    [TestMethod]
    [DataRow("alpha_pack.spv")]
    [DataRow("alpha_unpack_nv12.spv")]
    [DataRow("alpha_unpack_bgra.spv")]
    public void ComputeShader_EmbeddedSpirV_IsValidModule(string logicalName)
    {
        byte[] spirv = LoadEmbedded(logicalName);

        Assert.IsTrue(spirv.Length >= 20 && spirv.Length % 4 == 0,
            $"SPIR-V should be a non-trivial 4-byte-aligned blob; got {spirv.Length} bytes.");

        // SPIR-V modules begin with the magic word 0x07230203.
        uint magic = BitConverter.ToUInt32(spirv, 0);
        Assert.AreEqual(0x07230203u, magic, "Embedded resource is not a SPIR-V module.");
    }

    private static byte[] LoadEmbedded(string logicalName)
    {
        Assembly assembly = typeof(AlphaShaderCompilationTests).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded shader '{logicalName}' not found in {assembly.GetName().Name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
