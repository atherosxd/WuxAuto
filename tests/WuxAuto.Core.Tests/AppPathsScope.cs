using WuxAuto.Core;

namespace WuxAuto.Core.Tests;

/// <summary>
/// Base for the tests that point <see cref="AppPaths"/> somewhere: each test gets folders of its own
/// (<see cref="AppPaths.IsolateThisFlow"/>, set in the constructor - xUnit runs the constructor and the
/// test in one async flow), so these classes run in parallel; every test still puts back the folders it
/// found (xUnit disposes the class instance after each test) and the temp folders it made with
/// <see cref="TempRoot"/> are deleted.
/// </summary>
public abstract class AppPathsScope : IDisposable
{
    private readonly string _previousRoot = AppPaths.Root;
    private readonly string _previousAssets = AppPaths.AssetsFolder;
    private readonly List<string> _tempRoots = new();

    protected AppPathsScope() => AppPaths.IsolateThisFlow();

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
