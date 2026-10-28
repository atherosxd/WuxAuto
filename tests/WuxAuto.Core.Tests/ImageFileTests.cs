using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>Images are read and written through .NET (ImageFile), so folders with non-ASCII names work whatever the OpenCV wrapper does with paths.</summary>
[Collection("AppPaths")]
public class ImageFileTests : AppPathsScope
{
    [Fact]
    public void TemplateLibrary_LoadsFromNonAsciiFolder()
    {
        string root = Path.Combine(TempRoot(), "Pétur");
        Directory.CreateDirectory(Path.Combine(root, "templates"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "mapicon.png"), Path.Combine(root, "templates", "mapicon.png"));
        AppPaths.Configure(root, Path.Combine(root, "no-assets"));
        using var library = new TemplateLibrary();
        Assert.True(library.TryGet("mapicon", out _));
    }

    [Fact]
    public void DebugArtifacts_SaveFrame_WritesToNonAsciiFolder()
    {
        AppPaths.Configure(Path.Combine(TempRoot(), "Pétur"));
        using var frame = new Mat(64, 64, DepthType.Cv8U, 4);
        frame.SetTo(new MCvScalar(10, 20, 30, 255));
        DebugArtifacts.SaveFrame(frame, [], "test");
        string file = Assert.Single(Directory.GetFiles(AppPaths.DebugFolder, "*.png"));
        using var back = new Mat();
        CvInvoke.Imdecode(File.ReadAllBytes(file), ImreadModes.Unchanged, back);
        Assert.Equal((64, 64), (back.Width, back.Height));
    }
}
