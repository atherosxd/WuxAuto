using Android.App;
using Android.Runtime;
using Android.Util;

namespace WuxAuto.Mobile;

[Application]
public class MainApplication : MauiApplication
{
    public const string Tag = "WuxAuto";

    public MainApplication(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    /// <summary>
    /// Runs for every component that starts the process (the accessibility service starts it without any UI), so the
    /// native OpenCV load and the template install happen here, before the service or the capture touch them.
    /// </summary>
    public override void OnCreate()
    {
        base.OnCreate();
        try
        {
            Emgu.CV.CvInvokeAndroid.Init();
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"OpenCV did not load: {ex}");
        }

        try
        {
            AssetInstaller.Install(this);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"asset install failed: {ex}");
        }
    }
}
