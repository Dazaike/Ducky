using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

using Ducky.Core.Config;

namespace Ducky.Core.Audio;

public sealed class AudioSessionInfo
{
    public required AudioSessionControl Session { get; init; }
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public bool IsSystemSounds { get; init; }
}

public static class SessionEnumerator
{
    private static readonly Lazy<AotDeviceEnumerator?> DeviceEnumerator = new(CreateDeviceEnumerator);

    private static AotDeviceEnumerator? CreateDeviceEnumerator()
    {
        try
        {
            return new AotDeviceEnumerator();
        }
        catch
        {
            return null;
        }
    }
    private static readonly object CacheLock = new();
    private static readonly TimeSpan DeviceCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TargetDeviceCacheTtl = TimeSpan.FromSeconds(15);

    private static string? _cachedBackgroundPattern;
    private static MMDevice? _cachedBackgroundDevice;
    private static DateTime _backgroundDeviceCachedUtc;

    private static readonly Dictionary<string, TargetDeviceCacheEntry> TargetDeviceCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed class TargetDeviceCacheEntry
    {
        public List<string> DeviceIds { get; init; } = new();
        public DateTime CachedUtc { get; init; }
    }

    public static MMDevice GetDefaultRenderDevice() =>
        GetDefaultRenderDeviceSafe()
        ?? throw new InvalidOperationException("No default render audio device is available.");

    private static MMDevice? GetDefaultRenderDeviceSafe()
    {
        var enumerator = DeviceEnumerator.Value;
        if (enumerator is null)
        {
            return null;
        }

        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            return null;
        }
    }

    public static string NormalizeProcessName(string processName) =>
        processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

    public static void InvalidateDeviceCache()
    {
        lock (CacheLock)
        {
            _cachedBackgroundPattern = null;
            _cachedBackgroundDevice = null;
            TargetDeviceCache.Clear();
        }
    }

    public static void InvalidateTargetDeviceCache(string? processName = null)
    {
        lock (CacheLock)
        {
            if (processName is null)
            {
                TargetDeviceCache.Clear();
                return;
            }

            var name = NormalizeProcessName(processName);
            TargetDeviceCache.Remove(name);
        }
    }

    public static IReadOnlyList<int> FindAllProcessIds(string processName)
    {
        var name = NormalizeProcessName(processName);
        var ids = new List<int>();

        foreach (var proc in Process.GetProcessesByName(name))
        {
            try
            {
                ids.Add(proc.Id);
            }
            finally
            {
                proc.Dispose();
            }
        }

        return ids;
    }

    /// <summary>
    /// Returns the first PID with an active audio session, else the first running process.
    /// </summary>
    public static int? FindProcessId(string processName)
    {
        var sessionPids = FindSessionProcessIds(processName);
        if (sessionPids.Count > 0)
        {
            return sessionPids[0];
        }

        var running = FindAllProcessIds(processName);
        return running.Count > 0 ? running[0] : null;
    }

    public static IReadOnlyList<int> FindSessionProcessIds(string processName)
    {
        var name = NormalizeProcessName(processName);
        return GetRenderSessionsForDevices(GetDevicesForTargetMonitoring(processName))
            .Where(s => s.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(s => (int)s.ProcessId)
            .Distinct()
            .ToList();
    }

    public static bool IsTargetAvailable(string processName)
    {
        if (FindSessionProcessIds(processName).Count > 0)
        {
            return true;
        }

        return FindAllProcessIds(processName).Count > 0;
    }

    public static IReadOnlyList<MMDevice> GetActiveRenderDevices(bool forceRefresh = false)
    {
        _ = forceRefresh;
        return EnumerateActiveRenderDevices();
    }

    private static List<MMDevice> EnumerateActiveRenderDevices()
    {
        var enumerator = DeviceEnumerator.Value;
        if (enumerator is null)
        {
            return new List<MMDevice>();
        }

        try
        {
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        }
        catch
        {
            return new List<MMDevice>();
        }
    }

    public static MMDevice? FindRenderDeviceByPattern(string deviceNamePattern)
    {
        if (string.IsNullOrWhiteSpace(deviceNamePattern))
        {
            return GetDefaultRenderDevice();
        }

        string? cachedDeviceId = null;
        lock (CacheLock)
        {
            if (_cachedBackgroundDevice is not null &&
                _cachedBackgroundPattern is not null &&
                _cachedBackgroundPattern.Equals(deviceNamePattern, StringComparison.OrdinalIgnoreCase) &&
                DateTime.UtcNow - _backgroundDeviceCachedUtc < DeviceCacheTtl)
            {
                cachedDeviceId = _cachedBackgroundDevice.ID;
            }
        }

        if (cachedDeviceId is not null)
        {
            foreach (var device in GetActiveRenderDevices())
            {
                if (device.ID.Equals(cachedDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return device;
                }
            }
        }

        foreach (var device in GetActiveRenderDevices())
        {
            string friendlyName;
            try
            {
                friendlyName = GetDeviceFriendlyNameSafe(device);
            }
            catch
            {
                continue;
            }

            if (!friendlyName.Contains(deviceNamePattern, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lock (CacheLock)
            {
                _cachedBackgroundPattern = deviceNamePattern;
                _cachedBackgroundDevice = device;
                _backgroundDeviceCachedUtc = DateTime.UtcNow;
            }

            return device;
        }

        lock (CacheLock)
        {
            _cachedBackgroundPattern = deviceNamePattern;
            _cachedBackgroundDevice = null;
        }

        return null;
    }

    public static IReadOnlyList<MMDevice> GetDevicesForTargetMonitoring(string processName)
    {
        var name = NormalizeProcessName(processName);
        List<string> deviceIds;

        lock (CacheLock)
        {
            if (TargetDeviceCache.TryGetValue(name, out var cached) &&
                DateTime.UtcNow - cached.CachedUtc < TargetDeviceCacheTtl &&
                cached.DeviceIds.Count > 0)
            {
                deviceIds = cached.DeviceIds;
            }
            else
            {
                deviceIds = DiscoverDeviceIdsForTarget(name);
                TargetDeviceCache[name] = new TargetDeviceCacheEntry
                {
                    DeviceIds = deviceIds,
                    CachedUtc = DateTime.UtcNow
                };
            }
        }

        return ResolveDevicesById(deviceIds);
    }

    private static List<MMDevice> ResolveDevicesById(IEnumerable<string> deviceIds)
    {
        var devices = new List<MMDevice>();
        var activeDevices = EnumerateActiveRenderDevices();
        var byId = activeDevices.ToDictionary(d => d.ID, StringComparer.OrdinalIgnoreCase);

        foreach (var deviceId in deviceIds)
        {
            if (byId.TryGetValue(deviceId, out var device))
            {
                devices.Add(device);
            }
        }

        if (devices.Count == 0)
        {
            var fallback = GetDefaultRenderDeviceSafe();
            if (fallback is not null)
            {
                devices.Add(fallback);
            }
        }

        return devices;
    }

    private static List<string> DiscoverDeviceIdsForTarget(string processName)
    {
        var deviceIds = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(MMDevice device)
        {
            if (seenIds.Add(device.ID))
            {
                deviceIds.Add(device.ID);
            }
        }

        var defaultDevice = GetDefaultRenderDeviceSafe();
        if (defaultDevice is not null)
        {
            TryAdd(defaultDevice);
        }

        foreach (var device in GetActiveRenderDevices())
        {
            try
            {
                if (DeviceHasTargetSession(device, processName))
                {
                    TryAdd(device);
                }
            }
            catch
            {
                // Skip devices that fail COM/session enumeration under AOT.
            }
        }

        return deviceIds;
    }

    private static bool DeviceHasTargetSession(MMDevice device, string processName)
    {
        foreach (var info in GetRenderSessionsForDevice(device))
        {
            if (info.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<AudioSessionInfo> GetRenderSessions(MMDevice? device = null)
    {
        if (device is not null)
        {
            return GetRenderSessionsForDevice(device);
        }

        return GetRenderSessionsForDevices(GetActiveRenderDevices());
    }

    private static IReadOnlyList<AudioSessionInfo> GetRenderSessionsForDevices(IEnumerable<MMDevice> devices)
    {
        var result = new List<AudioSessionInfo>();
        foreach (var renderDevice in devices)
        {
            try
            {
                result.AddRange(GetRenderSessionsForDevice(renderDevice));
            }
            catch
            {
                // Skip devices that fail COM/session enumeration under AOT.
            }
        }

        return result;
    }

    private static IReadOnlyList<AudioSessionInfo> GetRenderSessionsForDevice(MMDevice device)
    {
        try
        {
            var manager = device.AudioSessionManager;
            var result = new List<AudioSessionInfo>();
            var deviceName = GetDeviceFriendlyNameSafe(device);
            var deviceId = GetDeviceIdSafe(device);

            int sessionCount;
            try
            {
                sessionCount = manager.Sessions.Count;
            }
            catch
            {
                return result;
            }

            for (var i = 0; i < sessionCount; i++)
            {
                AudioSessionControl session;
                try
                {
                    session = manager.Sessions[i];
                }
                catch
                {
                    continue;
                }

                try
                {
                    var pid = GetSessionProcessId(session);
                    var processName = pid > 0 ? GetProcessName((int)pid) : string.Empty;
                    var isSystem = IsSystemSoundsSession(session, pid);

                    result.Add(new AudioSessionInfo
                    {
                        Session = session,
                        ProcessId = pid,
                        ProcessName = processName,
                        SessionId = GetSessionIdentifierSafe(session),
                        DisplayName = session.DisplayName ?? string.Empty,
                        DeviceName = deviceName,
                        DeviceId = deviceId,
                        IsSystemSounds = isSystem
                    });
                }
                catch
                {
                    // Session may have expired between enumeration and query.
                }
            }

            return result;
        }
        catch
        {
            return Array.Empty<AudioSessionInfo>();
        }
    }

    private static string GetDeviceFriendlyNameSafe(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch
        {
            return GetDeviceIdSafe(device);
        }
    }

    private static string GetDeviceIdSafe(MMDevice device)
    {
        try
        {
            return device.ID;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static IReadOnlyList<AudioSessionInfo> GetSessionsOnDevice(string deviceNamePattern)
    {
        var device = FindRenderDeviceByPattern(deviceNamePattern);
        if (device is null)
        {
            return Array.Empty<AudioSessionInfo>();
        }

        return GetRenderSessionsForDevice(device);
    }

    public static bool HasActiveBackgroundAudio(
        DuckingSettings settings,
        string targetProcess,
        uint selfPid)
    {
        if (!settings.RequireBackgroundAudio)
        {
            return true;
        }

        var targetName = NormalizeProcessName(targetProcess);
        foreach (var info in GetSessionsOnDevice(settings.BackgroundAudioDevicePattern))
        {
            if (IsSkippedForDucking(info, targetName, selfPid, settings.ExcludedProcesses))
            {
                continue;
            }

            if (IsSessionActivelyPlaying(info, settings.PeakThreshold))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsSessionActivelyPlaying(AudioSessionInfo info, float peakThreshold)
    {
        if (GetPeakLevel(info.Session) > peakThreshold)
        {
            return true;
        }

        try
        {
            return info.Session.State == AudioSessionState.AudioSessionStateActive;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSkippedForDucking(
        AudioSessionInfo info,
        string targetProcessName,
        uint selfPid,
        IEnumerable<string> excludedProcesses)
    {
        if (info.IsSystemSounds || info.ProcessId == 0 || info.ProcessId == selfPid)
        {
            return true;
        }

        if (!string.IsNullOrEmpty(info.ProcessName) &&
            info.ProcessName.Equals(targetProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var excluded = new HashSet<string>(
            excludedProcesses.Select(NormalizeProcessName),
            StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(info.ProcessName) && excluded.Contains(info.ProcessName))
        {
            return true;
        }

        if (info.ProcessName.Equals("audiodg", StringComparison.OrdinalIgnoreCase) ||
            info.ProcessName.Equals("SteelSeriesSonar", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static float GetPeakForProcess(int processId, MMDevice? device = null)
    {
        var maxPeak = 0f;
        var devices = device is not null
            ? new[] { device }
            : GetActiveRenderDevices();

        foreach (var info in GetRenderSessionsForDevices(devices))
        {
            if (info.ProcessId != (uint)processId)
            {
                continue;
            }

            maxPeak = Math.Max(maxPeak, GetPeakLevel(info.Session));
        }

        return maxPeak;
    }

    public static float GetPeakForTargetProcess(string processName, MMDevice? device = null)
    {
        var name = NormalizeProcessName(processName);
        if (device is not null)
        {
            return GetPeakForTargetProcessOnDevices(name, new[] { device });
        }

        var peak = GetPeakForTargetProcessOnDevices(name, GetDevicesForTargetMonitoring(processName));
        if (peak > 0f)
        {
            return peak;
        }

        InvalidateTargetDeviceCache(processName);
        peak = GetPeakForTargetProcessOnDevices(name, GetDevicesForTargetMonitoring(processName));
        if (peak > 0f)
        {
            return peak;
        }

        return GetPeakForTargetProcessOnDevices(name, GetActiveRenderDevices());
    }

    private static float GetPeakForTargetProcessOnDevices(string processName, IEnumerable<MMDevice> devices)
    {
        var maxPeak = 0f;

        foreach (var info in GetRenderSessionsForDevices(devices))
        {
            if (!info.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            maxPeak = Math.Max(maxPeak, GetPeakLevel(info.Session));
        }

        return maxPeak;
    }

    public static float GetPeakLevel(AudioSessionControl session)
    {
        try
        {
            return session.AudioMeterInformation.MasterPeakValue;
        }
        catch
        {
            return 0f;
        }
    }

    private static uint GetSessionProcessId(AudioSessionControl session)
    {
        try
        {
            return session.GetProcessID;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsSystemSoundsSession(AudioSessionControl session, uint pid)
    {
        if (pid == 0)
        {
            return true;
        }

        try
        {
            if (session.IsSystemSoundsSession)
            {
                return true;
            }
        }
        catch
        {
            // Fall back to name-based detection below.
        }

        var id = GetSessionIdentifierSafe(session);
        if (id.Contains("SystemSounds", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = session.DisplayName ?? string.Empty;
        return name.Contains("System Sounds", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSessionIdentifierSafe(AudioSessionControl session)
    {
        try
        {
            return session.GetSessionIdentifier ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetProcessName(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }
}
