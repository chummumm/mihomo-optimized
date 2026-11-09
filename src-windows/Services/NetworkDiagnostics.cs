using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;

namespace Mihomo.Desktop.Services;

internal sealed record NetworkInterfaceEvidence(string DeviceId, int? Index, string Kind, string Status, bool? GatewayConfigured, string? InspectionError = null);
internal sealed record NetworkEvidence(bool Available, NetworkInterfaceEvidence[] Interfaces);

/// <summary>Read-only, coalesced observations. This object has no lifecycle control capability.</summary>
internal sealed class NetworkDiagnostics : IDisposable
{
    private readonly object gate = new();
    private readonly LifecycleDiagnostics diagnostics;
    private readonly Func<NetworkEvidence> capture;
    private readonly bool subscribe;
    private readonly Timer timer;
    private bool started;
    private bool disposed;
    private bool running;
    private int pending;
    private int pendingNotificationCount;
    private long? firstNotificationMs;
    private long? lastNotificationMs;
    private bool availableNotification;
    private bool unavailableNotification;
    private string? previous;
    private bool observationFailed;
    internal bool IsIdle { get { lock (gate) return !running && pending == 0; } }

    internal NetworkDiagnostics(LifecycleDiagnostics diagnostics, Func<NetworkEvidence>? capture = null, bool subscribe = true)
    {
        this.diagnostics = diagnostics;
        this.capture = capture ?? Capture;
        this.subscribe = subscribe;
        timer = new Timer(_ => Observe(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal void Start()
    {
        lock (gate)
        {
            if (started || disposed) return;
            started = true;
            try
            {
                if (subscribe)
                {
                    NetworkChange.NetworkAddressChanged += AddressChanged;
                    NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
                }
            }
            catch (Exception error)
            {
                diagnostics.Record("network_observer_unavailable", new { errorType = error.GetType().Name });
            }
            ScheduleLocked(1);
        }
    }

    private void AddressChanged(object? sender, EventArgs args) => Schedule(2);
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => Schedule(4, args.IsAvailable);
    internal void Schedule(int reason, bool? availability = null)
    {
        lock (gate) { if (!disposed) ScheduleLocked(reason, availability); }
    }
    private void ScheduleLocked(int reason, bool? availability = null)
    {
        bool firstPending = pending == 0;
        pending |= reason;
        if ((reason & 6) != 0)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            firstNotificationMs ??= now;
            lastNotificationMs = now;
            if (pendingNotificationCount < int.MaxValue) pendingNotificationCount++;
            availableNotification |= availability == true;
            unavailableNotification |= availability == false;
        }
        // Bound the batch from its first notification. Continuous changes must
        // not postpone evidence indefinitely while waiting for a quiet period.
        if (firstPending && !running) timer.Change(250, Timeout.Infinite);
    }

    private void Observe()
    {
        int reasons;
        int notificationCount;
        long? firstNotification;
        long? lastNotification;
        bool availableNotified;
        bool unavailableNotified;
        lock (gate)
        {
            if (disposed) return;
            if (running) { timer.Change(250, Timeout.Infinite); return; }
            running = true;
            reasons = pending;
            pending = 0;
            notificationCount = pendingNotificationCount;
            pendingNotificationCount = 0;
            firstNotification = firstNotificationMs;
            lastNotification = lastNotificationMs;
            firstNotificationMs = lastNotificationMs = null;
            availableNotified = availableNotification;
            unavailableNotified = unavailableNotification;
            availableNotification = unavailableNotification = false;
        }
        try
        {
            NetworkEvidence state = Normalize(capture());
            string key = System.Text.Json.JsonSerializer.Serialize(state, HostChannel.JsonOptions);
            lock (gate)
            {
                bool stateChanged = key != previous;
                if (!disposed && (stateChanged || observationFailed || notificationCount > 0))
                {
                    // A disconnect/reconnect can finish inside the coalescing
                    // window. Retain the notifications even if the sample matches.
                    string eventName = stateChanged || observationFailed ? "network_snapshot_changed" : "network_change_observed";
                    diagnostics.Record(eventName, new
                    {
                        notifications = reasons, notificationCount, firstNotificationMs = firstNotification,
                        lastNotificationMs = lastNotification, availableNotified, unavailableNotified,
                        stateChanged, recoveredObservation = observationFailed,
                        networkAvailable = state.Available, interfaces = state.Interfaces
                    });
                    previous = key;
                    observationFailed = false;
                }
            }
        }
        catch (Exception error)
        {
            lock (gate)
            {
                if (!disposed && (!observationFailed || notificationCount > 0))
                    diagnostics.Record("network_snapshot_unavailable", new
                    {
                        notifications = reasons, notificationCount, firstNotificationMs = firstNotification,
                        lastNotificationMs = lastNotification, availableNotified, unavailableNotified,
                        errorType = error.GetType().Name
                    });
                observationFailed = true;
            }
        }
        finally
        {
            lock (gate)
            {
                running = false;
                if (!disposed && pending != 0) timer.Change(250, Timeout.Infinite);
            }
        }
    }

    internal static NetworkEvidence Normalize(NetworkEvidence state) => new(state.Available,
        state.Interfaces.Select(item => item with
        {
            DeviceId = Guid.TryParse(item.DeviceId, out var id) ? id.ToString("D") : "",
            Kind = LifecycleDiagnostics.Code(item.Kind), Status = LifecycleDiagnostics.Code(item.Status)
        }).OrderBy(item => item.DeviceId, StringComparer.Ordinal).ThenBy(item => item.Index).ThenBy(item => item.Kind, StringComparer.Ordinal).Take(64).ToArray());

    private static NetworkEvidence Capture() => new(NetworkInterface.GetIsNetworkAvailable(),
        NetworkInterface.GetAllNetworkInterfaces().Take(64).Select(adapter =>
        {
            string kind = adapter.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "wifi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.GigabitEthernet => "ethernet",
                NetworkInterfaceType.Loopback => "loopback",
                NetworkInterfaceType.Tunnel => "tunnel",
                _ => "other"
            };
            string status = adapter.OperationalStatus.ToString().ToLowerInvariant();
            try
            {
                var properties = adapter.GetIPProperties();
                int? index = null;
                if (adapter.Supports(NetworkInterfaceComponent.IPv4)) index = properties.GetIPv4Properties()?.Index;
                else if (adapter.Supports(NetworkInterfaceComponent.IPv6)) index = properties.GetIPv6Properties()?.Index;
                // A configured gateway is not proof that the default route exists
                // or that the Internet is reachable. Ethernet can also be virtual.
                bool gateway = properties.GatewayAddresses.Any(value => !value.Address.Equals(IPAddress.Any) && !value.Address.Equals(IPAddress.IPv6Any));
                return new NetworkInterfaceEvidence(adapter.Id, index, kind, status, gateway);
            }
            catch (Exception error)
            {
                return new NetworkInterfaceEvidence(adapter.Id, null, kind, status, null, error.GetType().Name);
            }
        }).ToArray());

    public void Dispose()
    {
        bool detach;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            detach = subscribe && started;
            timer.Dispose();
        }
        // Do not hold our callback gate while unregistering native notifications.
        if (detach)
        {
            try { NetworkChange.NetworkAddressChanged -= AddressChanged; NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged; }
            catch (Exception) { }
        }
    }
}
