using System.Reflection;
using Vortice.ShaderCompiler;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Validates that the Linux GPU alpha pack/unpack compute shaders (GLSL) compile to SPIR-V through the same
/// shaderc the Linux runtime uses (<see cref="Compiler"/>). The dmabuf import + Vulkan dispatch can only run
/// on a real Linux GPU, but the shader source itself is verified here on every platform - a syntax or stage
/// error is caught at test time rather than on the target hardware.
/// </summary>
[TestClass]
public sealed class AlphaShaderCompilationTests
{
    [TestMethod]
    [DataRow("alpha_pack.comp")]
    [DataRow("alpha_unpack_nv12.comp")]
    [DataRow("alpha_unpack_bgra.comp")]
    public void ComputeShader_CompilesToSpirV(string logicalName)
    {
        string source = LoadEmbedded(logicalName);

        using var compiler = new Compiler();
        // The .comp extension selects ShaderKind.ComputeShader; default target env is Vulkan.
        CompileResult result = compiler.Compile(source, logicalName);

        Assert.AreEqual(CompilationStatus.Success, result.Status, result.ErrorMessage);
        Assert.IsTrue(result.Bytecode.Length >= 20 && result.Bytecode.Length % 4 == 0,
            $"SPIR-V should be a non-trivial 4-byte-aligned blob; got {result.Bytecode.Length} bytes.");

        // SPIR-V modules begin with the magic word 0x07230203.
        uint magic = BitConverter.ToUInt32(result.Bytecode, 0);
        Assert.AreEqual(0x07230203u, magic, "Output is not a SPIR-V module.");
    }

    private static string LoadEmbedded(string logicalName)
    {
        Assembly assembly = typeof(AlphaShaderCompilationTests).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded shader '{logicalName}' not found in {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
