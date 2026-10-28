using Android.App;
using Android.Content.PM;

namespace WuxAuto.Mobile;

// Landscape like the game: without it the activity follows the device's natural orientation
// (MuMu is natively portrait 1080x1920), which flipped the whole emulator to portrait.
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, Exported = true,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}
