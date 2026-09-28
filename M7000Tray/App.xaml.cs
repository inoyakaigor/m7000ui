using System.Diagnostics;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace M7000Tray;

public partial class App : Application
{
    static readonly Uri EnvelopeUri = new(Path.Combine(AppContext.BaseDirectory, "Assets", "envelope.png"));
    static readonly Uri AppIconUri = new(Path.Combine(AppContext.BaseDirectory, "Assets", "app.png"));

    readonly Settings _settings = Settings.Load();
    Mutex? _singleInstance;
    TaskbarIcon? _tray;
    System.Drawing.Icon? _currentIcon;
    DispatcherQueueTimer? _timer;
    SettingsWindow? _settingsWindow;
    bool _polling;
    bool _loginRejected; // не долбим роутер неверным паролем, пока пользователь не сохранит новый
    string _status = "Загрузка…";
    RouterSnapshot? _lastSnap;
    double? _percentLeft; // последний известный остаток, %
    bool _warning;
    DateTime _pausedUntil; // «Не обновлять N минут» из меню трея

    public App()
    {
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown; // живём без окон
        UnhandledException += (_, e) => Settings.LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Settings.LogCrash(e.ExceptionObject);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new Mutex(true, "M7000Tray.SingleInstance", out bool first);
        if (!first) { Exit(); return; }

#if DEBUG
        SelfCheck();
#endif

        _settings.ApplyAutoStart();

        AppNotificationManager.Default.NotificationInvoked += (_, _) => { };
        AppNotificationManager.Default.Register("M7000", AppIconUri); // иконка приложения в заголовке, конверт — в самом уведомлении

        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("Настройки", OpenSettings));
        menu.Items.Add(MenuItem("Обновить", Resume)); // ручное обновление заодно снимает паузу
        var pause = new MenuFlyoutSubItem { Text = "Не обновлять" };
        foreach (int minutes in (int[])[5, 10, 20])
            pause.Items.Add(MenuItem($"{minutes} минут", () => Pause(minutes)));
        menu.Items.Add(pause);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Выход", Quit));

        var click = new XamlUICommand();
        click.ExecuteRequested += (_, _) => Process.Start(new ProcessStartInfo(RouterClient.RouterUrl + "login.html") { UseShellExecute = true });

        _tray = new TaskbarIcon
        {
            ContextFlyout = menu,
            ContextMenuMode = H.NotifyIcon.ContextMenuMode.PopupMenu,
            LeftClickCommand = click,
            NoLeftClickDelay = true,
        };
        UpdateTray(null);
        _tray.ForceCreate(enablesEfficiencyMode: false);

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(1);
        _timer.Tick += (_, _) => _ = PollAsync();
        _timer.Start();
        _ = PollAsync();
    }

    static MenuFlyoutItem MenuItem(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        var cmd = new XamlUICommand();
        cmd.ExecuteRequested += (_, _) => action();
        item.Command = cmd;
        return item;
    }

    async Task PollAsync()
    {
        if (_polling || DateTime.Now < _pausedUntil) return;
        // Ручная правка ini: подхватываем, а не затираем следующим Save(). Новый пароль — снова пробуем войти.
        if (_settings.ReloadIfChanged()) { _loginRejected = false; _settings.ApplyAutoStart(); }
        if (_loginRejected) return;
        if (string.IsNullOrEmpty(_settings.PasswordProtected))
        {
            _status = "Укажите пароль от админки роутера";
            UpdateTray(null, warning: true);
            return;
        }

        _polling = true;
        try
        {
            var snap = await Task.Run(() => RouterClient.PollAsync(_settings.Password));
            _lastSnap = snap;

            _settings.UsedBytes += Settings.RouterDelta(_settings.LastRouterTotal, snap.RouterTotalBytes);
            _settings.LastRouterTotal = snap.RouterTotalBytes;
            _settings.Save();
            ShowTraffic();

            foreach (var sms in NewSince(snap.LatestSms, _settings.LastSmsTime))
                ShowSms(sms);
            string? newest = snap.LatestSms.Select(s => s.ReceivedTime).DefaultIfEmpty(null).Max(StringComparer.Ordinal);
            if (newest is not null && string.CompareOrdinal(newest, _settings.LastSmsTime) > 0)
            {
                _settings.LastSmsTime = newest;
                _settings.Save();
            }
        }
        catch (LoginRejectedException)
        {
            _loginRejected = true;
            _status = "Роутер отклонил пароль. Опрос остановлен — введите пароль заново";
            UpdateTray(null, warning: true);
        }
        catch (Exception ex)
        {
            _status = $"Ошибка: {ex.Message}";
            UpdateTray(null, warning: true);
        }
        finally { _polling = false; }
    }

    void ShowTraffic()
    {
        double limit = _settings.LimitGb * Settings.BytesPerGb;
        double left = Math.Max(0, limit - _settings.UsedBytes);
        double percentLeft = Math.Clamp(left / limit * 100, 0, 100);
        _status = $"Осталось {left / Settings.BytesPerGb:0.00} из {_settings.LimitGb:0.##} ГБ ({percentLeft:0.#}%)";
        UpdateTray(percentLeft);
    }

    /// <summary>SMS новее last, от старых к новым. last == null — первый запуск: ничего не показываем, только запоминаем.</summary>
    internal static List<Sms> NewSince(IEnumerable<Sms> latest, string? last) =>
        last is null ? [] : latest.Where(s => string.CompareOrdinal(s.ReceivedTime, last) > 0)
                                  .OrderBy(s => s.ReceivedTime, StringComparer.Ordinal).ToList();

    static void ShowSms(Sms sms)
    {
        var toast = new AppNotificationBuilder()
            .SetAppLogoOverride(EnvelopeUri)
            .AddText($"Новое сообщение от {sms.From}")
            .AddText(sms.Content)
            .BuildNotification();
        AppNotificationManager.Default.Show(toast);
    }

    void UpdateTray(double? percentLeft, bool warning = false)
    {
        if (_tray is null) return;
        var old = _currentIcon;
        // warning — стандартный жёлтый треугольник Windows (SIID_WARNING), нужного для трея размера.
        _currentIcon = warning
            ? System.Drawing.SystemIcons.GetStockIcon(System.Drawing.StockIconId.Warning, System.Drawing.StockIconOptions.SmallIcon)
            : TrayIconRenderer.Render(percentLeft);
        _tray.Icon = _currentIcon;
        old?.Dispose();
        string tip = DateTime.Now < _pausedUntil ? $"{_status}\nОпрос приостановлен до {_pausedUntil:HH:mm}" : _status;
        _tray.ToolTipText = tip.Length > 120 ? tip[..120] : tip;

        if (percentLeft is not null) _percentLeft = percentLeft;
        _warning = warning;
        _settingsWindow?.Refresh(_status, _percentLeft, _warning, _lastSnap);
    }

    void Pause(int minutes)
    {
        _pausedUntil = DateTime.Now.AddMinutes(minutes);
        UpdateTray(_percentLeft, _warning);
    }

    void Resume()
    {
        _pausedUntil = default;
        _ = PollAsync();
    }

    void OpenSettings()
    {
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(_settings, () => { _loginRejected = false; ShowTraffic(); _ = PollAsync(); });
        _settingsWindow.Refresh(_status, _percentLeft, _warning, _lastSnap);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate();
    }

    void Quit()
    {
        _timer?.Stop();
        _tray?.Dispose();
        AppNotificationManager.Default.Unregister();
        Exit();
    }

#if DEBUG
    static void SelfCheck()
    {
        Debug.Assert(TrayIconRenderer.ColorFor(100) == TrayIconRenderer.ColorFor(90));
        Debug.Assert(TrayIconRenderer.ColorFor(89.99) == TrayIconRenderer.ColorFor(40));
        Debug.Assert(TrayIconRenderer.ColorFor(39.99) == TrayIconRenderer.ColorFor(10));
        Debug.Assert(TrayIconRenderer.ColorFor(9.99) == TrayIconRenderer.ColorFor(0));
        Debug.Assert(TrayIconRenderer.ColorFor(90) != TrayIconRenderer.ColorFor(89.99));

        Debug.Assert(Settings.RouterDelta(null, 500) == 0);        // первый опрос — только база
        Debug.Assert(Settings.RouterDelta(100, 150) == 50);
        Debug.Assert(Settings.RouterDelta(900, 30) == 30);         // роутер обнулил счётчик

        Debug.Assert(Glyphs.Battery(0, false) == "\uE850" && Glyphs.Battery(95, false) == "\uE859" && Glyphs.Battery(100, false) == "\uE83F");
        Debug.Assert(Glyphs.Battery(0, true) == "\uE85A" && Glyphs.Battery(99, true) == "\uE862" && Glyphs.Battery(100, true) == "\uE83E");

        Debug.Assert(Glyphs.Signal(null) == "\uE871" && Glyphs.Signal(0) == "\uE86C" && Glyphs.Signal(4) == "\uE870" && Glyphs.Signal(9) == "\uE870");

        Sms a = new("A", "2026-09-23 17:39:30", "a"), b = new("B", "2026-09-23 17:40:21", "b");
        Debug.Assert(NewSince([b, a], null).Count == 0);
        Debug.Assert(NewSince([b, a], "2026-09-23 17:39:30") is [var only] && only == b);
        Debug.Assert(NewSince([b, a], "2026-01-01 00:00:00") is [var x, var y] && x == a && y == b);
    }
#endif
}
