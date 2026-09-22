using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.Web.WebView2.Core;
using OpenCodeSpend.Data;

namespace OpenCodeSpend.Desktop;

public partial class MainWindow : Window
{
    private WebApplication? _app;

    /// <summary>Локальный адрес панели: любой свободный порт, только этот компьютер.</summary>
    private readonly string _url = $"http://127.0.0.1:{FreePort()}";

    /// <summary>Любой свободный порт: конфликтов с другими приложениями не будет.</summary>
    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    private const string OpenCodeHome = "https://opencode.ai";



    private Pusher? _pusher;
    private string? _pusherServerUrl;
    private System.Windows.Threading.DispatcherTimer? _pushTimer;
    private System.Windows.Threading.DispatcherTimer? _fastTimer;
    private System.Windows.Threading.DispatcherTimer? _watchTimer;
    private LinkStore? _links;

    /// <summary>
    /// Локальная панель приложения: своя база, свой сбор данных, отправка на сервер.
    /// Вход в аккаунт происходит в opencode (Google или GitHub), привязка к серверу — по коду.
    /// </summary>
    private static string[] CollectorArgs() => new[]
    {
        // локальная панель работает без входа: она и так только на этом компьютере,
        // а аккаунт подтягивается из привязки к серверу
        "--Spend:GoogleClientId=", "--Spend:GoogleClientSecret=",
        "--Spend:GitHubClientId=", "--Spend:GitHubClientSecret=",
    };

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Тёмный заголовок окна (кнопки свернуть/развернуть/закрыть).</summary>
    private void ApplyDarkTitleBar()
    {
        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var on = 1;
            if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(h, 19, ref on, sizeof(int));
        }
        catch { }
    }

    public MainWindow()
    {
        InitializeComponent();
        LoginBtn.Click += (_, _) => StartOpencodeLogin();
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) =>
        {
            try { _app?.StopAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
            Environment.Exit(0);
        };
    }

    private async Task StartAsync()
    {
        try
        {
            OverlayText.Text = "запуск сервиса…";
            // Строим хост на фоновом потоке: в Server.Build есть синхронное ожидание async-инициализации,
            // а на UI-потоке WPF это даёт дедлок (SynchronizationContext).
            _app = await Task.Run(() => Server.Build(CollectorArgs(), _url));
            await _app.StartAsync();
            _links = _app.Services.GetService(typeof(LinkStore)) as LinkStore;

            await Web.EnsureCoreWebView2Async();
            Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0a, 0x0d, 0x13);
            Web.CoreWebView2.Settings.AreDevToolsEnabled = true;
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Web.CoreWebView2.NavigationCompleted += OnNavigation;
            // После входа opencode открывает свою консоль — она нам не нужна.
            // Отменяем эту загрузку ДО отрисовки и сразу уводим в профиль.
            Web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                var u = e.Uri ?? "";
                if (_capturing && u.StartsWith(OpenCodeHome, StringComparison.OrdinalIgnoreCase)
                    && !u.Contains("/authorize") && !u.Contains("/auth/callback")
                    && !u.Contains("/console/login") && !u.Contains("/console/auth/"))
                {
                    e.Cancel = true;
                    _capturing = false;
                    _ = FinishCaptureAsync();
                }
            };

            // навигация идёт при скрытом окне: пользователь видит загрузку, а не страницу ошибки
            GoToServer();

            await Task.Delay(250);

            StartPusher();

            // пока кода нет — ждём подключения, потом запускаем отправку и вход в opencode
            _watchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _watchTimer.Tick += (_, _) =>
            {
                var link = _links?.Get();
                if (link is null)
                {
                    StopPusher();                       // отвязались от сервера — отправка прекращается
                }
                else if (_pusher is null || _pusherServerUrl != link.ServerUrl)
                {
                    StartPusher();                      // привязались или сменили сервер — начинаем сразу
                    GoToServer();
                }
                if (!CaptureExists() && !_capturing && !InOpenCodeFlow())
                    EnsureOpencodeConnected();
            };
            _watchTimer.Start();

            EnsureOpencodeConnected();
        }
        catch (Exception e)
        {
            OverlayText.Text = "ошибка запуска: " + e.Message;
        }
    }

    /// <summary>Показываем локальную панель: у приложения своя база и свои данные.</summary>
    private void GoToServer() => Navigate(_url + "/");

    private void Navigate(string url)
    {
        try { if (Web.CoreWebView2 is not null) Web.CoreWebView2.Navigate(url); } catch { }
    }

    // ---------- вход в аккаунт opencode прямо в этом окне ----------

    private readonly Queue<string> _queue = new();
    private readonly HashSet<string> _visited = new();
    private bool _capturing, _firstLoad, _loginModal;

    /// <summary>Выход: стираем ВСЕ куки браузера, забываем сессию opencode и перезапускаем приложение.</summary>
    private async Task LogoutAndRestartAsync()
    {
        try
        {
            Web.CoreWebView2?.CookieManager.DeleteAllCookies();
            // чистим и кэш, и всё остальное состояние браузера
            if (Web.CoreWebView2 is not null)
                await Web.CoreWebView2.Profile.ClearBrowsingDataAsync(Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.AllProfile);
            if (File.Exists(CapturePath)) File.Delete(CapturePath);
        }
        catch { }
        await Task.Delay(500);   // даём браузеру записать очистку
        RestartApp();
    }

    /// <summary>Выход = перезапуск приложения с чистого листа: никаких застрявших сессий.</summary>
    private static void RestartApp()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe)) System.Diagnostics.Process.Start(exe);
        }
        catch { }
        Environment.Exit(0);
    }

    /// <summary>Заглушка — локальная страница внутри того же окна.
    /// Так надёжнее: WebView2 нативное, и прятать его переключением Visibility нельзя.</summary>
    private void ShowOverlay(string text, bool loginButton)
    {
        _loginModal = loginButton;
        Navigate($"{_url}/wait?t={Uri.EscapeDataString(text)}&login={(loginButton ? 1 : 0)}");
    }

    private void HideOverlay()
    {
        _loginModal = false;
        Overlay.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
    }

    /// <summary>Начинаем вход в opencode: показываем окно и уводим его на их страницу входа.</summary>
    private void StartOpencodeLogin()
    {
        _capturing = true;
        _queue.Clear();
        _visited.Clear();
        HideOverlay();
        Navigate(_url + "/opencode");
    }

    /// <summary>Если аккаунт opencode не подключён — показываем модалку с кнопкой входа.</summary>
    private void EnsureOpencodeConnected()
    {
        if (_capturing || CaptureExists()) return;
        ShowOverlay("Аккаунт opencode не подключён", loginButton: true);
    }

    /// <summary>Собирает cookie сессии opencode из браузера.</summary>
    private async Task<string> ReadCookieAsync()
    {
        try
        {
            var parts = new List<string>();
            foreach (var c in await Web.CoreWebView2.CookieManager.GetCookiesAsync(OpenCodeHome))
                parts.Add($"{c.Name}={c.Value}");
            return string.Join("; ", parts);
        }
        catch { return ""; }
    }

    /// <summary>Перед новым входом забываем старую сессию opencode, чтобы вход был настоящим.</summary>
    private async Task ClearOpenCodeCookiesAsync()
    {
        try
        {
            if (Web.CoreWebView2 is null) return;
            foreach (var host in new[] { OpenCodeHome, "https://auth.opencode.ai" })
            {
                var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync(host);
                foreach (var c in cookies) Web.CoreWebView2.CookieManager.DeleteCookie(c);
            }
        }
        catch { }
    }

    private static string CapturePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode-spend", "ocspend-capture.json");

    private static bool CaptureExists()
    {
        try { return File.Exists(CapturePath); } catch { return false; }
    }

    /// <summary>Идёт вход в opencode или у его провайдера — не мешаем, не переключаем окно.</summary>
    private bool InOpenCodeFlow()
    {
        var u = Web.Source?.AbsoluteUri ?? "";
        return u.Contains("opencode.ai") || u.Contains("google.com") || u.Contains("github.com");
    }

    private void OnNavigation(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var current = Web.Source?.AbsoluteUri ?? "";

        // первая удачная загрузка локальной панели — показываем её вместо экрана загрузки
        // первая удачная загрузка локальной страницы — убираем стартовую заглушку WPF
        if (!_firstLoad && e.IsSuccess && current.StartsWith(_url, StringComparison.OrdinalIgnoreCase))
        {
            _firstLoad = true;
            Overlay.Visibility = Visibility.Collapsed;
        }

        // выход: сначала полностью чистим куки, потом перезапускаем приложение с чистого листа
        if (current.Contains("/auth/opencode-logout"))
        {
            _ = LogoutAndRestartAsync();
            return;
        }

        // НАЧАЛСЯ ВХОД: наша страница /wait → /opencode → auth.opencode.ai.
        // Включаем захват, иначе после ввода пароля нас никуда не перекинет.
        if (current.Contains("/opencode") || current.Contains("auth.opencode.ai") || !CaptureExists())
        {
            if (!_capturing)
            {
                _capturing = true;
                _queue.Clear();
                _visited.Clear();
            }
        }



        if (!_capturing) return;
        var uri = current;

        // вход выполнен: opencode перестал показывать authorize/callback
        // Вход завершён: opencode вернул нас на свою страницу. Cookie уже стоит — забираем её
        // сразу, без прогулки по страницам (она и давала лишние мигания).
        if (_capturing && uri.StartsWith(OpenCodeHome) && !uri.Contains("/authorize") && !uri.Contains("/auth/callback")
            && !uri.Contains("/console/login") && !uri.Contains("/console/auth/"))
        {
            _capturing = false;
            _ = FinishCaptureAsync();
        }
    }

    /// <summary>Сервер отверг cookie — забываем её и заново показываем вход в opencode.</summary>
    private void ReloginOpencode()
    {
        try { if (File.Exists(CapturePath)) File.Delete(CapturePath); } catch { }
        _queue.Clear();
        _visited.Clear();
        _capturing = false;
        _pusher = null;
        ShowOverlay("Аккаунт opencode отключился — войдите заново", loginButton: true);
    }

    /// <summary>Забирает cookie сессии opencode, сохраняет её и сразу отдаёт серверу.</summary>
    private async Task FinishCaptureAsync()
    {
        var cookie = "";
        try
        {
            cookie = await ReadCookieAsync();
            if (cookie.Length == 0)
            {
                // сессия могла записаться чуть позже — даём ей мгновение
                await Task.Delay(900);
                cookie = await ReadCookieAsync();
            }

            var dir = Path.GetDirectoryName(CapturePath)!;
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(CapturePath, JsonSerializer.Serialize(new
            {
                savedAt = DateTimeOffset.Now,
                cookie,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }

        // кладём cookie в локальное состояние: панель сразу начинает тянуть профиль opencode
        try
        {
            if (_app?.Services.GetService(typeof(OpenCodeSpend.Data.SpendStore)) is OpenCodeSpend.Data.SpendStore store
                && cookie.Length > 0)
                await store.SetStateAsync("profile_cookie", cookie, null);
        }
        catch { }
        if (_pusher is not null) await _pusher.SyncAsync();   // cookie уходит на сервер сразу
        GoToServer();                                          // один переход — сразу в панель
        await Task.Delay(500);
        HideOverlay();
    }

    // ---------- отправка данных на сервер ----------

    private void StopPusher()
    {
        _pushTimer?.Stop(); _pushTimer = null;
        _fastTimer?.Stop(); _fastTimer = null;
        _pusher = null;
        _pusherServerUrl = null;
    }

    private void StartPusher()
    {
        try
        {
            StopPusher();
            var link = _links?.Get();
            if (link is null || string.IsNullOrWhiteSpace(link.ServerUrl) || string.IsNullOrWhiteSpace(link.Token)) return;

            var pusher = new Pusher(link.ServerUrl!, link.Token!, _url, link.DeviceId);
            _pusher = pusher;
            _pusherServerUrl = link.ServerUrl;
            // единый канал: локальные данные и команды одним запросом раз в секунду
            _pushTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _pushTimer.Tick += async (_, _) =>
            {
                await pusher.SyncAsync();
                // сервер отключил этот ПК от аккаунта — показываем кнопку «Подключить сервер»
                if (pusher.Revoked)
                {
                    _links?.Clear();
                    StopPusher();
                    GoToServer();
                    return;
                }
                OverlayText.Text = pusher.LastError is null
                    ? $"push ok: usage {pusher.PushedUsage}, site {pusher.PushedSite}"
                    : "push: " + pusher.LastError;
            };
            _pushTimer.Start();
            _ = pusher.SyncAsync();

            // мгновенная отправка: как только opencode сообщил о событии — отправляем снимок сразу (с небольшим склеиванием)
            _fastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _fastTimer.Tick += async (_, _) =>
            {
                _fastTimer!.Stop();
                if (_pusher is not null) await _pusher.SyncAsync();
            };
            try
            {
                if (_app?.Services.GetService(typeof(OpenCodeSpend.Data.OpencodeControl)) is OpenCodeSpend.Data.OpencodeControl ctl)
                    ctl.Events.Changed += () => Dispatcher.BeginInvoke(new Action(() => { _fastTimer?.Stop(); _fastTimer?.Start(); }));
            }
            catch { }
        }
        catch { }
    }
}
