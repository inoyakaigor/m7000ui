using System.Globalization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace M7000Tray;

public sealed partial class SettingsWindow : Window
{
    readonly Settings _settings;
    readonly Action _onSaved;
    // Что окно само подставило в поля. Отличается от введённого — значит, пользователь правил, и опрос поле не трогает.
    double _shownLimit, _shownRemaining;

    public SettingsWindow(Settings settings, Action onSaved)
    {
        InitializeComponent();
        _settings = settings;
        _onSaved = onSaved;

        PasswordInput.Password = settings.Password;
        LimitInput.Value = _shownLimit = settings.LimitGb;
        RemainingInput.Value = _shownRemaining = CurrentRemainingGb();
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

        // Высота — по содержимому: иначе любая новая строка в XAML обрезает кнопку «Сохранить».
        ((FrameworkElement)Content).Loaded += (_, _) =>
        {
            var root = (FrameworkElement)Content;
            double s = root.XamlRoot.RasterizationScale;
            root.Measure(new Windows.Foundation.Size(AppWindow.ClientSize.Width / s, double.PositiveInfinity));
            AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(AppWindow.ClientSize.Width, (int)Math.Ceiling(root.DesiredSize.Height * s)));
            var size = AppWindow.Size;
            AppWindow.Move(new Windows.Graphics.PointInt32(area.X + area.Width - size.Width - 12, area.Y + area.Height - size.Height - 12));
        };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDpiForSystem();

    void Reveal_Toggled(object sender, RoutedEventArgs e) =>
        PasswordInput.PasswordRevealMode = ((Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)sender).IsChecked == true
            ? Microsoft.UI.Xaml.Controls.PasswordRevealMode.Visible
            : Microsoft.UI.Xaml.Controls.PasswordRevealMode.Hidden;

    double CurrentRemainingGb() => Math.Round(Math.Max(0, _settings.LimitGb - _settings.UsedBytes / Settings.BytesPerGb), 2);

    /// <summary>Свежие данные после опроса/сохранения. percentLeft == null — данных ещё не было.</summary>
    public void Refresh(string status, double? percentLeft, bool error, RouterSnapshot? snap)
    {
        StatusText.Text = status;
        SimText.Text = string.IsNullOrEmpty(snap?.SimNumber) ? "Номер SIM: —" : snap.SimNumber;

        StatusBar.IsIndeterminate = percentLeft is null && !error;
        StatusBar.ShowError = error;
        if (percentLeft is { } p)
        {
            StatusBar.Value = p;
            var c = TrayIconRenderer.ColorFor(p);
            StatusBar.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(c.A, c.R, c.G, c.B));
        }

        if (snap?.BatteryPercent is { } battery)
        {
            BatteryLevel.Glyph = Glyphs.Battery(battery, snap.Charging);
            ToolTipService.SetToolTip(BatteryLevel, new ToolTip { Content = $"Заряд {battery}%, {(snap.Charging ? "заряжается" : "не заряжается")}" });
        }
        if (snap is not null)
        {
            SignalStatus.Glyph = Glyphs.Signal(snap.SignalLevel);
            RoamingIcon.Visibility = snap.Roaming ? Visibility.Visible : Visibility.Collapsed;
            NetworkIcon.Visibility = snap.Lte ? Visibility.Visible : Visibility.Collapsed;
            // На всю группу: значки 4G и роуминга перекрывают полоски сигнала.
            ToolTipService.SetToolTip(SignalGroup, new ToolTip { Content = SignalTip(snap) });
        }

        if (ReadNumber(LimitInput) == _shownLimit) LimitInput.Value = _shownLimit = _settings.LimitGb;
        if (ReadNumber(RemainingInput) == _shownRemaining) RemainingInput.Value = _shownRemaining = CurrentRemainingGb();
    }

    static string SignalTip(RouterSnapshot s)
    {
        if (s.SignalLevel is not { } level) return "Нет сети";
        string F(double? v, string unit) => v is { } d ? $"{d:0.00} {unit}" : "—";
        string tip = $"Уровень {level}/4 · RSRP {F(s.Rsrp, "dBm")} · RSRQ {F(s.Rsrq, "dB")} · SNR {F(s.Snr, "dB")}";
        if (s.Lte) tip += " · 4G";
        if (s.Roaming) tip += " · роуминг";
        return tip;
    }

    // Value и Text у NumberBox обновляются только по Enter/потере фокуса — читаем то, что сейчас набрано во внутреннем TextBox.
    static double ReadNumber(NumberBox box)
    {
        string text = FindTextBox(box)?.Text ?? box.Text;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ? v : box.Value;
    }

    static TextBox? FindTextBox(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox tb) return tb;
            if (FindTextBox(child) is { } found) return found;
        }
        return null;
    }

    void Renew_Click(object sender, RoutedEventArgs e) => RemainingInput.Value = ReadNumber(LimitInput);

    void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.Password = PasswordInput.Password;
        double limitIn = ReadNumber(LimitInput), remainingIn = ReadNumber(RemainingInput);
        double limit = !double.IsNaN(limitIn) && limitIn > 0 ? limitIn : _shownLimit;
        double remaining = double.IsNaN(remainingIn) || remainingIn < 0 ? _shownRemaining : remainingIn;
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
