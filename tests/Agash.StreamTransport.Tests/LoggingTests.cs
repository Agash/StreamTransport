using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.Tests;

[TestClass]
public sealed class LoggingTests
{
    [TestMethod]
    public void EventIds_AreExplicitAndUniqueAcrossTheLibrary()
    {
        List<(int Id, string Method)> ids = [];
        foreach (string path in Directory.GetFiles(AppContext.BaseDirectory, "Agash.StreamTransport*.dll"))
        {
            if (Path.GetFileName(path).Contains(".Tests", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Type type in Assembly.LoadFrom(path).GetTypes())
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (method.GetCustomAttribute<LoggerMessageAttribute>() is { } attribute)
                    {
                        string name = $"{type.FullName}.{method.Name}";
                        Assert.AreNotEqual(-1, attribute.EventId, $"{name} has no event id");
                        ids.Add((attribute.EventId, name));
                    }
                }
            }
        }

        Assert.IsGreaterThan(50, ids.Count, $"the library's assemblies were found ({ids.Count} log methods)");
        string[] duplicates = [.. ids.GroupBy(static i => i.Id).Where(static g => g.Count() > 1).Select(static g => $"{g.Key}: {string.Join(", ", g.Select(static i => i.Method))}")];
        Assert.IsEmpty(duplicates, string.Join("; ", duplicates));
    }
}
