using WuxAuto.Core;

namespace WuxAuto.Core.Tests;

/// <summary>
/// Base for the tests of the "AppPaths" collection: <see cref="AppPaths"/> is static, so every test
/// puts back the folders it found (xUnit disposes the class instance after each test) and the temp
/// folders it made with <see cref="TempRoot"/> are deleted.
/// </summary>
public abstract class AppPathsScope : IDisposable
{
    private readonly string _previousRoot = AppPaths.Root;
    private readonly string _previousAssets = AppPaths.AssetsFolder;
    private readonly List<string> _tempRoots = new();

    /// <summary>A new, empty folder under the temp path; deleted after the test.</summary>
    protected string TempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "wux-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }

    public void Dispose()
    {
        AppPaths.Configure(_previousRoot, _previousAssets);
        foreach (string root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // a file still open somewhere: the temp folder is left behind
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        GC.SuppressFinalize(this);
    }
}
