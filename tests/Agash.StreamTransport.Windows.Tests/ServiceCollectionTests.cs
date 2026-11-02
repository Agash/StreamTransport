using Agash.StreamTransport.Media;
using Agash.StreamTransport.Windows.Direct3D12;
using Microsoft.Extensions.DependencyInjection;

namespace Agash.StreamTransport.Windows.Tests;

[TestClass]
public sealed class ServiceCollectionTests
{
    [TestMethod]
    public void AddWindowsMedia_RegistersTheD3D12ProcessorOnce()
    {
        ServiceCollection services = new();
        _ = services.AddWindowsMedia().AddWindowsMedia();

        using ServiceProvider provider = services.BuildServiceProvider();
        IVideoProcessorFactory factory = provider.GetServices<IVideoProcessorFactory>().Single();
        Assert.IsInstanceOfType<D3D12VideoProcessorFactory>(factory);
    }
}
