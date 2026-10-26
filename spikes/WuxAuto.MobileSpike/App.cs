namespace WuxAuto.MobileSpike;

public class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new ContentPage
        {
            Content = new Label
            {
                Text = "WuxSpike - enable the accessibility service, then use adb broadcasts.",
                Margin = 24,
            },
        });
}
