using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace M7000Tray;

public sealed partial class SettingsWindow : Window
{
    readonly Settings _settings;
    readonly Action _onSaved;
    readonly double _shownLimit, _shownRemaining;

    public SettingsWindow(Settings settings, string status, string? simNumber, Action onSaved)
    {
        InitializeComponent();
        _settings = settings;
        _onSaved = onSaved;

        StatusText.Text = status;
        SimText.Text = string.IsNullOrEmpty(simNumber) ? "Номер SIM: —" : simNumber;
        PasswordInput.Password = settings.Password;
        LimitInput.Value = _shownLimit = settings.LimitGb;
        _shownRemaining = Math.Round(Math.Max(0, settings.LimitGb - settings.UsedBytes / Settings.BytesPerGb), 2);
        RemainingInput.Value = _shownRemaining;
        AutoStartInput.IsChecked = settings.AutoStart;

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "M7000.ico"));

        // Небольшое окно без ресайза, в правом нижнем углу рабочей области — рядом с треем.
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }
        double scale = GetDpiForSystem() / 96.0;
        int w = (int)(340 * scale), h = (int)(430 * scale);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + area.Width - w - 12, area.Y + area.Height - h - 12, w, h));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDpiForSystem();

    void Reveal_Toggled(object sender, RoutedEventArgs e) =>
        PasswordInput.PasswordRevealMode = ((Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)sender).IsChecked == true
            ? Microsoft.UI.Xaml.Controls.PasswordRevealMode.Visible
            : Microsoft.UI.Xaml.Controls.PasswordRevealMode.Hidden;

    void Renew_Click(object sender, RoutedEventArgs e) => RemainingInput.Value = LimitInput.Value;

    void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.Password = PasswordInput.Password;
        double limit = !double.IsNaN(LimitInput.Value) && LimitInput.Value > 0 ? LimitInput.Value : _shownLimit;
        double remaining = double.IsNaN(RemainingInput.Value) ? _shownRemaining : RemainingInput.Value;
        // Трогаем счётчик, только если пользователь что-то поменял, — иначе не теряем трафик, набежавший пока окно открыто.
        if (limit != _shownLimit || remaining != _shownRemaining)
            _settings.UsedBytes = Math.Max(0, limit - remaining) * Settings.BytesPerGb;
        _settings.LimitGb = limit;
        _settings.AutoStart = AutoStartInput.IsChecked == true;
        _settings.Save();
        _settings.ApplyAutoStart();
        Close();
        _onSaved();
    }
}
