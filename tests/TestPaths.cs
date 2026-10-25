using System.Reflection;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.JellyDvrGuides.Tests;

/// <summary>Application paths with only DataPath, pointing at a fresh folder deleted on dispose.</summary>
public sealed class TestPaths : IDisposable
{
    public TestPaths()
    {
        DataPath = Path.Combine(Path.GetTempPath(), "jellydvr-guides-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataPath);
        Paths = DispatchProxy.Create<IApplicationPaths, Proxy>();
        ((Proxy)(object)Paths).DataPath = DataPath;
    }

    public string DataPath { get; }

    public IApplicationPaths Paths { get; }

    public void Dispose() => Directory.Delete(DataPath, true);

    public class Proxy : DispatchProxy
    {
        public string DataPath { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_DataPath" ? DataPath : throw new NotSupportedException(targetMethod?.Name);
    }
}
