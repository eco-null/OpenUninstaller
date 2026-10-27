using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Tests;

public sealed class VersionTests
{
    [Fact]
    public void DefaultAppVersionMatchesAssemblyVersion()
    {
        var expected = typeof(AppPaths).Assembly.GetName().Version!.ToString(3);
        Assert.Equal(expected, AppPaths.CreateDefault().AppVersion);
        Assert.NotEqual("unknown", expected);
    }
}
