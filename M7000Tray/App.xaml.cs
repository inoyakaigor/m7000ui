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
    static readonly Uri AppIconUri = new(Path.Combine(AppContext.BaseDirectory, "Assets", "app-plate.png"));
    // Windows в тёмной теме инвертирует тёмные иконки в заголовке уведомления (роутер становился негативом).
    // На светлой подложке иконка считается светлой и выводится как есть.

    readonly Settings _settings = Settings.Load();
    Mutex? _singleInstance;
    TaskbarIcon? _tray;
    System.Drawing.Icon? _currentIcon;
    DispatcherQueueTimer? _timer;
    DispatcherQueueTimer? _networkTimer; // опрос через пару секунд после смены сети (держим ссылку от GC)
    bool _foreignNetwork; // на 192.168.0.1 не наш роутер
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

        // Кнопка «Пометить прочитанным»: событие приходит не в UI-потоке.
        var ui = DispatcherQueue.GetForCurrentThread();
        AppNotificationManager.Default.NotificationInvoked += (sender, e) => ui.TryEnqueue(() => _ = HandleNotificationAsync(e.Arguments));
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
        click.ExecuteRequested += (_, _) =>
        {
            // Роутер держит одну сессию: наш опрос раз в минуту выкидывает из админки. Даём 5 минут поработать.
            Pause(5);
            Process.Start(new ProcessStartInfo(RouterClient.RouterUrl + "login.html") { UseShellExecute = true });
        };

        _tray = new TaskbarIcon
        {
            ContextFlyout = menu,
            ContextMenuMode = H.NotifyIcon.ContextMenuMode.PopupMenu,
            LeftClickCommand = click,
            NoLeftClickDelay = true,
        };
        UpdateTray(null);
        _tray.ForceCreate(enablesEfficiencyMode: false);

        // Приложение было закрыто, а кнопку нажали в центре уведомлений — Windows запустила нас ради неё.
        if (Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs() is { Kind: Microsoft.Windows.AppLifecycle.ExtendedActivationKind.AppNotification } activation)
            _ = HandleNotificationAsync(((AppNotificationActivatedEventArgs)activation.Data).Arguments);

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(1);
        _timer.Tick += (_, _) => _ = PollAsync();
        _timer.Start();

        // Сеть появилась или сменилась (выход из сна, другой Wi-Fi) — опросить сразу, не ждать минуту.
        // Событие приходит пачкой и не в UI-потоке: перезапускаем одноразовый таймер на 3 с.
        _networkTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _networkTimer.Interval = TimeSpan.FromSeconds(3);
        _networkTimer.IsRepeating = false;
        _networkTimer.Tick += (_, _) => _ = PollAsync();
        var netUi = DispatcherQueue.GetForCurrentThread();
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (sender, e) => netUi.TryEnqueue(() => { _networkTimer.Stop(); _networkTimer.Start(); });
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
            if (!await IsOurRouterAsync()) return;

            var snap = await Task.Run(() => RouterClient.PollAsync(_settings.Password));
            _lastSnap = snap;
            if (_settings.RouterMac is null && snap.RouterMac is not null) _settings.RouterMac = snap.RouterMac; // запомнили свой роутер

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
        catch (Exception ex) when (RouterClient.IsNetworkError(ex))
        {
            _status = "Нет связи с роутером";
            UpdateTray(null, warning: true);
        }
        catch (Exception ex)
        {
            _status = $"Ошибка: {ex.Message}";
            Settings.LogCrash(ex); // в подсказке только текст, стек — в crash.log
            UpdateTray(null, warning: true);
        }
        finally { _polling = false; }
    }

    /// <summary>
    /// true — на 192.168.0.1 наш роутер (или он ещё не запомнен), можно логиниться.
    /// false — нет связи или чужая сеть: статус и иконка уже выставлены, логина не будет.
    /// </summary>
    async Task<bool> IsOurRouterAsync()
    {
        string? mac = await Task.Run(RouterClient.GatewayMac);
        if (mac is null)
        {
            _foreignNetwork = false;
            _status = "Нет связи с роутером";
            UpdateTray(null, warning: true);
            return false;
        }
        _foreignNetwork = _settings.RouterMac is { } known && mac != known;
        if (_foreignNetwork)
        {
            _status = "Другая сеть — роутер M7000 не найден";
            UpdateTray(null);
            return false;
        }
        return true;
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
            .AddText(sms.Content);
        if (sms.Index >= 0)
            toast.AddButton(new AppNotificationButton("Пометить прочитанным").AddArgument("markRead", sms.Index.ToString()));
        AppNotificationManager.Default.Show(toast.BuildNotification());
    }

    async Task HandleNotificationAsync(IDictionary<string, string> args)
    {
        if (!args.TryGetValue("markRead", out var raw) || !int.TryParse(raw, out int index)) return;
        if (string.IsNullOrEmpty(_settings.PasswordProtected) || _loginRejected) return;
        try
        {
            if (!await IsOurRouterAsync()) return; // чужой роутер с тем же адресом — не логинимся
            await Task.Run(() => RouterClient.MarkReadAsync(_settings.Password, index));
            await PollAsync(); // обновить конверт в трее
        }
        catch (Exception ex)
        {
            Settings.LogCrash(ex);
            _status = $"Не удалось пометить SMS прочитанным: {ex.Message}";
            UpdateTray(_percentLeft, warning: true);
        }
    }

    void UpdateTray(double? percentLeft, bool warning = false)
    {
        if (_tray is null) return;
        var old = _currentIcon;
        // warning — стандартный жёлтый треугольник Windows (SIID_WARNING), нужного для трея размера.
        // Чужая сеть — обычная иконка приложения: это не ошибка, просто роутера рядом нет.
        _currentIcon = warning
            ? System.Drawing.SystemIcons.GetStockIcon(System.Drawing.StockIconId.Warning, System.Drawing.StockIconOptions.SmallIcon)
            : _foreignNetwork
            ? new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "M7000.ico"), TrayIconRenderer.IconSize, TrayIconRenderer.IconSize)
            : TrayIconRenderer.Render(percentLeft,
                TrayIconRenderer.ChooseBadge(_lastSnap?.BatteryPercent, _lastSnap?.UnreadSms ?? 0),
                _lastSnap?.BatteryPercent ?? 0, _lastSnap?.Charging ?? false, paused: DateTime.Now < _pausedUntil);
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
        var until = DateTime.Now.AddMinutes(minutes);
        if (until > _pausedUntil) _pausedUntil = until; // не укорачиваем уже поставленную паузу
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

        Debug.Assert(TrayIconRenderer.ChooseBadge(5, 3) == TrayBadge.LowBattery);   // батарея важнее SMS
        Debug.Assert(TrayIconRenderer.ChooseBadge(50, 1) == TrayBadge.Envelope);
        Debug.Assert(TrayIconRenderer.ChooseBadge(50, 0) == TrayBadge.Percent);
        Debug.Assert(TrayIconRenderer.ChooseBadge(null, 0) == TrayBadge.Percent);   // нет данных о батарее — не пугаем

        Debug.Assert(RouterClient.NormalizeMac("3c:6a:d2:24:dc:a0") == "3C6AD224DCA0");
        Debug.Assert(RouterClient.NormalizeMac("3C-6A-D2-24-DC-A0") == RouterClient.NormalizeMac("3C:6A:D2:24:DC:A0"));
        Debug.Assert(RouterClient.IsNetworkError(new HttpRequestException("x", new System.Net.Sockets.SocketException(10051))));
        Debug.Assert(!RouterClient.IsNetworkError(new LoginRejectedException()));

        Sms a = new("A", "2026-09-23 17:39:30", "a"), b = new("B", "2026-09-23 17:40:21", "b");
        Debug.Assert(NewSince([b, a], null).Count == 0);
        Debug.Assert(NewSince([b, a], "2026-09-23 17:39:30") is [var only] && only == b);
        Debug.Assert(NewSince([b, a], "2026-01-01 00:00:00") is [var x, var y] && x == a && y == b);
    }
#endif
}
