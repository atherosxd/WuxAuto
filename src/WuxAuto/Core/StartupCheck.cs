using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Windows.Media.Ocr;

namespace WuxAuto.Core;

/// <summary>
/// Checks, before the main window is built, the Windows parts this app needs and that
/// trimmed "lite" Windows builds (e.g. Ghost Spectre) or Windows N editions often remove.
/// Without it a missing DLL only shows up as a one-line type-initializer error.
/// </summary>
public static class StartupCheck
{
    public sealed record Result(IReadOnlyList<string> Fatal, IReadOnlyList<string> Warnings)
    {
        public bool HasProblems => Fatal.Count > 0 || Warnings.Count > 0;
    }

    // Imported by cvextern.dll (Emgu CV's native OpenCV build; dumpbin /dependents): if one is
    // missing the DLL cannot load. d3d11 / dxgi are the capture's too.
    private static readonly string[] MediaFoundationDlls = ["mfplat.dll", "mf.dll", "mfreadwrite.dll"];
    private static readonly string[] OtherSystemDlls = ["d3d11.dll", "dxgi.dll"];

    public static Result Run()
    {
        var fatal = new List<string>();
        var warnings = new List<string>();

        int build = Environment.OSVersion.Version.Build;
        if (build < 19041)
            warnings.Add($"Windows build {build} is older than 19041 (Windows 10 version 2004), the oldest this app is built for.");

        var missingMf = MediaFoundationDlls.Where(n => !CanLoad(n)).ToList();
        if (missingMf.Count > 0)
            fatal.Add(
                $"Media Foundation is missing ({string.Join(", ", missingMf)}). OpenCV needs it to load. " +
                "On a Windows 'N' edition install the Media Feature Pack (Settings > Apps > Optional features > " +
                "Add a feature > Media Feature Pack). Trimmed 'lite' Windows builds may have removed it for good.");

        var missingOther = OtherSystemDlls.Where(n => !CanLoad(n)).ToList();
        if (missingOther.Count > 0)
            fatal.Add($"Windows system files are missing: {string.Join(", ", missingOther)}.");

        // Only meaningful when the system DLLs are there; otherwise it repeats the error above.
        if (fatal.Count == 0)
        {
            try
            {
                // The first Emgu call loads cvextern.dll.
                using var probe = new Mat(1, 1, DepthType.Cv8U, 1);
            }
            catch (Exception ex)
            {
                fatal.Add($"OpenCV could not load: {Innermost(ex).Message}");
            }
        }

        try
        {
            var languages = OcrEngine.AvailableRecognizerLanguages;
            if (!languages.Any(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)))
                warnings.Add(
                    "No English OCR language is installed, so the app cannot read the game's text. " +
                    "Add English under Settings > Time & language > Language (with its optical character recognition part)." +
                    (languages.Count > 0 ? $" Installed: {string.Join(", ", languages.Select(l => l.LanguageTag))}." : string.Empty));
        }
        catch (Exception ex)
        {
            warnings.Add($"Windows OCR is not available on this system: {Innermost(ex).Message}");
        }

        return new Result(fatal, warnings);
    }

    /// <summary>Writes the check result plus basic system info to the log folder; returns the file path or null.</summary>
    public static string? WriteReport(Result result)
    {
        var text = new StringBuilder()
            .AppendLine($"WuxAuto startup check {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), build {Environment.OSVersion.Version}")
            .AppendLine($".NET: {RuntimeInformation.FrameworkDescription}, process {RuntimeInformation.ProcessArchitecture}")
            .AppendLine($"App folder: {AppContext.BaseDirectory}")
            .AppendLine();
        foreach (var line in result.Fatal)
            text.AppendLine("ERROR: " + line);
        foreach (var line in result.Warnings)
            text.AppendLine("WARNING: " + line);
        return CrashLog.Write("startup_check", text.ToString());
    }

    private static bool CanLoad(string dll)
    {
        if (!NativeLibrary.TryLoad(dll, out var handle))
            return false;
        NativeLibrary.Free(handle);
        return true;
    }

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException != null)
            ex = ex.InnerException;
        return ex;
    }
}

/// <summary>Plain-file error log for failures that happen before (or outside) the normal log pane.</summary>
public static class CrashLog
{
    public static string? Write(string tag, string text)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogFolder);
            string path = Path.Combine(AppPaths.LogFolder, $"{tag}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, text);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
