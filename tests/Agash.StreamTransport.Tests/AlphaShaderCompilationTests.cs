// The Vulkan SPIR-V shaders are embedded only in the base (non -windows) build of
// Agash.StreamTransport -- the Linux compute path. On the -windows TFM the library ships the HLSL
// equivalents instead, so there is nothing here to validate and the resources are legitimately absent.
#if !WINDOWS_D3D11
using System.Reflection;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Validates the Linux GPU alpha pack/unpack compute shaders. The GLSL <c>.comp</c> sources are compiled to
/// SPIR-V at build time by glslc (see the <c>CompileComputeShadersToSpirV</c> target in
/// <c>Agash.StreamTransport.csproj</c>) - a syntax or stage error fails the build there - and the runtime
/// loads the precompiled <c>.spv</c> directly. This test confirms each <c>.spv</c> embedded in the shipping
/// library is present and a well-formed SPIR-V module.
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
        // Read from the shipping library, not a test-local copy: this is the exact resource the
        // Vulkan alpha codec loads at runtime, so the test cannot pass against a stale duplicate.
        Assembly assembly = typeof(Codecs.AlphaPacking).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded shader '{logicalName}' not found in {assembly.GetName().Name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
#endif
