using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

[Collection("AppPaths")]
public class TemplateLibraryTests : AppPathsScope
{
    // The legacy (OpenCvSharp) crop of flashlight3.png's largest opaque patch (Task 10).
    const int LegacyWidth = 98, LegacyHeight = 67;

    [Fact]
    public void LoadGray_CropsTransparentPngToSameBoxAsLegacy()
    {
        AppPaths.Configure(TempRoot(), Path.Combine(AppContext.BaseDirectory, "Assets"));
        using var library = new TemplateLibrary();
        Assert.True(library.TryGet("flashlight3", out var t));
        Assert.Equal((LegacyWidth, LegacyHeight), (t.Gray.Width, t.Gray.Height));
    }
}
