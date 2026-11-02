using Agash.StreamTransport.Linux.Vulkan;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Agash.StreamTransport.Linux.Tests;

[TestClass]
public sealed class ServiceCollectionTests
{
    [TestMethod]
    public void AddLinuxMedia_RegistersTheVulkanProcessorOnce()
    {
        ServiceCollection services = new();
        _ = services.AddLinuxMedia().AddLinuxMedia();

        using ServiceProvider provider = services.BuildServiceProvider();
        IVideoProcessorFactory factory = provider.GetServices<IVideoProcessorFactory>().Single();
        Assert.IsInstanceOfType<VulkanVideoProcessorFactory>(factory);
    }
}
