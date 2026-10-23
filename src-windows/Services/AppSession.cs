using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Mihomo.Desktop.Services;

public sealed class AppSession : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly HostChannel host = new();
    private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource controllerLifetime = new();
    private string controllerIdentity = "";
    private readonly string appearancePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mihomo Optimized", "native-appearance.json");
    private Task dirtySync = Task.CompletedTask;
    private bool dirty;
    private bool started;
    private bool exiting;
    private bool launchCheckConsumed;
    private Task<JsonElement>? updateCheck;
    public JsonElement Status { get; private set; } = JsonSerializer.SerializeToElement(new { phase = "connecting" });
    public JsonElement Update { get; private set; } = JsonSerializer.SerializeToElement(new { available = false });
    public string Theme { get; private set; } = "system";
    public bool Ready { get; private set; }
    public bool IsExiting => exiting;
    public bool IsUpdating { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? StatusChanged;
    public event Action<string, JsonElement>? HostEvent;
    public event Action<string, bool>? Notification;
    public event Action<string>? NavigateRequested;
    public event Action? ExitRequested;
    public event Action? ThemeChanged;

    public bool IsDirty
    {
        get => dirty;
        set
        {
            if (dirty == value) return;
            dirty = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
            dirtySync = SyncDirtyAsync(dirtySync, value);
        }
    }

    public AppSession()
    {
        host.Event += OnHostEvent;
        host.Disconnected += error => Dispatch(() =>
        {
            if (exiting) return;
            Ready = false;
            Status = JsonSerializer.SerializeToElement(new { phase = "error", lastError = error.Message });
            StatusChanged?.Invoke(this, EventArgs.Empty);
            Notify(error.Message, true);
        });
        try
        {
            if (File.Exists(appearancePath))
            {
                using var saved = JsonDocument.Parse(File.ReadAllText(appearancePath));
                string value = HostChannel.Text(saved.RootElement, "theme", "system");
                if (value is "light" or "dark" or "system") Theme = value;
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;
    }

    public async Task StartAsync()
    {
        if (started) return;
        host.Start();
        started = true;
        ApplyTheme();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        ApplyStatus(await host.CallAsync("desktop_status", null, timeout.Token));
        _ = PollAsync();
        _ = CheckUpdateOnLaunchAsync();
    }

    public async Task<JsonElement> CommandAsync(string method, object? args = null, CancellationToken cancellationToken = default)
    {
        if (method == "desktop_install_update") return await InstallUpdateAsync(args, cancellationToken);
        RejectDuringUpdate(method);
        if (method == "desktop_set_dirty")
        {
            var parameters = JsonSerializer.SerializeToElement(args, HostChannel.JsonOptions);
            if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("dirty", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || parameters.EnumerateObject().Count() != 1)
                throw new DesktopException("invalid_arguments", "桌面操作参数无效");
            await SetDirtyAsync(value.GetBoolean()).WaitAsync(cancellationToken);
            return JsonSerializer.SerializeToElement<object?>(null);
        }
        await CurrentDirtySync().WaitAsync(cancellationToken);
        RejectDuringUpdate(method);
        if (method == "desktop_check_update")
        {
            launchCheckConsumed = true;
            return await CheckUpdateAsync(cancellationToken);
        }
        JsonElement result = await host.CallAsync(method, args, cancellationToken);
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("phase", out _)) ApplyStatus(result);
        if (method == "desktop_exit" && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("exiting", out var leaving) && leaving.ValueKind == JsonValueKind.True) ExpectExit();
        return result;
    }

    private void RejectDuringUpdate(string method)
    {
        if (IsUpdating && method != "desktop_status")
            throw new DesktopException("update_in_progress", "应用正在下载或安装更新，请等待更新完成。");
    }

    private async Task<JsonElement> InstallUpdateAsync(object? args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RejectDuringUpdate("desktop_install_update");
        if (IsDirty) throw new DesktopException("unsaved_config", "请先保存或放弃配置修改，再安装应用更新。");
        // The host cannot cancel an accepted installation. Keep the edit lock
        // until its real result, even if the initiating page is hidden.
        IsUpdating = true;
        StatusChanged?.Invoke(this, EventArgs.Empty);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUpdating)));
        try
        {
            await CurrentDirtySync().WaitAsync(lifetime.Token);
            if (IsDirty) throw new DesktopException("unsaved_config", "请先保存或放弃配置修改，再安装应用更新。");
            var result = await host.CallAsync("desktop_install_update", args, lifetime.Token);
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("exiting", out var leaving) && leaving.ValueKind == JsonValueKind.True) ExpectExit();
            return result;
        }
        finally
        {
            if (!exiting)
            {
                IsUpdating = false;
                StatusChanged?.Invoke(this, EventArgs.Empty);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUpdating)));
            }
        }
    }

    public Task SetDirtyAsync(bool value)
    {
        IsDirty = value;
        return CurrentDirtySync();
    }

    private Task CurrentDirtySync()
    {
        // A transient full host queue must not poison every later command.
        // Retry the current authoritative value once on the next operation.
        if (dirtySync.IsFaulted || dirtySync.IsCanceled) dirtySync = SyncDirtyAsync(dirtySync, dirty);
        return dirtySync;
    }

    private async Task SyncDirtyAsync(Task previous, bool value)
    {
        try { await previous; } catch (Exception) { /* A later authoritative value can repair a failed write. */ }
        if (!started) return;
        try { await host.CallAsync("desktop_set_dirty", new { dirty = value }, lifetime.Token); }
        catch (Exception error) { Notify(error.Message, true); throw; }
    }

    private void ApplyStatus(JsonElement status)
    {
        Dispatch(() =>
        {
            Status = status.Clone();
            string identity = Status.TryGetProperty("controller", out var controller) && controller.ValueKind == JsonValueKind.Object
                ? HostChannel.Text(controller, "url") + "\n" + HostChannel.Text(controller, "secret") : "";
            if (identity != controllerIdentity)
            {
                controllerIdentity = identity;
                var previous = controllerLifetime;
                controllerLifetime = new CancellationTokenSource();
                previous.Cancel();
                previous.Dispose();
            }
            Ready = true;
            StatusChanged?.Invoke(this, EventArgs.Empty);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        });
    }

    private void OnHostEvent(string name, JsonElement data)
    {
        if (name == "desktop-status") ApplyStatus(data);
        if (name == "desktop-exit-requested" && HostChannel.Text(data, "reason") == "update")
        {
            exiting = true;
            host.ExpectExit();
        }
        Dispatch(() => HostEvent?.Invoke(name, data));
    }

    private async Task PollAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try { ApplyStatus(await host.CallAsync("desktop_status", null, timeout.Token)); }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
                catch (DesktopException) { if (!Ready) break; }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<JsonElement> CheckUpdateAsync(CancellationToken cancellationToken)
    {
        Task<JsonElement> check = updateCheck ??= host.CallAsync("desktop_check_update", null, lifetime.Token);
        try
        {
            Update = await check.WaitAsync(cancellationToken);
            return Update;
        }
        finally { if (check.IsCompleted && ReferenceEquals(updateCheck, check)) updateCheck = null; }
    }

    private async Task CheckUpdateOnLaunchAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token);
            while (!launchCheckConsumed && (HostChannel.Text(Status, "phase") is "connecting" or "starting" or "stopping" || IsDirty || IsUpdating))
                await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
            if (launchCheckConsumed) return;
            launchCheckConsumed = true;
            var update = await CheckUpdateAsync(lifetime.Token);
            if (update.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.True)
                Notify("发现新版本 " + HostChannel.Text(update, "version") + "，可在设置中更新。");
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Automatic checks do not interrupt offline startup; manual checks show their error. */ }
    }

    private (Uri Uri, string Secret) Endpoint(string path)
    {
        if (Status.ValueKind != JsonValueKind.Object || !Status.TryGetProperty("controller", out var controller) || controller.ValueKind != JsonValueKind.Object)
            throw new DesktopException("core_stopped", "请先启动内核");
        string secret = HostChannel.Text(controller, "secret");
        if (!Uri.TryCreate(HostChannel.Text(controller, "url"), UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" || !IPAddress.TryParse(endpoint.Host, out var address) || !IPAddress.IsLoopback(address) || endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || string.IsNullOrWhiteSpace(secret))
            throw new DesktopException("controller_invalid", "本地控制器地址无效");
        if (path.Contains("://", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('#') || path.Contains('\\'))
            throw new ArgumentException("API path must remain on the managed controller", nameof(path));
        var uri = new Uri(endpoint, "/" + path.TrimStart('/'));
        if (uri.Host != endpoint.Host || uri.Port != endpoint.Port) throw new ArgumentException("Invalid controller path", nameof(path));
        return (uri, secret);
    }

    public async Task<JsonElement> ApiAsync(HttpMethod method, string path, object? body = null, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? headers = null)
    {
        var (uri, secret) = Endpoint(path);
        var controllerToken = controllerLifetime.Token;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken, controllerToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            if (headers is not null)
                foreach (var header in headers)
                {
                    if (header.Key is not ("Idempotency-Key" or "X-Mihomo-Instance-ID")) throw new ArgumentException("Unsupported API header");
                    request.Headers.Add(header.Key, header.Value);
                }
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, HostChannel.JsonOptions), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            const int maximum = 32 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximum) throw new DesktopException("response_too_large", "内核返回的数据过大，请缩小查询范围");
            using var data = new MemoryStream();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[16384];
            int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (data.Length + count > maximum) throw new DesktopException("response_too_large", "内核返回的数据过大");
                data.Write(buffer, 0, count);
            }
            timeout.Token.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode)
            {
                string detail = "";
                try { using var error = JsonDocument.Parse(data.ToArray()); detail = HostChannel.Text(error.RootElement, "message"); } catch (JsonException) { }
                throw new DesktopException("api_failed", detail.Length is > 0 and < 500 ? detail : $"内核返回 HTTP {(int)response.StatusCode}");
            }
            if (data.Length == 0) return JsonSerializer.SerializeToElement(new { });
            using var parsed = JsonDocument.Parse(data.ToArray());
            return parsed.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !controllerToken.IsCancellationRequested)
        {
            throw new DesktopException("request_timeout", "请求超时，请刷新状态后重试。");
        }
    }

    public async IAsyncEnumerable<JsonElement> StreamAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken, controllerLifetime.Token);
        var (uri, secret) = Endpoint(path);
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + secret);
        var wsUri = new UriBuilder(uri) { Scheme = "ws" }.Uri;
        await socket.ConnectAsync(wsUri, linked.Token);
        var buffer = new byte[16384];
        while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), linked.Token);
                if (result.MessageType == WebSocketMessageType.Close) yield break;
                if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 32 * 1024 * 1024)
                    throw new DesktopException("stream_invalid", "内核数据流格式无效");
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            linked.Token.ThrowIfCancellationRequested();
            using var json = JsonDocument.Parse(message.ToArray());
            yield return json.RootElement.Clone();
        }
    }

    public void SetTheme(string theme)
    {
        if (theme is not ("system" or "light" or "dark")) throw new ArgumentException("Unknown theme", nameof(theme));
        Theme = theme;
        ApplyTheme();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(appearancePath)!);
            string temporary = appearancePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { theme }));
            File.Move(temporary, appearancePath, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Notify("外观已切换，但未能保存偏好。", true); }
    }

    public void ApplyTheme()
    {
        var app = Application.Current;
        app.ThemeMode = Theme switch { "dark" => ThemeMode.Dark, "light" => ThemeMode.Light, _ => ThemeMode.System };
        bool dark = Theme == "dark" || (Theme == "system" && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int setting && setting == 0);
        var palette = new Dictionary<string, string>
        {
            ["AppBackgroundBrush"] = dark ? "#202020" : "#F3F3F3",
            ["SurfaceBrush"] = dark ? "#2D2D2D" : "#FFFFFF",
            ["SubtleSurfaceBrush"] = dark ? "#272727" : "#F8F8F8",
            ["TextPrimaryBrush"] = dark ? "#FFFFFF" : "#1B1B1B",
            ["TextSecondaryBrush"] = dark ? "#C5C5C5" : "#616161",
            ["BorderBrush"] = dark ? "#454545" : "#E2E2E2",
            ["SuccessBrush"] = dark ? "#6CCB5F" : "#0F7B0F",
            ["DangerBrush"] = dark ? "#FF99A4" : "#C42B1C"
        };
        foreach (var item in palette) app.Resources[item.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Value));
        app.Resources["AccentBrush"] = dark ? SystemColors.AccentColorLight2Brush : SystemColors.AccentColorDark1Brush;
        app.Resources["AccentTextBrush"] = dark ? Brushes.Black : Brushes.White;
        if (SystemParameters.HighContrast)
        {
            app.Resources["AppBackgroundBrush"] = app.Resources["SurfaceBrush"] = app.Resources["SubtleSurfaceBrush"] = SystemColors.WindowBrush;
            app.Resources["TextPrimaryBrush"] = app.Resources["TextSecondaryBrush"] = app.Resources["SuccessBrush"] = app.Resources["DangerBrush"] = SystemColors.WindowTextBrush;
            app.Resources["BorderBrush"] = SystemColors.WindowTextBrush;
            app.Resources["AccentBrush"] = SystemColors.HighlightBrush;
            app.Resources["AccentTextBrush"] = SystemColors.HighlightTextBrush;
        }
        ThemeChanged?.Invoke();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Theme)));
    }

    private void OnSystemThemeChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatch(ApplyTheme);
    public void Notify(string message, bool error = false) => Dispatch(() => Notification?.Invoke(message, error));
    public void Navigate(string key) => Dispatch(() => NavigateRequested?.Invoke(key));
    public void RequestExit() => Dispatch(() => ExitRequested?.Invoke());
    public void ExpectExit() { exiting = true; host.ExpectExit(); }
    private static void Dispatch(Action action)
    {
        if (Application.Current.Dispatcher.CheckAccess()) action();
        else if (!Application.Current.Dispatcher.HasShutdownStarted) _ = Application.Current.Dispatcher.BeginInvoke(action);
    }

    public async ValueTask DisposeAsync()
    {
        exiting = true;
        lifetime.Cancel();
        controllerLifetime.Cancel();
        SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
        await host.DisposeAsync();
        http.Dispose();
        controllerLifetime.Dispose();
        lifetime.Dispose();
    }
}
