namespace WuxAuto.Mobile;

/// <summary>Placeholder page until the floating control and settings UI (Task 6+).</summary>
public class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new ContentPage
        {
            Content = new Label
            {
                Text = "WuxAuto - turn on the WuxAuto accessibility service (Settings > Accessibility), then start the capture.",
                Margin = 24,
            },
        });
}
