using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ZmkVolumeFader;

/// <summary>
/// Owns every Core Audio COM object on one MTA thread. Endpoints and sessions are
/// long-lived: device notifications trigger endpoint reconciliation, and one
/// IAudioSessionNotification registration per endpoint supplies new sessions.
///
/// The UI publishes complete desired-volume snapshots. They are coalesced here,
/// then drained through a single OS-call budget after app/category fan-out. This
/// keeps a category with many sessions from multiplying a per-fader rate limit
/// into hundreds of Core Audio notifications per second.
/// </summary>
sealed class AudioController : IDisposable
{
    internal sealed record AppSnapshot(string Key, int Pid, IReadOnlyList<string> Aliases);
    internal sealed record VolumeSnapshot(
        IReadOnlyDictionary<string, float> Endpoints,
        IReadOnlyDictionary<string, float> Apps);
    internal sealed record ExternalVolumeChange(string Key, bool IsEndpoint, float Volume);

    const string SystemAppKey = "#system";
    const int MinOsCallIntervalMs = 25;       // system-wide ceiling: 40 setters/s
    const int SessionSweepMs = 30_000;        // state query only; no re-enumeration
    const float VolumeEpsilon = 0.0049f;      // smaller than one integer-percent step

    readonly Action<IReadOnlyList<AppSnapshot>> _appsChanged;
    readonly Action<ExternalVolumeChange> _externalVolumeChanged;
    readonly Action<Exception> _faulted;
    readonly ConcurrentQueue<Action> _commands = new();
    readonly AutoResetEvent _wake = new(false);
    readonly object _lifecycleLock = new();
    readonly object _desiredLock = new();
    readonly Thread _thread;

    DesiredSnapshot? _pendingDesired;
    bool _desiredDirty;
    volatile bool _stopping;

    // Worker-thread-only state below this line.
    MMDeviceEnumerator? _enumerator;
    readonly Dictionary<string, EndpointEntry> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    readonly List<VolumeTarget> _writeOrder = new();
    Dictionary<string, float> _desiredEndpoints = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, float> _desiredApps = new(StringComparer.OrdinalIgnoreCase);
    int _writeCursor;
    long _nextOsCall;
    long _nextSessionSweep;
    long _nextRetryAt = long.MaxValue;
    bool _writeWorkPending;
    bool _appsDirty;
    readonly Guid _notificationGuid = Guid.NewGuid();

    long _endpointSetCalls, _sessionSetCalls, _externalChangeCount;
    volatile int _endpointCount, _sessionCount;
    internal long EndpointSetCalls => Interlocked.Read(ref _endpointSetCalls);
    internal long SessionSetCalls => Interlocked.Read(ref _sessionSetCalls);
    internal long ExternalChangeCount => Interlocked.Read(ref _externalChangeCount);
    internal int EndpointCount => _endpointCount;
    internal int SessionCount => _sessionCount;

    sealed record DesiredSnapshot(Dictionary<string, float> Endpoints, Dictionary<string, float> Apps);

    abstract class VolumeTarget
    {
        public required string DesiredKey;
        public bool WasDesired;
        public float LastKnown = float.NaN;
        public long RetryAfter;
        public bool HoldForExternal;
        public abstract bool IsEndpoint { get; }
        public abstract void RefreshActual();
        public abstract void Set(float scalar);
        public virtual void Dispose() { }
    }

    sealed class EndpointEntry : VolumeTarget
    {
        public required string Id;
        public required MMDevice Device;
        public AudioSessionManager? Manager;
        public AudioSessionManager.SessionCreatedDelegate? CreatedHandler;
        public AudioEndpointVolumeNotificationDelegate? VolumeHandler;
        public override bool IsEndpoint => true;

        public override void RefreshActual()
        {
            LastKnown = Device.AudioEndpointVolume.MasterVolumeLevelScalar;
        }

        public override void Set(float scalar)
        {
            Device.AudioEndpointVolume.MasterVolumeLevelScalar = scalar;
            LastKnown = scalar;
        }

        public override void Dispose()
        {
            if (Manager != null && CreatedHandler != null)
                try { Manager.OnSessionCreated -= CreatedHandler; } catch { }
            if (VolumeHandler != null)
                try { Device.AudioEndpointVolume.OnVolumeNotification -= VolumeHandler; } catch { }
            try { Device.Dispose(); } catch { }
        }
    }

    sealed class SessionEntry : VolumeTarget
    {
        public required string Id;
        public required string EndpointId;
        public required int Pid;
        public required IReadOnlyList<string> Aliases;
        public required AudioSessionControl Control;
        public SessionVolumeEvents? Events;
        public float ExpectedVolume = float.NaN;
        public long ExpectedUntil;
        public override bool IsEndpoint => false;

        public override void RefreshActual()
        {
            LastKnown = Control.SimpleAudioVolume.Volume;
        }

        public override void Set(float scalar)
        {
            Volatile.Write(ref ExpectedVolume, scalar);
            Interlocked.Exchange(ref ExpectedUntil, Environment.TickCount64 + 1_000);
            Control.SimpleAudioVolume.Volume = scalar;
            LastKnown = scalar;
        }

        public override void Dispose()
        {
            if (Events != null)
                try { Control.UnRegisterEventClient(Events); } catch { }
            try { Control.Dispose(); } catch { }
        }
    }

    sealed class SessionVolumeEvents(Action<float, bool> changed) : IAudioSessionEventsHandler
    {
        public void OnVolumeChanged(float volume, bool isMuted) => changed(volume, isMuted);
        public void OnDisplayNameChanged(string displayName) { }
        public void OnIconPathChanged(string iconPath) { }
        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }
        public void OnGroupingParamChanged(ref Guid groupingId) { }
        public void OnStateChanged(AudioSessionState state) { }
        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) { }
    }

    public AudioController(Action<IReadOnlyList<AppSnapshot>> appsChanged,
        Action<ExternalVolumeChange>? externalVolumeChanged = null, Action<Exception>? faulted = null)
    {
        _appsChanged = appsChanged;
        _externalVolumeChanged = externalVolumeChanged ?? (_ => { });
        _faulted = faulted ?? (_ => { });
        _thread = new Thread(Worker)
        {
            IsBackground = true,
            Name = "core-audio"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Re-scan endpoints after a real device notification or user refresh.</summary>
    public void RefreshEndpoints() => TryPost(ReconcileEndpoints);

    /// <summary>
    /// Replace, rather than append to, the desired state. Fast fader updates only
    /// overwrite this one pending snapshot; they can never build an unbounded work
    /// queue behind the audio worker.
    /// </summary>
    public void SetDesiredVolumes(Dictionary<string, float> endpoints, Dictionary<string, float> apps)
    {
        lock (_lifecycleLock)
        {
            if (_stopping) return;
            lock (_desiredLock)
            {
                _pendingDesired = new DesiredSnapshot(endpoints, apps);
                _desiredDirty = true;
            }
            _wake.Set();
        }
    }

    /// <summary>
    /// Read current target levels on the COM owner thread. Used only when arming
    /// physical-fader pickup; it is event driven and never becomes a poll.
    /// </summary>
    public bool RequestCurrentVolumes(IEnumerable<string> endpointIds, IEnumerable<string> appKeys,
        Action<VolumeSnapshot> completed)
    {
        var endpointSet = endpointIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var appSet = appKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return TryPost(() =>
        {
            var endpoints = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var apps = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            foreach (string id in endpointSet)
            {
                if (!_endpoints.TryGetValue(id, out var endpoint)) continue;
                try { endpoint.RefreshActual(); endpoints[id] = endpoint.LastKnown; }
                catch (Exception ex) { Program.LogRateLimited($"audio-read:{id}", ex, "Reading an audio endpoint"); }
            }

            foreach (string key in appSet)
            {
                float total = 0;
                int count = 0;
                foreach (var session in _sessions.Values.Where(s =>
                             s.DesiredKey.Equals(key, StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        session.RefreshActual();
                        if (!float.IsNaN(session.LastKnown)) { total += session.LastKnown; count++; }
                    }
                    catch (Exception ex)
                    {
                        Program.LogRateLimited($"audio-read:{key}", ex, "Reading an app audio session");
                    }
                }
                if (count > 0) apps[key] = total / count;
            }

            try { completed(new VolumeSnapshot(endpoints, apps)); } catch { }
        });
    }

    /// <summary>
    /// Resolve the temporary write hold installed before notifying the UI about
    /// an external mixer change. Physical pickup leaves it held until that target
    /// disappears from the next desired snapshot; virtual control resumes writes.
    /// </summary>
    public void ResolveExternalChange(ExternalVolumeChange change, bool continueDriving) => TryPost(() =>
    {
        foreach (var target in _writeOrder.Where(t => t.IsEndpoint == change.IsEndpoint
                     && t.DesiredKey.Equals(change.Key, StringComparison.OrdinalIgnoreCase)))
            if (continueDriving) target.HoldForExternal = false;
        if (continueDriving)
        {
            _writeWorkPending = true;
            _nextRetryAt = long.MinValue;
        }
    });

    bool TryPost(Action action)
    {
        lock (_lifecycleLock)
        {
            if (_stopping) return false;
            _commands.Enqueue(action);
            _wake.Set();
            return true;
        }
    }

    void Worker()
    {
        int failures = 0;
        while (!_stopping)
        {
            try
            {
                RunWorkerCore();
                failures = 0;
            }
            catch (Exception ex)
            {
                try { _faulted(ex); } catch { }
            }
            finally { CleanupWorkerState(); }

            if (_stopping) break;
            int delay = Math.Min(30_000, 1_000 << Math.Min(failures++, 4));
            _wake.WaitOne(delay);
        }
    }

    void RunWorkerCore()
    {
        _enumerator = new MMDeviceEnumerator();
        ReconcileEndpoints();
        PublishAppsIfDirty();
        _nextSessionSweep = Environment.TickCount64 + SessionSweepMs;

        while (!_stopping)
        {
            DrainCommands();
            TakeDesiredSnapshot();

            long now = Environment.TickCount64;
            if (now >= _nextSessionSweep)
            {
                SweepExpiredSessions();
                _nextSessionSweep = now + SessionSweepMs;
            }
            PublishAppsIfDirty();

            bool wrote = false;
            if (_writeWorkPending && now >= _nextOsCall && now >= _nextRetryAt)
            {
                wrote = TryWriteOne(now, out long earliestRetry);
                if (wrote)
                {
                    _nextOsCall = now + MinOsCallIntervalMs;
                    _nextRetryAt = long.MinValue;
                }
                else
                {
                    _writeWorkPending = earliestRetry != long.MaxValue;
                    _nextRetryAt = earliestRetry;
                }
            }

            long deadline = _nextSessionSweep;
            if (_writeWorkPending)
            {
                if (_nextRetryAt > now) deadline = Math.Min(deadline, _nextRetryAt);
                else if (_nextOsCall > now) deadline = Math.Min(deadline, _nextOsCall);
                else deadline = now + 1;
            }
            int wait = (int)Math.Clamp(deadline - now, 1, SessionSweepMs);
            _wake.WaitOne(wait);
        }
    }

    void CleanupWorkerState()
    {
        // Dispose any session wrappers accepted immediately before shutdown or a
        // Core Audio restart. The desired snapshot survives and is replayed.
        DrainCommands();
        foreach (var s in _sessions.Values) s.Dispose();
        _sessions.Clear();
        foreach (var e in _endpoints.Values) e.Dispose();
        _endpoints.Clear();
        _endpointCount = 0;
        _sessionCount = 0;
        _writeOrder.Clear();
        try { _enumerator?.Dispose(); } catch { }
        _enumerator = null;
        _writeCursor = 0;
        _nextOsCall = 0;
        _nextRetryAt = long.MinValue;
        _writeWorkPending = true;
        _appsDirty = true;
    }

    void DrainCommands()
    {
        while (_commands.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Program.LogRateLimited("audio-command", ex, "Core Audio command"); }
        }
    }

    void TakeDesiredSnapshot()
    {
        DesiredSnapshot? next = null;
        lock (_desiredLock)
        {
            if (_desiredDirty)
            {
                next = _pendingDesired;
                _pendingDesired = null;
                _desiredDirty = false;
            }
        }
        if (next == null) return;

        _desiredEndpoints = next.Endpoints;
        _desiredApps = next.Apps;
        _writeWorkPending = true;
        _nextRetryAt = long.MinValue;

        // A target leaving the desired set must re-read its actual volume if it is
        // assigned again later; another mixer may have changed it in between.
        foreach (var t in _writeOrder)
        {
            bool wanted = t.IsEndpoint
                ? _desiredEndpoints.ContainsKey(t.DesiredKey)
                : _desiredApps.ContainsKey(t.DesiredKey);
            if (!wanted)
            {
                t.WasDesired = false;
                t.HoldForExternal = false;
            }
        }
    }

    bool TryWriteOne(long now, out long earliestRetry)
    {
        earliestRetry = long.MaxValue;
        int count = _writeOrder.Count;
        for (int checkedCount = 0; checkedCount < count; checkedCount++)
        {
            if (_writeCursor >= _writeOrder.Count) _writeCursor = 0;
            if (_writeOrder.Count == 0) return false;
            var target = _writeOrder[_writeCursor++];

            var desiredMap = target.IsEndpoint ? _desiredEndpoints : _desiredApps;
            if (!desiredMap.TryGetValue(target.DesiredKey, out float desired))
            {
                target.WasDesired = false;
                continue;
            }
            if (now < target.RetryAfter)
            {
                earliestRetry = Math.Min(earliestRetry, target.RetryAfter);
                continue;
            }
            if (target.HoldForExternal) continue;

            try
            {
                if (!target.WasDesired)
                {
                    target.WasDesired = true;
                    target.RefreshActual();       // once per assignment, not per tick
                }
                if (!float.IsNaN(target.LastKnown) && Math.Abs(target.LastKnown - desired) < VolumeEpsilon)
                    continue;

                target.Set(desired);
                if (target.IsEndpoint) Interlocked.Increment(ref _endpointSetCalls);
                else Interlocked.Increment(ref _sessionSetCalls);
                return true;
            }
            catch (Exception ex)
            {
                // A disappearing device/session should not create a tight retry
                // loop. Endpoint reconciliation or the slow session sweep cleans it.
                target.RetryAfter = now + 5_000;
                earliestRetry = Math.Min(earliestRetry, target.RetryAfter);
                Program.LogRateLimited($"audio-target:{target.DesiredKey}", ex, "Core Audio volume target");
            }
        }
        return false;
    }

    void ReconcileEndpoints()
    {
        if (_enumerator == null) return;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discovered = new List<MMDevice>();
        try
        {
            discovered.AddRange(_enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active));
            foreach (var device in discovered)
            {
                string id;
                try { id = device.ID; }
                catch { try { device.Dispose(); } catch { } continue; }
                present.Add(id);
                if (_endpoints.ContainsKey(id))
                {
                    try { device.Dispose(); } catch { }
                    continue;
                }
                AddEndpoint(id, device);
            }
        }
        catch (Exception ex)
        {
            foreach (var d in discovered)
                if (!_endpoints.Values.Any(e => ReferenceEquals(e.Device, d)))
                    try { d.Dispose(); } catch { }
            Program.LogRateLimited("audio-endpoint-reconcile", ex, "Enumerating audio endpoints");
            return;
        }

        foreach (var id in _endpoints.Keys.Where(id => !present.Contains(id)).ToArray())
            RemoveEndpoint(id);
    }

    void AddEndpoint(string id, MMDevice device)
    {
        var endpoint = new EndpointEntry { Id = id, DesiredKey = id, Device = device };
        _endpoints[id] = endpoint;
        _endpointCount = _endpoints.Count;
        _writeOrder.Add(endpoint);
        try
        {
            device.AudioEndpointVolume.NotificationGuid = _notificationGuid;
            AudioEndpointVolumeNotificationDelegate changed = data =>
            {
                if (data.EventContext == _notificationGuid) return;
                float volume = data.MasterVolume;
                Guid context = data.EventContext;
                TryPost(() => OnEndpointVolumeChanged(endpoint, volume, context));
            };
            endpoint.VolumeHandler = changed;
            device.AudioEndpointVolume.OnVolumeNotification += changed;
        }
        catch (Exception ex)
        {
            Program.LogRateLimited($"audio-endpoint-events:{id}", ex, "Registering endpoint volume notifications");
        }
        if (_desiredEndpoints.ContainsKey(id))
        {
            _writeWorkPending = true;
            _nextRetryAt = long.MinValue;
        }

        try
        {
            var manager = device.AudioSessionManager; // one stable registration
            endpoint.Manager = manager;
            AudioSessionManager.SessionCreatedDelegate created = (_, raw) =>
            {
                AudioSessionControl? control = null;
                try { control = new AudioSessionControl(raw); } catch { }
                if (control != null && !TryPost(() =>
                    {
                        if (_endpoints.TryGetValue(id, out var current) && ReferenceEquals(current, endpoint))
                            AddSession(id, control);
                        else
                            try { control.Dispose(); } catch { }
                    }))
                    try { control.Dispose(); } catch { }
            };
            endpoint.CreatedHandler = created;
            manager.OnSessionCreated += created;

            var sessions = manager.Sessions;
            int count = sessions.Count;
            for (int i = 0; i < count; i++)
            {
                AudioSessionControl? control = null;
                try { control = sessions[i]; } catch { }
                if (control != null) AddSession(id, control);
            }
        }
        catch (Exception ex)
        {
            // Endpoint volume control can still work when session management is
            // unavailable (service restart, exclusive-mode transition, etc.).
            Program.LogRateLimited($"audio-session-manager:{id}", ex, "Opening an audio session manager");
        }
    }

    bool AddSession(string endpointId, AudioSessionControl control)
    {
        if (!_endpoints.ContainsKey(endpointId))
        {
            try { control.Dispose(); } catch { }
            return false;
        }

        try
        {
            if (control.State == AudioSessionState.AudioSessionStateExpired)
            {
                control.Dispose();
                return false;
            }

            int pid = 0;
            string appKey;
            IReadOnlyList<string> aliases;
            if (control.IsSystemSoundsSession) { appKey = SystemAppKey; aliases = new[] { SystemAppKey }; }
            else
            {
                pid = checked((int)control.GetProcessID);
                var identity = AppIdentityForPid(pid);
                if (identity == null) { control.Dispose(); return false; }
                appKey = identity.Value.Key;
                aliases = identity.Value.Aliases;
            }

            string instanceId;
            try { instanceId = control.GetSessionInstanceIdentifier; }
            catch { instanceId = $"{endpointId}:{pid}:{Guid.NewGuid():N}"; }
            string id = $"{endpointId}\n{instanceId}";
            if (_sessions.ContainsKey(id))
            {
                control.Dispose();
                return false;
            }

            var session = new SessionEntry
            {
                Id = id,
                EndpointId = endpointId,
                DesiredKey = appKey,
                Aliases = aliases,
                Pid = pid,
                Control = control,
            };
            var events = new SessionVolumeEvents((volume, muted) =>
            {
                long now = Environment.TickCount64;
                if (AudioNotificationLogic.MatchesExpectedWrite(Volatile.Read(ref session.ExpectedVolume),
                        Interlocked.Read(ref session.ExpectedUntil), now, volume, VolumeEpsilon))
                {
                    Interlocked.Exchange(ref session.ExpectedUntil, 0);
                    return;
                }
                TryPost(() => OnSessionVolumeChanged(session, volume, muted));
            });
            try
            {
                control.RegisterEventClient(events);
                session.Events = events;
            }
            catch (Exception ex)
            {
                Program.LogRateLimited($"audio-session-events:{id}", ex, "Registering session volume notifications");
            }
            _sessions[id] = session;
            _sessionCount = _sessions.Count;
            _writeOrder.Add(session);
            _appsDirty = true;
            if (_desiredApps.ContainsKey(appKey))
            {
                _writeWorkPending = true;
                _nextRetryAt = long.MinValue;
            }
            return true;
        }
        catch
        {
            try { control.Dispose(); } catch { }
            return false;
        }
    }

    bool RemoveEndpoint(string id)
    {
        if (!_endpoints.Remove(id, out var endpoint)) return false;
        _endpointCount = _endpoints.Count;
        _writeOrder.Remove(endpoint);
        foreach (var sessionId in _sessions.Values
                     .Where(s => s.EndpointId.Equals(id, StringComparison.OrdinalIgnoreCase))
                     .Select(s => s.Id).ToArray())
            RemoveSession(sessionId);
        endpoint.Dispose();
        _appsDirty = true;
        return true;
    }

    void RemoveSession(string id)
    {
        if (!_sessions.Remove(id, out var session)) return;
        _sessionCount = _sessions.Count;
        _writeOrder.Remove(session);
        session.Dispose();
        _appsDirty = true;
        if (_writeCursor > _writeOrder.Count) _writeCursor = 0;
    }

    void OnEndpointVolumeChanged(EndpointEntry endpoint, float volume, Guid context)
    {
        if (!_endpoints.TryGetValue(endpoint.Id, out var current) || !ReferenceEquals(current, endpoint)) return;
        float previous = endpoint.LastKnown;
        endpoint.LastKnown = volume;
        if (!endpoint.WasDesired || context == _notificationGuid
            || !AudioNotificationLogic.HasMeaningfulChange(previous, volume, VolumeEpsilon)) return;
        endpoint.HoldForExternal = true;
        Interlocked.Increment(ref _externalChangeCount);
        try { _externalVolumeChanged(new ExternalVolumeChange(endpoint.DesiredKey, true, volume)); } catch { }
    }

    void OnSessionVolumeChanged(SessionEntry session, float volume, bool isMuted)
    {
        if (!_sessions.TryGetValue(session.Id, out var current) || !ReferenceEquals(current, session)) return;
        float previous = session.LastKnown;
        session.LastKnown = volume;
        long now = Environment.TickCount64;
        if (AudioNotificationLogic.MatchesExpectedWrite(session.ExpectedVolume, session.ExpectedUntil,
                now, volume, VolumeEpsilon))
        {
            session.ExpectedUntil = 0;
            return;
        }
        if (!session.WasDesired || !AudioNotificationLogic.HasMeaningfulChange(previous, volume, VolumeEpsilon)) return;
        session.HoldForExternal = true;
        Interlocked.Increment(ref _externalChangeCount);
        try { _externalVolumeChanged(new ExternalVolumeChange(session.DesiredKey, false, volume)); } catch { }
    }

    void SweepExpiredSessions()
    {
        foreach (var session in _sessions.Values.ToArray())
        {
            try
            {
                if (session.Control.State != AudioSessionState.AudioSessionStateExpired) continue;
            }
            catch { }
            RemoveSession(session.Id);
        }
    }

    void PublishAppsIfDirty()
    {
        if (!_appsDirty) return;
        _appsDirty = false;
        PublishApps();
    }

    void PublishApps()
    {
        var apps = _sessions.Values
            .GroupBy(s => s.DesiredKey, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var aliases = g.SelectMany(x => x.Aliases).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return new AppSnapshot(g.Key, g.Select(x => x.Pid).FirstOrDefault(pid => pid != 0), aliases);
            })
            .OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        try { _appsChanged(apps); } catch { }
    }

    static (string Key, IReadOnlyList<string> Aliases)? AppIdentityForPid(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            string legacy = p.ProcessName.ToLowerInvariant();
            try
            {
                string? path = p.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    var identity = AppIdentityLogic.Create(legacy, path, info.CompanyName, info.ProductName);
                    return (identity.Key, identity.Aliases);
                }
            }
            catch { }
            return (legacy, new[] { legacy });
        }
        catch { return null; }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_stopping) return;
            _stopping = true;
            _wake.Set();
        }
        if (_thread.Join(3_000)) _wake.Dispose();
    }
}
