using Agash.StreamTransport.MacOS.Metal;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Agash.StreamTransport.MacOS.Tests;

[TestClass]
public sealed class ServiceCollectionTests
{
    [TestMethod]
    public void AddMacOSMedia_RegistersTheMetalProcessorOnce()
    {
        ServiceCollection services = new();
        _ = services.AddMacOSMedia().AddMacOSMedia();

        using ServiceProvider provider = services.BuildServiceProvider();
        IVideoProcessorFactory factory = provider.GetServices<IVideoProcessorFactory>().Single();
        Assert.IsInstanceOfType<MetalVideoProcessorFactory>(factory);
    }
}
