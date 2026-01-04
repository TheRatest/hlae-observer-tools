using System;
using System.Globalization;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Services.Vmix;

/// <summary>
/// Listens to killfeed events and GSI focus to trigger vMix replay marks.
/// </summary>
public sealed class VmixReplayService : IDisposable
{
    private readonly HlaeWebSocketClient _webSocketClient;
    private readonly GsiServer _gsiServer;
    private readonly HttpClient _httpClient;
    private readonly VmixReplaySettings _settings;
    private readonly object _sync = new();
    private readonly Dictionary<(int Round, string Player), int> _roundKillCounts = new();
    private readonly List<EventKill> _eventKills = new();

    private int _focusedObserverSlot = -1;
    private int _currentRoundNumber;
    private int _labelRoundNumber;
    private string _currentRoundPhase = string.Empty;
    private int? _eventRoundNumber;
    private DateTimeOffset? _firstKillTime;
    private DateTimeOffset? _lastKillTime;
    private bool _markCreated;
    private CancellationTokenSource? _markCts;
    private CancellationTokenSource? _extendCts;
    private bool _disposed;

    private const string FunctionMark = "ReplayMarkInOutLive";
    private const string FunctionSelectLast = "ReplaySelectLastEvent";
    private const string FunctionJumpToNow = "ReplayJumpToNow";
    private const string FunctionUpdateOut = "ReplayUpdateSelectedOutPoint";
    private const string FunctionSetText = "ReplaySetLastEventText";

    private readonly record struct VmixConfig(bool Enabled, string Host, int Port, double PreSeconds, double PostSeconds, double ExtendWindowSeconds);
    private readonly record struct EventKill(string PlayerName, int RoundKillNumber);

    public VmixReplayService(HlaeWebSocketClient webSocketClient, GsiServer gsiServer, VmixReplaySettings settings)
    {
        _webSocketClient = webSocketClient;
        _gsiServer = gsiServer;
        _httpClient = new HttpClient();
        _settings = settings;

        _webSocketClient.MessageReceived += OnWebSocketMessage;
        _gsiServer.GameStateUpdated += OnGameStateUpdated;
    }

    private void OnGameStateUpdated(object? sender, GsiGameState state)
    {
        int focusedSlot = -1;
        int roundNumber = state.RoundNumber;
        var phase = (state.RoundPhase ?? string.Empty).ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(state.FocusedPlayerSteamId))
        {
            foreach (var player in state.Players)
            {
                if (string.Equals(player.SteamId, state.FocusedPlayerSteamId, StringComparison.Ordinal))
                {
                    focusedSlot = player.Slot >= 0 ? player.Slot + 1 : player.Slot;
                    break;
                }
            }
        }

        lock (_sync)
        {
            _focusedObserverSlot = focusedSlot;
            _currentRoundNumber = roundNumber;
            _currentRoundPhase = phase;

            // Only advance the labeling round during active phases; keep previous when round phase is OVER
            if (!string.Equals(phase, "OVER", StringComparison.Ordinal))
            {
                if (roundNumber > 0)
                {
                    _labelRoundNumber = roundNumber;
                }
            }
            else if (_labelRoundNumber == 0 && roundNumber > 0)
            {
                // Fallback: if we start in OVER with no prior round, assume previous
                _labelRoundNumber = Math.Max(1, roundNumber - 1);
            }

            if (roundNumber > 0 && roundNumber != _labelRoundNumber)
            {
                // Optional cleanup: drop very old round entries
                var keysToRemove = new List<(int Round, string Player)>();
                foreach (var key in _roundKillCounts.Keys)
                {
                    if (key.Round < _labelRoundNumber - 2)
                    {
                        keysToRemove.Add(key);
                    }
                }
                foreach (var key in keysToRemove)
                {
                    _roundKillCounts.Remove(key);
                }
            }
        }
    }

    private void OnWebSocketMessage(object? sender, string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp))
                return;
            if (!string.Equals(typeProp.GetString(), "killfeed_event", StringComparison.Ordinal))
                return;

            if (!TryReadAttacker(root, out var attackerSlot, out var attackerName))
                return;

            int focusedSlot;
            int roundNumber;
            int labelRound;
            var config = SnapshotSettings();
            if (!config.Enabled)
                return;
            lock (_sync)
            {
                focusedSlot = _focusedObserverSlot;
                roundNumber = _currentRoundNumber;
                labelRound = _labelRoundNumber > 0 ? _labelRoundNumber : _currentRoundNumber;
            }

            if (attackerSlot < 0 || attackerSlot != focusedSlot)
                return;

            var roundForLabel = labelRound > 0 ? labelRound : roundNumber;
            var killIndex = GetNextRoundKill(attackerName, roundForLabel);
            HandleCaughtKill(DateTimeOffset.UtcNow, attackerName, roundForLabel, killIndex, config);
        }
        catch
        {
            // ignore malformed messages
        }
    }

    private static bool TryReadAttacker(JsonElement root, out int slot, out string name)
    {
        slot = -1;
        name = string.Empty;
        if (!root.TryGetProperty("attacker", out var attacker) || attacker.ValueKind != JsonValueKind.Object)
            return false;
        if (!attacker.TryGetProperty("observer_slot", out var slotProp))
            return false;

        slot = slotProp.ValueKind switch
        {
            JsonValueKind.Number when slotProp.TryGetInt32(out var v) => v,
            JsonValueKind.String when int.TryParse(slotProp.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) => v,
            _ => -1
        };
        if (attacker.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
        {
            name = nameProp.GetString() ?? string.Empty;
        }
        return slot >= 0;
    }

    private void HandleCaughtKill(DateTimeOffset killTime, string attackerName, int roundNumber, int roundKillNumber, VmixConfig config)
    {
        lock (_sync)
        {
            if (_eventRoundNumber.HasValue && _eventRoundNumber.Value != roundNumber && roundNumber > 0)
            {
                ResetStateForNewEvent(killTime, roundNumber, config);
            }

            if (_firstKillTime == null || !_markCreated)
            {
                if (_firstKillTime == null)
                {
                    _firstKillTime = killTime;
                    _eventRoundNumber = roundNumber;
                    _eventKills.Clear();
                }
                _lastKillTime = killTime;
                AddEventKill(attackerName, roundKillNumber);
                _markCreated = false;
                ScheduleMark(config);
                return;
            }

            if (_lastKillTime.HasValue && (killTime - _lastKillTime.Value).TotalSeconds <= config.ExtendWindowSeconds)
            {
                _lastKillTime = killTime;
                AddEventKill(attackerName, roundKillNumber);
                ScheduleExtend(config);
            }
            else
            {
                ResetStateForNewEvent(killTime, roundNumber, config);
                AddEventKill(attackerName, roundKillNumber);
            }
        }
    }

    private void ScheduleMark(VmixConfig config)
    {
        _markCts?.Cancel();
        _markCts = new CancellationTokenSource();
        var cts = _markCts;

        var lastKill = _lastKillTime ?? DateTimeOffset.UtcNow;
        var delay = lastKill + TimeSpan.FromSeconds(config.PostSeconds) - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                await SendMarkAsync(cts.Token, config).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // canceled
            }
        }, cts.Token);
    }

    private async Task SendMarkAsync(CancellationToken token, VmixConfig config)
    {
        DateTimeOffset firstKill;
        DateTimeOffset lastKill;
        lock (_sync)
        {
            firstKill = _firstKillTime ?? DateTimeOffset.UtcNow;
            lastKill = _lastKillTime ?? firstKill;
        }

        var valueSeconds = (lastKill - firstKill).TotalSeconds + config.PreSeconds + config.PostSeconds;
        if (valueSeconds < config.PreSeconds + config.PostSeconds)
        {
            valueSeconds = config.PreSeconds + config.PostSeconds;
        }

        var uri = BuildFunctionUri(FunctionMark, Math.Ceiling(valueSeconds).ToString(CultureInfo.InvariantCulture), config);
        Console.WriteLine($"[VMIX] Marking replay: in ~{valueSeconds:F1}s before now (first kill {firstKill:O}, last kill {lastKill:O})");
        await SafeGetAsync(uri, token, "ReplayMarkInOutLive").ConfigureAwait(false);

        lock (_sync)
        {
            _markCreated = true;
            _markCts = null;
        }

        await ApplyLabelAsync(token, config).ConfigureAwait(false);
    }

    private void ScheduleExtend(VmixConfig config)
    {
        _extendCts?.Cancel();
        _extendCts = new CancellationTokenSource();
        var cts = _extendCts;
        var killTime = _lastKillTime ?? DateTimeOffset.UtcNow;
        var delay = killTime + TimeSpan.FromSeconds(config.PostSeconds) - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        _ = Task.Run(async () =>
        {
            try
            {
                Console.WriteLine("[VMIX] Selecting last replay event for extension");
                await SafeGetAsync(BuildFunctionUri(FunctionSelectLast, null, config), cts.Token, "ReplaySelectLastEvent").ConfigureAwait(false);
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                Console.WriteLine("[VMIX] Jumping replay to live before extending");
                await SafeGetAsync(BuildFunctionUri(FunctionJumpToNow, null, config), cts.Token, "ReplayJumpToNow").ConfigureAwait(false);
                // Give vMix a brief moment to apply the jump before updating out point
                await Task.Delay(200, cts.Token).ConfigureAwait(false);
                Console.WriteLine($"[VMIX] Extending last replay out point (new out at {DateTimeOffset.UtcNow:O})");
                await SafeGetAsync(BuildFunctionUri(FunctionUpdateOut, null, config), cts.Token, "ReplayUpdateSelectedOutPoint").ConfigureAwait(false);
                await ApplyLabelAsync(cts.Token, config).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // canceled
            }
        }, cts.Token);
    }

    private void ResetStateForNewEvent(DateTimeOffset killTime, int roundNumber, VmixConfig config)
    {
        _markCts?.Cancel();
        _extendCts?.Cancel();
        _firstKillTime = killTime;
        _lastKillTime = killTime;
        _eventRoundNumber = roundNumber;
        _eventKills.Clear();
        _markCreated = false;
        ScheduleMark(config);
    }

    private int GetNextRoundKill(string playerName, int roundNumber)
    {
        lock (_sync)
        {
            var key = (roundNumber, playerName);
            _roundKillCounts.TryGetValue(key, out var count);
            count++;
            _roundKillCounts[key] = count;
            return count;
        }
    }

    private void AddEventKill(string playerName, int roundKillNumber)
    {
        _eventKills.Add(new EventKill(playerName, roundKillNumber));
    }

    private string? BuildLabel()
    {
        if (!_eventRoundNumber.HasValue || _eventKills.Count == 0)
            return null;

        var round = _eventRoundNumber.Value;
        var order = new List<string>();
        var byPlayer = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var kill in _eventKills)
        {
            if (!byPlayer.TryGetValue(kill.PlayerName, out var list))
            {
                list = new List<int>();
                byPlayer[kill.PlayerName] = list;
                order.Add(kill.PlayerName);
            }
            list.Add(kill.RoundKillNumber);
        }

        var parts = new List<string>();
        foreach (var player in order)
        {
            var nums = byPlayer[player];
            nums.Sort();
            string segment = nums.Count == 1
                ? $"{player}_{nums[0]}k"
                : $"{player}_{nums[0]}-{nums[^1]}k";
            parts.Add(segment);
        }

        if (parts.Count == 0)
            return null;

        return $"R{round}_{string.Join("_", parts)}";
    }

    private VmixConfig SnapshotSettings()
    {
        var host = _settings.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            host = "127.0.0.1";
        }

        var port = _settings.Port <= 0 ? 8088 : _settings.Port;
        var pre = Math.Max(0, _settings.PreSeconds);
        var post = Math.Max(0, _settings.PostSeconds);
        var extend = Math.Max(0, _settings.ExtendWindowSeconds);

        return new VmixConfig(_settings.Enabled, host, port, pre, post, extend);
    }

    private Uri BuildFunctionUri(string function, string? value, VmixConfig config)
    {
        var baseUri = $"http://{config.Host}:{config.Port}/api/?Function={function}";
        if (!string.IsNullOrWhiteSpace(value))
        {
            baseUri += $"&Value={value}";
        }
        return new Uri(baseUri);
    }

    private Uri BuildSetTextUri(string label, VmixConfig config)
    {
        return BuildFunctionUri(FunctionSetText, Uri.EscapeDataString(label), config);
    }

    private async Task ApplyLabelAsync(CancellationToken token, VmixConfig config)
    {
        string? label;
        lock (_sync)
        {
            label = BuildLabel();
        }

        if (string.IsNullOrWhiteSpace(label))
            return;

        try
        {
            await SafeGetAsync(BuildFunctionUri(FunctionSelectLast, null, config), token, "ReplaySelectLastEvent (label)").ConfigureAwait(false);
            await SafeGetAsync(BuildSetTextUri(label, config), token, "ReplaySetLastEventText").ConfigureAwait(false);
            Console.WriteLine($"[VMIX] Labeled replay event: {label}");
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
    }

    private async Task SafeGetAsync(Uri uri, CancellationToken token, string? label = null)
    {
        try
        {
            using var response = await _httpClient.GetAsync(uri, token).ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();
        }
        catch
        {
            // swallow errors to avoid disrupting observer flow
            if (!token.IsCancellationRequested)
            {
                Console.WriteLine($"[VMIX] Request failed: {(label ?? uri.ToString())}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _webSocketClient.MessageReceived -= OnWebSocketMessage;
        _gsiServer.GameStateUpdated -= OnGameStateUpdated;

        _markCts?.Cancel();
        _extendCts?.Cancel();
        _httpClient.Dispose();
    }
}
