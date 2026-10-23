using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Mihomo.Desktop.Services;

[SupportedOSPlatform("windows")]
internal sealed class InstanceBroker : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly string userSid;
    private readonly int sessionId;
    private readonly string applicationPath;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private Task? listener;
    private int disposed;
    public bool IsFirst { get; }

    // A separate scope lets acceptance tests exercise real pipes without
    // connecting to a user's running application. Production uses the default.
    internal InstanceBroker(string? scope = null)
    {
        lifetimeToken = lifetime.Token;
        using var identity = WindowsIdentity.GetCurrent();
        userSid = identity.User?.Value ?? throw new InvalidOperationException("无法确认当前 Windows 用户");
        using var current = Process.GetCurrentProcess();
        sessionId = current.SessionId;
        applicationPath = CanonicalPath(Path.Combine(AppContext.BaseDirectory, "mihomo-optimized.exe"));
        if (!string.Equals(CanonicalPath(Environment.ProcessPath ?? ""), applicationPath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("实例通信只能由配套原生桌面程序启动");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userSid + ":" + sessionId + (scope is null ? "" : ":" + scope))))[..24];
        pipeName = "MihomoOptimized.Native." + key;
        string mutexName = @"Local\" + pipeName;
        // Holding the object, without taking thread-affine mutex ownership, is
        // sufficient. Existing clients only need read/synchronize access.
        if (MutexAcl.TryOpenExisting(mutexName, MutexRights.Synchronize, out var existing))
        {
            mutex = existing;
            IsFirst = false;
        }
        else
        {
            var security = new MutexSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new MutexAccessRule(identity.User!, MutexRights.FullControl, AccessControlType.Allow));
            mutex = MutexAcl.Create(false, mutexName, out bool first, security);
            IsFirst = first;
        }
    }

    public async Task<bool> SendAsync(string command, CancellationToken cancellationToken)
    {
        if (command is not ("activate" or "quit")) throw new ArgumentException("Unknown instance command", nameof(command));
        // CurrentUserOnly also compares elevation on Windows. An elevated
        // installer must reach this same user's ordinary desktop instance.
        // Identification prevents a server from borrowing that elevated token.
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(cancellationToken);
        using var peer = AuthenticatePeer(pipe.SafePipeHandle, server: true);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { command }).AsMemory(), cancellationToken);
        string? line = await BoundedLine.ReadAsync(reader, 4096, cancellationToken);
        using var reply = JsonDocument.Parse(line ?? "{}");
        return reply.RootElement.ValueKind == JsonValueKind.Object && reply.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
    }

    public void Listen(Func<string, Task<bool>> handle)
    {
        if (!IsFirst || listener is not null) throw new InvalidOperationException();
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(userSid));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(userSid), PipeAccessRights.FullControl, AccessControlType.Allow));
        // Create synchronously so ownership/ACL failures reach startup. Keep the
        // first server instance open between clients, preventing name takeover.
        var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);
        listener = ListenAsync(pipe, handle);
    }

    private async Task ListenAsync(NamedPipeServerStream pipe, Func<string, Task<bool>> handle)
    {
        using (pipe)
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(lifetimeToken);
                    using var peer = AuthenticatePeer(pipe.SafePipeHandle, server: false);
                    using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(60));
                    string? line = await BoundedLine.ReadAsync(reader, 4096, deadline.Token);
                    using var request = JsonDocument.Parse(line ?? "{}");
                    string command = HostChannel.Text(request.RootElement, "command");
                    bool ok = command is "activate" or "quit" && await handle(command).WaitAsync(deadline.Token);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok }).AsMemory(), deadline.Token);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException or Win32Exception or DecoderFallbackException) { }
                finally
                {
                    if (pipe.IsConnected) try { pipe.Disconnect(); } catch (IOException) { }
                }
            }
        }
    }

    private SafeProcessHandle AuthenticatePeer(SafePipeHandle pipe, bool server)
    {
        bool found = server ? GetNamedPipeServerProcessId(pipe, out uint pid) : GetNamedPipeClientProcessId(pipe, out pid);
        if (!found) throw new Win32Exception(Marshal.GetLastWin32Error());
        var process = OpenProcess(0x00101000, false, pid); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
        try
        {
            if (process.IsInvalid || WaitForSingleObject(process, 0) != 258)
                throw new UnauthorizedAccessException("无法验证实例进程");
            var image = new StringBuilder(32768);
            uint length = (uint)image.Capacity;
            if (!QueryFullProcessImageNameW(process, 0, image, ref length) || !ProcessIdToSessionId(pid, out uint peerSession))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (peerSession != sessionId || !string.Equals(CanonicalPath(image.ToString()), applicationPath, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("实例程序的路径或登录会话不匹配");
            if (!OpenProcessToken(process, 0x0008, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                if (identity.User?.Value != userSid) throw new UnauthorizedAccessException("实例程序不属于当前用户");
            // Hold this handle throughout the exchange; a PID cannot be recycled
            // between validation and execution of the requested action.
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    private static string CanonicalPath(string path)
    {
        using var file = File.OpenHandle(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var final = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(file, final, (uint)final.Capacity, 0);
        if (length == 0 || length >= final.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        return final.ToString();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        mutex.Dispose();
        if (listener is null) lifetime.Dispose();
        else _ = listener.ContinueWith(task => { _ = task.Exception; lifetime.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
}
