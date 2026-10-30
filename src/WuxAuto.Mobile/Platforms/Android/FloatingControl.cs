using Android.AccessibilityServices;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using WuxAuto.Core;
using WuxAuto.Mobile.Engine;
using WuxAuto.Phone;
using Color = Android.Graphics.Color;
using View = Android.Views.View;
using Button = Android.Widget.Button;
using Paint = Android.Graphics.Paint;

namespace WuxAuto.Mobile;

/// <summary>
/// The floating control (spec §5.4), drawn by the accessibility service as TYPE_ACCESSIBILITY_OVERLAY windows (no
/// "draw over other apps" permission): a round button - grey idle, green running, red error, with an amber ring while
/// the running pilot waits - that the player drags (it snaps to the nearest edge; the spot is saved as fractions in
/// <see cref="PhoneSettings.FloatingX"/>/<see cref="PhoneSettings.FloatingY"/>). Tap → a small panel (LOD, Laby greyed out until M5,
/// Start / Stop, a fixed 3-line status, the last kill, Open app); long-press → Panic.
/// <para><b>It never covers what the pilot reads or taps</b> by where it sits, not by hiding: MediaProjection records
/// overlays, and its frames arrive on their own clock, so the button cannot be hidden for "the" captured frame. The
/// default spot is the top strip the phone checks found clear (note #15: centre 0.40, 0.03; nothing read or tapped
/// there); the panel is bigger and covers the game, so while it is open the engine does not tick the pilot
/// (<see cref="MobileEngine.PanelOpen"/>), and it shuts itself after <see cref="PanelIdleCloseMs"/> or on a touch
/// outside it. Both windows are not focusable, so the game stays the active window (the touch gate's
/// game-in-front check).</para>
/// <para>While the pilot runs the button's window carries FLAG_KEEP_SCREEN_ON (keep the screen on while running).</para>
/// </summary>
public sealed class FloatingControl
{
    /// <summary>The open panel shuts itself after this long without a touch (the pilot waits while it is open).</summary>
    public const int PanelIdleCloseMs = 15_000;

    public const int LongPressMs = 700;

    static readonly Color Grey = Color.Rgb(0x75, 0x75, 0x75);
    static readonly Color Green = Color.Rgb(0x2E, 0x7D, 0x32);
    static readonly Color Red = Color.Rgb(0xC6, 0x28, 0x28);
    static readonly Color Amber = Color.Rgb(0xFF, 0xB3, 0x00);

    static FloatingControl? _current;

    readonly AccessibilityService _service;
    readonly IWindowManager _wm;
    readonly Handler _main = new(Looper.MainLooper!);
    readonly Bubble _bubble;
    readonly WindowManagerLayoutParams _bubbleParams;
    readonly Action _tick;
    readonly Action _idleClose;

    LinearLayout? _panel;
    WindowManagerLayoutParams? _panelParams;
    TextView? _status;
    TextView? _kill;
    Button? _startStop;
    Button? _laby;
    Button? _lod;
    int _size;

    /// <summary>The control on screen, or null while the accessibility service is off.</summary>
    public static FloatingControl? Current => _current;

    FloatingControl(AccessibilityService service)
    {
        _service = service;
        _wm = service.GetSystemService(Context.WindowService).JavaCast<IWindowManager>()!;
        _bubble = new Bubble(service, this);
        _bubbleParams = OverlayParams(WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits);
        _tick = Tick;
        _idleClose = () => ClosePanel();
    }

    static MobileEngine Engine => EngineHost.Engine;

    /// <summary>Puts the control on screen (accessibility service connected). Main thread.</summary>
    public static void Attach(AccessibilityService service)
    {
        Detach();
        try
        {
            var control = new FloatingControl(service);
            control.Show();
            _current = control;
        }
        catch (Exception ex)
        {
            Log.Error(MainApplication.Tag, $"floating control: {ex}");
        }
    }

    /// <summary>Takes it off screen (the service is going away). Main thread.</summary>
    public static void Detach()
    {
        var control = Interlocked.Exchange(ref _current, null);
        control?.Remove();
    }

    void Show()
    {
        Place();
        _wm.AddView(_bubble, _bubbleParams);
        Refresh();
        _main.PostDelayed(_tick, 1000);
        Log.Info(MainApplication.Tag, "floating control shown");
    }

    void Remove()
    {
        _main.RemoveCallbacks(_tick);
        ClosePanel();
        try { _wm.RemoveView(_bubble); } catch (Exception) { }
    }

    /// <summary>Called again when the screen turns: the saved fractions are laid out on the new size.</summary>
    public void OnConfigurationChanged()
    {
        Place();
        try { _wm.UpdateViewLayout(_bubble, _bubbleParams); } catch (Exception) { }
        ClosePanel();
    }

    /// <summary>Colours, keep-screen-on and the panel's texts follow the engine. Main thread.</summary>
    public void Refresh()
    {
        var engine = Engine;
        var state = engine.State;
        _bubble.Fill = state switch
        {
            EngineState.Running => Green,
            EngineState.Error => Red,
            _ => Grey,
        };
        _bubble.Ring = state == EngineState.Running && engine.PauseReason.Length > 0 ? Amber : Color.White;
        _bubble.Invalidate();

        bool keepOn = state == EngineState.Running;
        bool hasFlag = (_bubbleParams.Flags & WindowManagerFlags.KeepScreenOn) != 0;
        if (keepOn != hasFlag)
        {
            _bubbleParams.Flags = keepOn ? _bubbleParams.Flags | WindowManagerFlags.KeepScreenOn : _bubbleParams.Flags & ~WindowManagerFlags.KeepScreenOn;
            try { _wm.UpdateViewLayout(_bubble, _bubbleParams); } catch (Exception) { }
        }

        if (_panel == null)
            return;
        _status!.Text = engine.StatusText;
        _kill!.Text = engine.LastKill.Length > 0 ? $"Last kill: {engine.LastKill}" : "Last kill: -";
        bool running = state == EngineState.Running;
        _startStop!.Text = running ? "Stop" : "Start";
        var mode = engine.Settings.Sequence.Mode;
        Style(_laby!, LabyLater, selected: false, enabled: false); // until M5: LOD only on the phone
        Style(_lod!, "LOD", mode == RunMode.AutoLod, !running);
    }

    void Tick()
    {
        Refresh();
        _main.PostDelayed(_tick, 1000);
    }

    void Place()
    {
        var (w, h) = DisplaySize.Current(_service);
        _size = FloatingSpot.SizeFor(w, h);
        var phone = Engine.Settings.Phone;
        var (left, top) = FloatingSpot.TopLeft(phone.FloatingX, phone.FloatingY, w, h, _size);
        _bubbleParams.Width = _size;
        _bubbleParams.Height = _size;
        _bubbleParams.X = left;
        _bubbleParams.Y = top;
    }

    static WindowManagerLayoutParams OverlayParams(WindowManagerFlags flags)
    {
        var p = new WindowManagerLayoutParams(
            WindowManagerLayoutParams.WrapContent, WindowManagerLayoutParams.WrapContent,
            WindowManagerTypes.AccessibilityOverlay, flags | WindowManagerFlags.LayoutInScreen, Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left,
            LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.Always,
        };
        return p;
    }

    // ---- the button's gestures (called by Bubble) ----

    void MoveTo(int left, int top)
    {
        _bubbleParams.X = left;
        _bubbleParams.Y = top;
        try { _wm.UpdateViewLayout(_bubble, _bubbleParams); } catch (Exception) { }
    }

    void Dropped()
    {
        var (w, h) = DisplaySize.Current(_service);
        var (fx, fy) = FloatingSpot.Snap(_bubbleParams.X + _size / 2, _bubbleParams.Y + _size / 2, w, h, _size);
        var engine = Engine;
        engine.Settings.Phone.FloatingX = fx;
        engine.Settings.Phone.FloatingY = fy;
        Place();
        try { _wm.UpdateViewLayout(_bubble, _bubbleParams); } catch (Exception) { }
        Log.Info(MainApplication.Tag, $"floating control moved to {fx:0.000},{fy:0.000} ({_bubbleParams.X},{_bubbleParams.Y})");
        _ = Task.Run(engine.Save);
    }

    void Tapped()
    {
        if (_panel != null)
            ClosePanel();
        else
            OpenPanel();
    }

    void LongPressed()
    {
        _bubble.PerformHapticFeedback(FeedbackConstants.LongPress);
        ClosePanel();
        Engine.Panic();
        Toast.MakeText(_service, "WuxAuto stopped (panic)", ToastLength.Short)?.Show();
    }

    // ---- the panel ----

    void OpenPanel()
    {
        var ctx = _service;
        float dp = ctx.Resources!.DisplayMetrics!.Density;
        int pad = (int)(10 * dp);

        var panel = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        panel.SetPadding(pad, pad, pad, pad);
        var bg = new GradientDrawable();
        bg.SetColor(Color.Rgb(0x1E, 0x23, 0x2D));
        bg.SetCornerRadius(12 * dp);
        bg.SetStroke((int)Math.Max(1, dp), Color.Rgb(0x3A, 0x44, 0x58));
        panel.Background = bg;

        var modes = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
        _laby = PanelButton(ctx, LabyLater, () => { });
        _lod = PanelButton(ctx, "LOD", () => SetMode(RunMode.AutoLod));
        modes.AddView(_laby, Weighted());
        modes.AddView(_lod, Weighted());
        panel.AddView(modes);

        _startStop = PanelButton(ctx, "Start", StartOrStop);
        panel.AddView(_startStop);

        // Fixed height: three lines whatever the text (the desktop Mini window's rule), so the panel never jumps.
        _status = new TextView(ctx) { TextSize = 13 };
        _status.SetTextColor(Color.White);
        _status.SetLines(3);
        _status.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        panel.AddView(_status);

        _kill = new TextView(ctx) { TextSize = 12 };
        _kill.SetTextColor(Color.Rgb(0xB0, 0xBE, 0xC5));
        _kill.SetSingleLine(true);
        _kill.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        panel.AddView(_kill);

        var bottom = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
        bottom.AddView(PanelButton(ctx, "Open app", OpenApp), Weighted());
        bottom.AddView(PanelButton(ctx, "Close", () => ClosePanel()), Weighted());
        panel.AddView(bottom);

        // Outside touches go on to the game and shut the panel; inside ones keep it open longer.
        panel.Touch += (_, e) =>
        {
            if (e.Event?.Action == MotionEventActions.Outside)
                ClosePanel();
            else
                Poke();
            e.Handled = false;
        };

        var (w, h) = DisplaySize.Current(ctx);
        int width = (int)Math.Min(w * 0.9, 300 * dp);
        var p = OverlayParams(WindowManagerFlags.NotFocusable | WindowManagerFlags.WatchOutsideTouch);
        p.Width = width;
        p.X = Math.Clamp(_bubbleParams.X + _size / 2 - width / 2, 0, Math.Max(0, w - width));
        bool below = _bubbleParams.Y + _size / 2 < h / 2;
        p.Gravity = GravityFlags.Left | (below ? GravityFlags.Top : GravityFlags.Bottom);
        p.Y = below ? _bubbleParams.Y + _size + (int)(6 * dp) : h - _bubbleParams.Y + (int)(6 * dp);

        _panel = panel;
        _panelParams = p;
        Engine.PanelOpen = true;
        try
        {
            _wm.AddView(panel, p);
        }
        catch (Exception ex)
        {
            Log.Warn(MainApplication.Tag, $"panel: {ex.Message}");
            _panel = null;
            Engine.PanelOpen = false;
            return;
        }

        Refresh();
        Poke();
    }

    void ClosePanel()
    {
        _main.RemoveCallbacks(_idleClose);
        var panel = _panel;
        _panel = null;
        _panelParams = null;
        _status = _kill = null;
        _startStop = _laby = _lod = null;
        if (panel != null)
        {
            try { _wm.RemoveView(panel); } catch (Exception) { }
        }

        if (EngineHost.IsCreated)
            Engine.PanelOpen = false;
    }

    void Poke()
    {
        _main.RemoveCallbacks(_idleClose);
        _main.PostDelayed(_idleClose, PanelIdleCloseMs);
    }

    /// <summary>The Laby button's text until M5 ports the Labyrinth to the phone (it stays disabled).</summary>
    const string LabyLater = "Laby: coming in M5";

    void SetMode(RunMode mode)
    {
        var engine = Engine;
        if (engine.State == EngineState.Running || engine.Settings.Sequence.Mode == mode)
            return;
        engine.Settings.Sequence.Mode = mode;
        _ = Task.Run(engine.Save);
        Refresh();
    }

    void StartOrStop()
    {
        var engine = Engine;
        if (engine.State == EngineState.Running)
        {
            engine.Stop("Stop in the floating control");
        }
        else
        {
            // Shut first: the pilot does not tick while the panel covers the game.
            ClosePanel();
            engine.Start(engine.Settings.Sequence.Mode);
        }

        Refresh();
    }

    void OpenApp()
    {
        ClosePanel();
        var intent = new Intent(_service, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask | ActivityFlags.ReorderToFront);
        _service.StartActivity(intent);
    }

    Button PanelButton(Context ctx, string text, Action click)
    {
        var b = new Button(ctx) { Text = text, TextSize = 13 };
        b.SetAllCaps(false);
        b.Click += (_, _) =>
        {
            Poke();
            click();
        };
        return b;
    }

    /// <summary>The chosen mode is marked and fully opaque; while running neither can be changed.</summary>
    static void Style(Button b, string label, bool selected, bool enabled)
    {
        b.Enabled = enabled;
        b.Alpha = selected ? 1f : 0.55f;
        b.Text = selected ? $"● {label}" : label;
    }

    static LinearLayout.LayoutParams Weighted() => new(0, ViewGroup.LayoutParams.WrapContent, 1f);

    /// <summary>The round button: draws itself and turns touches into tap / drag / long-press.</summary>
    sealed class Bubble : View
    {
        readonly FloatingControl _owner;
        readonly Paint _paint = new(PaintFlags.AntiAlias);
        readonly int _slop;
        readonly Action _longPress;
        float _downRawX, _downRawY;
        int _downLeft, _downTop;
        bool _dragging, _longPressed;

        public Color Fill = Grey;
        public Color Ring = Color.White;

        public Bubble(Context context, FloatingControl owner) : base(context)
        {
            _owner = owner;
            _slop = ViewConfiguration.Get(context)!.ScaledTouchSlop;
            _longPress = () =>
            {
                if (_dragging)
                    return;
                _longPressed = true;
                _owner.LongPressed();
            };
            ContentDescription = "WuxAuto: tap for the panel, long-press to stop";
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            float r = Math.Min(Width, Height) / 2f;
            float ring = Math.Max(2f, r * 0.14f);
            _paint.SetStyle(Paint.Style.Fill);
            _paint.Color = Fill;
            canvas.DrawCircle(Width / 2f, Height / 2f, r - ring / 2f, _paint);
            _paint.SetStyle(Paint.Style.Stroke);
            _paint.StrokeWidth = ring;
            _paint.Color = Ring;
            canvas.DrawCircle(Width / 2f, Height / 2f, r - ring / 2f, _paint);
            _paint.SetStyle(Paint.Style.Fill);
            _paint.Color = Color.White;
            _paint.TextSize = r * 0.9f;
            _paint.TextAlign = Paint.Align.Center;
            canvas.DrawText("W", Width / 2f, Height / 2f - (_paint.Descent() + _paint.Ascent()) / 2f, _paint);
        }

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e == null)
                return false;
            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    _downRawX = e.RawX;
                    _downRawY = e.RawY;
                    _downLeft = _owner._bubbleParams.X;
                    _downTop = _owner._bubbleParams.Y;
                    _dragging = _longPressed = false;
                    _owner._main.PostDelayed(_longPress, LongPressMs);
                    return true;
                case MotionEventActions.Move:
                    if (_longPressed)
                        return true;
                    float dx = e.RawX - _downRawX, dy = e.RawY - _downRawY;
                    if (!_dragging && dx * dx + dy * dy > _slop * _slop)
                    {
                        _dragging = true;
                        _owner._main.RemoveCallbacks(_longPress);
                    }

                    if (_dragging)
                        _owner.MoveTo(_downLeft + (int)dx, _downTop + (int)dy);
                    return true;
                case MotionEventActions.Up:
                {
                    _owner._main.RemoveCallbacks(_longPress);
                    // The lift's own position counts: the last Move can lag behind it (a fast drag, adb's swipe).
                    float ux = e.RawX - _downRawX, uy = e.RawY - _downRawY;
                    if (!_longPressed && (_dragging || ux * ux + uy * uy > _slop * _slop))
                    {
                        _dragging = true;
                        _owner.MoveTo(_downLeft + (int)ux, _downTop + (int)uy);
                        _owner.Dropped();
                    }
                    else if (!_longPressed)
                    {
                        _owner.Tapped();
                    }

                    return true;
                }
                case MotionEventActions.Cancel:
                    _owner._main.RemoveCallbacks(_longPress);
                    if (_dragging)
                        _owner.Dropped();
                    return true;
            }

            return base.OnTouchEvent(e);
        }
    }
}
