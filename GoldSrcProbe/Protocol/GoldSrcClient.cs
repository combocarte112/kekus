using System.Net;
using System.Net.Sockets;
using System.Text;
using GoldSrcProbe.Auth;
using GoldSrcProbe.Config;
using GoldSrcProbe.Models;
using GoldSrcProbe.Services;

namespace GoldSrcProbe.Protocol;

public sealed class GoldSrcClient : IDisposable
{
    internal static bool DebugNet;
    internal static Action? OnMultiFragmentStarted;
    private UdpClient _udp;
    private readonly int _bindPort;
    private readonly string _playerName;
    private readonly int _timeoutMs;
    private readonly int _connectRetryCount;
    private readonly int _connectRetryDelayMs;
    private readonly string? _gamePath;
    private readonly AuthEmulatorType _configuredAuthType;
    private AuthEmulatorType _activeAuthType;
    private int _protLevel = 3;
    private bool _bypassEnabled;
    private bool _connectRejected;
    private string? _rejectMessage;
    private string? _lastConnectError;
    private int _lastConnectAttempts;
    private DateTime _state1Since;
    private DateTime _state2Since;
    private string? _downloadUrl;
    private bool _consistencyPending;
    private DateTime _resourceListUtc;
    private string? _consistencyCacheDir;
    private readonly NetChannel _chan = new();
    private readonly UserMessageRegistry _userMessages = new();
    private readonly List<ServerResource> _resources = [];

    private IPEndPoint? _remote;
    private bool _udpConnected;
    private bool _sendEntsSent;
    private bool _spectateSent;
    private bool _consistencySent;
    private bool _consistencyAcked;
    private DateTime _consistencyAckUtc;
    private bool _consistencyRequired;
    private bool _resourceListHandled;
    private bool _forceSpawnWarned;
    private bool _forceSendResWarned;
    private uint _consistencySpawnCount;
    private bool _spawnSent;
    private bool _sendResSent;
    private bool _sendResAfterAck;
    private bool _statusRequested;
    private DateTime _statusSentUtc;
    private int _statusRetries;
    private bool _usersRequested;
    private DateTime _lastMoveUtc;
    private DateTime _spawnCompletedUtc;
    private DateTime _spawnAckUtc;
    private DateTime _spawnSentUtc;
    private DateTime _sendResUtc;
    private DateTime _postSendResActivityUtc;
    private DateTime _nextReliableEmitUtc;
    private DateTime _lastServerDatagramUtc;
    private DateTime _lastSignificantSignonUtc;
    private DateTime _signonReliableCooldownUtc;
    private readonly StringBuilder _console = new();
    private readonly Queue<byte[]> _reliableOutbox = new();
    private readonly List<StatusPlayer> _statusPlayers = [];

    public GoldSrcClient(string playerName, int timeoutMs = 45000, AppConfig? config = null, int? bindPortOverride = null)
    {
        _playerName = playerName.Length > 31 ? playerName[..31] : playerName;
        _timeoutMs = timeoutMs;
        _connectRetryCount = Math.Max(0, config?.ConnectRetryCount ?? 1);
        _connectRetryDelayMs = Math.Max(5000, config?.ConnectRetryDelayMs ?? 20000);
        _gamePath = config?.CsGamePath;
        _configuredAuthType = AuthTicketProvider.Parse(config?.AuthEmulator);
        _activeAuthType = _configuredAuthType == AuthEmulatorType.Auto
            ? AuthEmulatorType.RevEmu2013
            : _configuredAuthType;
        _bindPort = bindPortOverride ?? config?.BindPort ?? 27005;
        _udp = CreateUdp(_bindPort);
        _chan.OnServerFragmentComplete += TouchSignonBurst;
    }

    public int ConnectionState => _chan.ConnectionState;
    public string AuthUsed => AuthTicketProvider.Describe(_activeAuthType);

    private static UdpClient CreateUdp(int bindPort)
    {
        if (bindPort > 0)
        {
            try
            {
                return new UdpClient(new IPEndPoint(IPAddress.Any, bindPort));
            }
            catch (SocketException)
            {
                Console.WriteLine($"  WARN: port {bindPort} busy — using random UDP port");
            }
        }

        return new UdpClient(0);
    }

    public int LocalPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public string AuthMode => AuthTicketProvider.Describe(_configuredAuthType);

    /// <summary>Connect, complete signon, stay on server (visible in scoreboard as spec).</summary>
    public async Task<(bool Ok, string? Error)> JoinAndHoldAsync(
        ServerEndpoint server,
        int holdSeconds,
        CancellationToken ct = default,
        string? manualChallenge = null)
    {
        ResetState();
        try
        {
            _remote = await ResolveAsync(server, ct);
            BindUdpToServer();
            Console.WriteLine($"  Local UDP :{LocalPort} -> {server.Key} (connected)");

            var challenge = await RequestChallengeAsync(ct, manualChallenge);
            if (!await TryConnectWithRetryAsync(ct, manualChallenge))
                return (false, _lastConnectError ?? "No S2C_CONNECTION (rejected or timeout)");

            Console.WriteLine($"  Connected — signon in progress...");

            var deadline = DateTime.UtcNow.AddMilliseconds(_timeoutMs);
            var holdUntil = DateTime.UtcNow.AddSeconds(holdSeconds);

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await PumpNetworkAsync(ct);
                MaybeAdvanceSignon1();
                MaybeSendMove();
                MaybeSendFileConsistency();
                ProcessSignonTick();
                PumpSignonAndFlush();

                if (_chan.ConnectionState >= 3 && !_sendEntsSent && CanSendPostSpawnCommand())
                    SendStringCommand("sendents");

                if (_chan.ConnectionState >= 3 && _sendEntsSent && !_spectateSent && CanSendPostSpawnCommand())
                    SendStringCommand("spectate");

                if (_chan.ConnectionState >= 3 && _sendEntsSent && _spectateSent)
                {
                    if (DateTime.UtcNow >= holdUntil)
                    {
                        Console.WriteLine($"  Held {holdSeconds}s on server — disconnecting.");
                        SendStringCommand("disconnect");
                        FlushReliableOutbox();
                        return (true, null);
                    }
                }

                await Task.Delay(_chan.AwaitingFragments ? 5 : 25, ct);
            }

            return _chan.ConnectionState >= 3
                ? (true, "Timeout before full signon")
                : (false, $"Signon stuck at step {_chan.ConnectionState}/3");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            if (_chan.Connected)
                TryDisconnect();
        }
    }

    /// <summary>Join server, run HLDS status, return player lines with steamid/ping/time.</summary>
    public async Task<ServerVerifyResult> VerifyPlayersAsync(ServerEndpoint server, CancellationToken ct = default)
    {
        var result = new ServerVerifyResult { Address = server.Key };
        ResetState();
        try
        {
            _remote = await ResolveAsync(server, ct);
            BindUdpToServer();
            Console.WriteLine($"  Local UDP :{LocalPort} -> {server.Key}");

            if (!await TryConnectWithRetryAsync(ct))
            {
                result.Error = _lastConnectError ?? "No S2C_CONNECTION";
                result.AuthUsed = AuthUsed;
                result.Reachability = ConnectReject.ReachabilityFor(_lastConnectError);
                result.ConnectAttempts = _lastConnectAttempts;
                return result;
            }

            result.AuthUsed = AuthUsed;
            result.ConnectAttempts = _lastConnectAttempts;
            var deadline = DateTime.UtcNow.AddMilliseconds(_timeoutMs);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await PumpNetworkAsync(ct);
                MaybeAdvanceSignon1();
                MaybeSendMove();
                MaybeSendFileConsistency();
                ProcessSignonTick();
                PumpSignonAndFlush();
                MaybeRequestStatus();

                if (_chan.ConnectionState >= 3 && HasCompleteStatus())
                    break;

                if (_statusRequested && _statusRetries >= 5 &&
                    DateTime.UtcNow - _statusSentUtc > TimeSpan.FromSeconds(10))
                    break;

                await Task.Delay(_chan.AwaitingFragments ? 5 : 50, ct);
            }

            FlushStatusFromConsole();
            result.Joined = _chan.ConnectionState >= 3;
            result.SignonState = _chan.ConnectionState;
            result.RawStatus = _statusPlayers.ToList();
            result.Summary = PlayerAnalyzer.Analyze(_statusPlayers, _playerName);

            if (!result.Joined)
                result.Error = $"Signon stuck at {_chan.ConnectionState}/3";
            else if (result.RawStatus.Count == 0)
            {
                result.Error = "Joined OK — 0 jucatori in status (server gol sau lista ascunsa)";
                result.ConsoleSnippet = TailConsole(30);
            }

            return result;
        }
        catch (TimeoutException ex)
        {
            result.Error = ex.Message;
            result.Reachability = ex.Message.Contains("shield", StringComparison.OrdinalIgnoreCase)
                ? "a2s-shield"
                : "no-response";
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return result;
        }
        finally
        {
            if (_chan.Connected)
                TryDisconnect();
        }
    }

    public async Task<(bool Ok, List<StatusPlayer> Players, string? Error, string RawConsole)> ProbeStatusAsync(
        ServerEndpoint server,
        CancellationToken ct = default)
    {
        ResetState();
        try
        {
            _remote = await ResolveAsync(server, ct);
            BindUdpToServer();
            var challenge = await RequestChallengeAsync(ct, fast: true);
            if (!await TryConnectWithRetryAsync(ct))
                return (false, [], _lastConnectError ?? "Connect failed (no S2C_CONNECTION)", _console.ToString());

            var deadline = DateTime.UtcNow.AddMilliseconds(_timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await PumpNetworkAsync(ct);
                MaybeAdvanceSignon1();
                MaybeSendMove();
                MaybeSendFileConsistency();
                ProcessSignonTick();
                PumpSignonAndFlush();
                MaybeRequestStatus();

                if (HasCompleteStatus())
                    break;

                if (_statusRequested && _statusRetries >= 5 &&
                    DateTime.UtcNow - _statusSentUtc > TimeSpan.FromSeconds(10))
                    break;

                await Task.Delay(_chan.AwaitingFragments ? 5 : 50, ct);
            }

            FlushStatusFromConsole();

            return _statusPlayers.Count > 0
                ? (true, _statusPlayers.ToList(), null, _console.ToString())
                : _chan.ConnectionState >= 3
                    ? (true, [], null, _console.ToString())
                    : (false, [], "Signon incomplete", _console.ToString());
        }
        catch (Exception ex)
        {
            return (false, [], ex.Message, _console.ToString());
        }
        finally
        {
            if (_chan.Connected)
                TryDisconnect();
        }
    }

    private void ResetState()
    {
        _chan.Reset();
        _userMessages.Clear();
        _resources.Clear();
        _console.Clear();
        _statusPlayers.Clear();
        _sendEntsSent = false;
        _spectateSent = false;
        _consistencySent = false;
        _consistencyAcked = false;
        _consistencyAckUtc = default;
        _consistencyRequired = false;
        _resourceListHandled = false;
        _forceSpawnWarned = false;
        _forceSendResWarned = false;
        _consistencySpawnCount = 0;
        _spawnSent = false;
        _sendResSent = false;
        _sendResAfterAck = false;
        _statusRequested = false;
        _statusSentUtc = default;
        _statusRetries = 0;
        _usersRequested = false;
        _reliableOutbox.Clear();
        _validSequence = 0;
        ClcMoveBuilder.Reset();
        _udpConnected = false;
        _connectRejected = false;
        _rejectMessage = null;
        _lastConnectError = null;
        _lastConnectAttempts = 0;
        _state1Since = default;
        _state2Since = default;
        _spawnCompletedUtc = default;
        _spawnAckUtc = default;
        _spawnSentUtc = default;
        _sendResUtc = default;
        _postSendResActivityUtc = default;
        _nextReliableEmitUtc = default;
        _lastServerDatagramUtc = default;
        _lastSignificantSignonUtc = default;
        _signonReliableCooldownUtc = default;
        _protLevel = 3;
        _downloadUrl = null;
        _consistencyPending = false;
        _resourceListUtc = default;
        _consistencyCacheDir = null;
        OnMultiFragmentStarted = null;
        RecreateUdp();
    }

    private void ResetConnectAttempt()
    {
        _chan.Reset();
        _userMessages.Clear();
        _resources.Clear();
        _sendEntsSent = false;
        _spectateSent = false;
        _consistencySent = false;
        _consistencyAcked = false;
        _consistencyAckUtc = default;
        _consistencyRequired = false;
        _resourceListHandled = false;
        _forceSpawnWarned = false;
        _forceSendResWarned = false;
        _consistencySpawnCount = 0;
        _spawnSent = false;
        _sendResSent = false;
        _sendResAfterAck = false;
        _statusRequested = false;
        _statusSentUtc = default;
        _statusRetries = 0;
        _usersRequested = false;
        _reliableOutbox.Clear();
        _validSequence = 0;
        ClcMoveBuilder.Reset();
        _connectRejected = false;
        _rejectMessage = null;
        _state1Since = default;
        _state2Since = default;
        _spawnCompletedUtc = default;
        _spawnAckUtc = default;
        _spawnSentUtc = default;
        _sendResUtc = default;
        _postSendResActivityUtc = default;
        _nextReliableEmitUtc = default;
        _lastServerDatagramUtc = default;
        _lastSignificantSignonUtc = default;
        _signonReliableCooldownUtc = default;
        _consistencyPending = false;
        _resourceListUtc = default;
        RecreateUdp();
    }

    private async Task<bool> TryConnectWithRetryAsync(CancellationToken ct, string? manualChallenge = null)
    {
        var maxAttempts = 1 + _connectRetryCount;
        _lastConnectAttempts = 0;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            _lastConnectAttempts = attempt;

            if (attempt > 1)
            {
                var waitSec = _connectRetryDelayMs / 1000.0;
                Console.WriteLine($"  Retry {attempt - 1}/{_connectRetryCount} — astept {waitSec:0.#}s...");
                await Task.Delay(_connectRetryDelayMs, ct);
                DrainPending(150);
                ResetConnectAttempt();
            }

            if (await TryConnectWithBypassAsync(ct, attempt == 1 ? manualChallenge : null))
                return true;

            if (attempt >= maxAttempts || !ConnectReject.IsRetriable(_lastConnectError))
                break;
        }

        return false;
    }

    private async Task<bool> TryConnectWithBypassAsync(CancellationToken ct, string? manualChallenge = null)
    {
        _bypassEnabled = _configuredAuthType == AuthEmulatorType.Auto;
        var authAttempts = _bypassEnabled
            ? AuthTicketProvider.GetBypassSequence()
            : new[] { _configuredAuthType };
        var protLevels = _bypassEnabled ? new[] { 3, 2 } : new[] { 3 };
        string? lastError = null;
        var usedManualChallenge = false;

        foreach (var prot in protLevels)
        {
            foreach (var auth in authAttempts)
            {
                ResetConnectAttempt();
                _activeAuthType = auth;
                _protLevel = prot;

                string challenge;
                if (!usedManualChallenge && !string.IsNullOrWhiteSpace(manualChallenge))
                {
                    challenge = manualChallenge.Trim();
                    usedManualChallenge = true;
                    Console.WriteLine($"  Challenge : {challenge} (manual)");
                }
                else
                {
                    challenge = await RequestChallengeAsync(ct, fast: true);
                }

                if (_bypassEnabled)
                    Console.WriteLine($"  Auth try: {AuthTicketProvider.Describe(auth)} prot={prot}");

                await SendConnectAsync(challenge, ct);

                if (await WaitForConnectionAsync(ct, perAttemptMs: 8000))
                {
                    if (_bypassEnabled)
                        Console.WriteLine($"  OK — {AuthTicketProvider.Describe(auth)} prot={prot}");
                    return true;
                }

                lastError = ConnectReject.Normalize(_rejectMessage);
                if (string.IsNullOrEmpty(lastError))
                    lastError = "No S2C_CONNECTION";

                if (_bypassEnabled)
                {
                    if (ConnectReject.IsAuthReject(lastError))
                        Console.WriteLine($"  skip — {lastError} (next auth)");
                    else
                        Console.WriteLine($"  FAIL — {lastError}");
                }

                if (ConnectReject.IsDefinitive(lastError))
                {
                    _lastConnectError = lastError;
                    return false;
                }

                if (ConnectReject.IsAuthReject(lastError))
                {
                    BindUdpToServer();
                    DrainPending(300);
                    await Task.Delay(2500, ct);
                }
            }
        }

        _lastConnectError = lastError;
        return false;
    }

    private void MaybeSendFileConsistency()
    {
        ProcessSignonTick();
    }

    private void MaybeForceSpawn()
    {
        // Signon timing handled by ProcessSignonTick — no forced spawn (causes overflow).
    }

    private async Task PrimeSignonAsync(CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < until && _chan.ConnectionState < 2 && !ct.IsCancellationRequested)
        {
            await PumpNetworkAsync(ct);
            if (!_chan.HasPendingReliable && !_chan.AwaitingFragments)
                PumpSignonAndFlush();
            MaybeAdvanceSignon1();
            await Task.Delay(25, ct);
        }
    }

    private void ProcessSignonTick()
    {
        if (_chan.ConnectionState >= 3)
            return;

        if (_chan.HasPendingReliable || _chan.AwaitingFragments || _reliableOutbox.Count > 0)
            return;

        if (DateTime.UtcNow < _signonReliableCooldownUtc)
            return;

        if (_chan.ConnectionState == 1)
        {
            if (_sendResSent)
            {
                _chan.ConnectionState = 2;
                _state2Since = DateTime.UtcNow;
                return;
            }

            if (IsSignonBurstSettled() ||
                (_state1Since != default && DateTime.UtcNow - _state1Since >= TimeSpan.FromSeconds(20)))
            {
                if (!_forceSendResWarned && DateTime.UtcNow - _state1Since >= TimeSpan.FromSeconds(20))
                {
                    _forceSendResWarned = true;
                    Console.WriteLine("  WARN: signon 1/3 timeout — forcing sendres");
                }

                SendSignonSendRes();
            }

            return;
        }

        if (_chan.ConnectionState != 2)
            return;

        if (_resourceListHandled && !_consistencySent)
        {
            if (_consistencyPending && ShouldSendConsistency())
            {
                _consistencyPending = false;
                var cacheRoot = Path.Combine(Directory.GetCurrentDirectory(), "output", "consistency_cache");
                _consistencyCacheDir = _remote is null
                    ? null
                    : Path.Combine(cacheRoot, $"{_remote.Address}_{_remote.Port}");

                if (!string.IsNullOrWhiteSpace(_downloadUrl))
                    Console.WriteLine($"  [net] fastdl: {_downloadUrl}");

                ConsistencyPreparer.Prepare(_resources, _downloadUrl, _gamePath, _remote);
                SendFileConsistency();
            }

            return;
        }

        if (_consistencySent && !_consistencyAcked)
            return;

        if (!_resourceListHandled && _sendResUtc != default &&
            DateTime.UtcNow - _sendResUtc < TimeSpan.FromSeconds(12))
            return;

        if (!_spawnSent && CanSendSpawn())
            SendSignonSpawn();
    }

    private bool ShouldSendConsistency()
    {
        if (DateTime.UtcNow - _resourceListUtc < TimeSpan.FromSeconds(1))
            return false;

        if (!IsSignonBurstSettled())
            return false;

        if (string.IsNullOrWhiteSpace(_downloadUrl) &&
            DateTime.UtcNow - _resourceListUtc < TimeSpan.FromSeconds(4))
            return false;

        return true;
    }

    private void MaybeAdvanceSignon1()
    {
        ProcessSignonTick();
    }

    /// <summary>Windows: after Disconnect(true), UdpClient must be recreated before Connect().</summary>
    private void RecreateUdp()
    {
        try
        {
            if (_udp.Client.Connected)
                _udp.Client.Disconnect(false);
        }
        catch (SocketException)
        {
            // ignore
        }

        try
        {
            _udp.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }

        _udp = CreateUdp(_bindPort);
        _udpConnected = false;
    }

    /// <summary>Connected UDP (like real CS / Skillartz tool) — required on many RO hosts.</summary>
    private void BindUdpToServer()
    {
        if (_remote is null)
            return;

        if (_udp.Client.Connected)
        {
            try { _udp.Client.Disconnect(true); } catch (SocketException) { }
        }

        _udp.Connect(_remote);
        _udpConnected = true;
    }

    private void SendUdp(byte[] data)
    {
        if (DebugNet && data.Length >= 8)
        {
            var w1 = BitConverter.ToUInt32(data, 0);
            var kind = (w1 & 0x80000000) != 0 ? "rel"
                : ((w1 & 0x40000000) != 0 ? "frag"
                : (data.Length == 16 && data[8] == ProtocolConstants.SvcNop ? "ack" : (data.Length > 8 ? "unrel" : "ack")));
            Console.WriteLine($"  [net] out {kind} seq={w1 & 0x3FFFFFFF} len={data.Length}");
        }
        if (_udpConnected)
            _udp.Send(data);
        else
            _udp.Send(data, _remote!);
    }

    private async Task SendUdpAsync(byte[] data, CancellationToken ct)
    {
        if (_udpConnected)
            await _udp.SendAsync(data, ct);
        else
            await _udp.SendAsync(data, _remote!, ct);
    }

    /// <summary>OOB header must be raw 0xFF bytes — ASCII.GetBytes("\xff") becomes 0x3F.</summary>
    private static byte[] BuildConnectionlessOob(string payload)
    {
        var body = Encoding.ASCII.GetBytes(payload);
        var packet = new byte[4 + body.Length];
        packet[0] = packet[1] = packet[2] = packet[3] = 0xFF;
        Buffer.BlockCopy(body, 0, packet, 4, body.Length);
        return packet;
    }

    private void MaybeRequestStatus()
    {
        if (!_chan.Connected || _chan.ConnectionState < 3)
            return;

        if (_chan.HasPendingReliable || _chan.AwaitingFragments || _reliableOutbox.Count > 0)
            return;

        if (!CanSendPostSpawnCommand())
            return;

        if (!_sendEntsSent)
        {
            // sendents floods reliable on strict ReUnion hosts — status works after spawn alone
            _sendEntsSent = true;
        }

        if (_statusPlayers.Count > 0)
            return;

        if (_statusRetries >= 2 && !_usersRequested)
        {
            SendStringCommand("users");
            _usersRequested = true;
            return;
        }

        var canRetry = !_statusRequested ||
                       (_statusRetries < 6 && DateTime.UtcNow - _statusSentUtc > TimeSpan.FromSeconds(2));
        if (!canRetry)
            return;

        SendStringCommand("status");
        _statusRequested = true;
        _statusSentUtc = DateTime.UtcNow;
        _statusRetries++;
    }

    private void FlushStatusFromConsole()
    {
        MergeStatusPlayers(StatusParser.Parse(_console.ToString()));
    }

    private static bool HasCompleteStatus(IReadOnlyList<StatusPlayer> players) =>
        players.Count > 0 &&
        players.All(p => p.IsBot || StatusParser.IsValidSteamIdFormat(p.UniqueId));

    private bool HasCompleteStatus() => HasCompleteStatus(_statusPlayers);

    private void MergeStatusPlayers(IEnumerable<StatusPlayer> parsed)
    {
        foreach (var p in parsed)
        {
            var idx = _statusPlayers.FindIndex(x => x.UserId == p.UserId);
            if (idx >= 0)
                _statusPlayers[idx] = p;
            else
                _statusPlayers.Add(p);
        }
    }

    private void IngestStatusText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (!text.Contains('#') &&
            !text.Contains("STEAM_", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("BOT", StringComparison.OrdinalIgnoreCase))
            return;

        MergeStatusPlayers(StatusParser.Parse(text));
    }

    private string TailConsole(int maxLines)
    {
        var lines = _console.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length <= maxLines)
            return _console.ToString();
        return string.Join('\n', lines[^maxLines..]);
    }

    private int _validSequence;

    private void MaybeSendMove()
    {
        if (!_chan.Connected || _chan.ConnectionState < 3)
            return;

        if (DateTime.UtcNow - _lastMoveUtc < TimeSpan.FromMilliseconds(100))
            return;

        _lastMoveUtc = DateTime.UtcNow;
        SendUnreliable(ClcMoveBuilder.Build(_chan.OutSequence));
    }

    private void SendUnreliable(ReadOnlySpan<byte> message)
    {
        var packet = new byte[ProtocolConstants.ConnectedHeaderSize + message.Length];
        BitConverter.TryWriteBytes(packet.AsSpan(0, 4), _chan.OutSequence);
        BitConverter.TryWriteBytes(packet.AsSpan(4, 4), _chan.InSequence | (_chan.InReliableSequence << 31));
        message.CopyTo(packet.AsSpan(8));
        ComMunge.Munge2(packet.AsSpan(8), (int)(_chan.OutSequence & 0xFF));
        _chan.OutSequence++;
        SendUdp(packet);
    }

    private async Task PumpNetworkAsync(CancellationToken ct)
    {
        var waitMs = _chan.AwaitingFragments ? 300 : 100;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(waitMs);

        try
        {
            while (!timeout.Token.IsCancellationRequested)
            {
                var result = await _udp.ReceiveAsync(timeout.Token);
                if (!_udpConnected &&
                    (_remote is null ||
                     !result.RemoteEndPoint.Address.Equals(_remote.Address) ||
                     result.RemoteEndPoint.Port != _remote.Port))
                    continue;

                HandleDatagram(result.Buffer);
            }
        }
        catch (OperationCanceledException)
        {
            // normal
        }

        // Retransmit only after full signon — ackRel mismatch is normal during signon burst
        if (_chan.ConnectionState >= 3 && _spawnCompletedUtc != default)
        {
            var retry = _chan.RetryPendingReliable();
            if (retry is not null)
                await SendUdpAsync(retry, ct);
        }
    }

    private void HandleDatagram(byte[] data)
    {
        if (data.Length < 4)
            return;

        _lastServerDatagramUtc = DateTime.UtcNow;

        var oob = BitConverter.ToInt32(data, 0);
        if (oob == -1)
        {
            ProcessConnectionless(data.AsSpan(4));
            return;
        }

        if (oob == -2)
            return;

        var hadPendingReliable = _chan.HasPendingReliable;
        var ack = _chan.ProcessIncoming(data, payload => ProcessServerMessages(payload));
        if (_consistencySent && !_consistencyAcked && hadPendingReliable && !_chan.HasPendingReliable &&
            !_chan.HasMoreOutboundFragments)
        {
            _consistencyAcked = true;
            _consistencyAckUtc = DateTime.UtcNow;
        }
        if (_spawnSent && _spawnAckUtc == default && hadPendingReliable && !_chan.HasPendingReliable &&
            !_chan.HasMoreOutboundFragments)
            _spawnAckUtc = DateTime.UtcNow;

        if (hadPendingReliable && !_chan.HasPendingReliable && _chan.ConnectionState < 3 &&
            !_chan.HasMoreOutboundFragments)
            _signonReliableCooldownUtc = DateTime.UtcNow.AddMilliseconds(600);
        _validSequence = (int)(_chan.InSequence & 0xFF);
        if (ack is not null)
            SendUdp(ack);
        if (_sendResAfterAck)
        {
            if (_resourceListHandled || IsSignonBurstSettled())
            {
                _sendResAfterAck = false;
                SendSignonSendRes();
                if (_chan.ConnectionState == 1)
                {
                    _chan.ConnectionState = 2;
                    _state2Since = DateTime.UtcNow;
                }
            }
        }

        MaybeCompleteSignonStep1();
        FlushReliableOutbox();
    }

    private void PumpSignonAndFlush()
    {
        MaybeCompleteSignonStep1();
        MaybeAdvanceSignonAfterConsistency();
        MaybeCompleteSpawn();
        FlushReliableOutbox();
    }

    private bool CanSendPostSpawnCommand()
    {
        if (_chan.HasPendingReliable || _chan.AwaitingFragments || _reliableOutbox.Count > 0)
            return false;

        if (_spawnCompletedUtc == default)
            return false;

        return DateTime.UtcNow - _spawnCompletedUtc >= TimeSpan.FromSeconds(3);
    }

    private void FlushReliableOutbox()
    {
        if (_chan.ConnectionState >= 3 && _spawnCompletedUtc != default)
        {
            var resend = _chan.RetryPendingReliable();
            if (resend is not null)
            {
                SendUdp(resend);
                return;
            }
        }

        if (DateTime.UtcNow < _nextReliableEmitUtc)
            return;

        if (_chan.HasPendingReliable && !_chan.HasMoreOutboundFragments)
            return;

        if (_chan.ConnectionState < 3 && DateTime.UtcNow < _signonReliableCooldownUtc)
            return;

        var packet = _chan.TryEmitNextReliable(_reliableOutbox);
        if (packet is null)
            return;

        SendUdp(packet);

        while (_chan.HasMoreOutboundFragments)
        {
            packet = _chan.TryEmitNextReliable(_reliableOutbox);
            if (packet is null)
                break;
            SendUdp(packet);
        }

        _nextReliableEmitUtc = DateTime.UtcNow.AddMilliseconds(_chan.ConnectionState < 3 ? 500 : 100);
    }

    private void MaybeCompleteSignonStep1()
    {
        if (_chan.ConnectionState != 1 || _chan.AwaitingFragments || _chan.HasPendingReliable)
            return;

        if (_sendResSent)
        {
            _chan.ConnectionState = 2;
            _state2Since = DateTime.UtcNow;
            return;
        }

        if (_sendResAfterAck)
            return;
    }

    private void TouchSignonBurst()
    {
        _lastSignificantSignonUtc = DateTime.UtcNow;
        if (_sendResSent && _chan.ConnectionState <= 2)
            _postSendResActivityUtc = DateTime.UtcNow;
    }

    private bool IsSignonBurstSettled()
    {
        if (_chan.HasPendingReliable || _chan.AwaitingFragments || _reliableOutbox.Count > 0)
            return false;

        if (_lastSignificantSignonUtc == default)
            return false;

        return DateTime.UtcNow - _lastSignificantSignonUtc >= TimeSpan.FromMilliseconds(1500);
    }

    private bool CanSendSpawn()
    {
        if (_sendResUtc == default || DateTime.UtcNow - _sendResUtc < TimeSpan.FromSeconds(5))
            return false;

        if (_postSendResActivityUtc != default &&
            DateTime.UtcNow - _postSendResActivityUtc < TimeSpan.FromSeconds(2))
            return false;

        if (_resourceListHandled)
        {
            if (!_consistencySent || !_consistencyAcked)
                return false;

            if (_resourceListUtc == default || DateTime.UtcNow - _resourceListUtc < TimeSpan.FromSeconds(2))
                return false;
        }
        else if (_sendResUtc == default || DateTime.UtcNow - _sendResUtc < TimeSpan.FromSeconds(12))
        {
            return false;
        }

        if (_consistencyAckUtc != default &&
            DateTime.UtcNow - _consistencyAckUtc < TimeSpan.FromSeconds(2))
            return false;

        return IsSignonBurstSettled();
    }

    private void MaybeAdvanceSignonAfterConsistency()
    {
        ProcessSignonTick();
    }

    private async Task<bool> WaitForConnectionAsync(CancellationToken ct, int? perAttemptMs = null)
    {
        var timeout = perAttemptMs ?? 15000;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_connectRejected)
                return false;

            await PumpNetworkAsync(ct);
            if (_chan.Connected)
                return true;
            await Task.Delay(50, ct);
        }

        return _chan.Connected;
    }

    private void ProcessConnectionless(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0)
            return;

        if (body[0] == ProtocolConstants.S2CConnection)
        {
            _chan.Connected = true;
            SendSignonNew();
            return;
        }

        if (body[0] == ProtocolConstants.S2CReject)
        {
            var msg = Encoding.ASCII.GetString(body);
            _console.AppendLine(msg);
            var trimmed = msg.TrimStart('\0', '9').Trim();
            Console.WriteLine($"  Server reject: {trimmed}");
            if (_bypassEnabled && !_chan.Connected)
            {
                _connectRejected = true;
                _rejectMessage = trimmed;
                return;
            }

            throw new InvalidOperationException($"Rejected: {trimmed}");
        }

        _console.AppendLine(Encoding.ASCII.GetString(body));
    }

    private void ProcessServerMessages(byte[] payload)
    {
        if (DebugNet)
            Console.WriteLine($"  [net] svc payload {payload.Length}b op=0x{(payload.Length > 0 ? payload[0] : 0):X2}");
        var offset = 0;
        while (offset < payload.Length)
        {
            var msgType = payload[offset++];
            offset = HandleServerMessage(msgType, payload, offset);
            if (offset > payload.Length)
                break;
        }
    }

    private int HandleServerMessage(byte msgType, byte[] data, int offset)
    {
        switch (msgType)
        {
            case ProtocolConstants.SvcNop:
            case ProtocolConstants.SvcChoke:
            case ProtocolConstants.SvcBad:
                return offset;
            case ProtocolConstants.SvcPrint:
                return ReadPrint(data, offset);
            case ProtocolConstants.SvcCenterPrint:
                return SkipString(data, offset);
            case ProtocolConstants.SvcStuffText:
            {
                var text = ReadCString(data, offset);
                _console.AppendLine(text);
                IngestStatusText(text);
                return SkipString(data, offset);
            }
            case ProtocolConstants.SvcDisconnect:
            {
                var reason = ReadCString(data, offset);
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(reason) ? "Server sent svc_disconnect" : $"Disconnect: {reason}");
            }
            case ProtocolConstants.SvcSignonNum:
                return offset + Math.Min(1, data.Length - offset);
            case ProtocolConstants.SvcServerInfo:
                return ParseServerInfo(data, offset);
            case ProtocolConstants.SvcSendExtraInfo:
                TouchSignonBurst();
                offset = SkipString(data, offset);
                return offset + Math.Min(1, data.Length - offset);
            case ProtocolConstants.SvcNewMoveVars:
                TouchSignonBurst();
                offset += Math.Min(92, data.Length - offset);
                offset = MessageSkipper.SkipString(data, offset);
                return offset;
            case ProtocolConstants.SvcNewUserMsg:
                return ParseNewUserMsg(data, offset);
            case ProtocolConstants.SvcResourceList:
                if (_resourceListHandled)
                    return data.Length;

                _resourceListHandled = true;
                _resources.Clear();
                List<ServerResource> parsed;
                int wireConsistencyCount;
                (parsed, _consistencyRequired, wireConsistencyCount) = ResourceListParser.Parse(data, offset, _gamePath);
                _resources.AddRange(parsed);
                if (DebugNet)
                {
                    var need = _resources.Count(r => r.NeedConsistency);
                    Console.WriteLine($"  [net] resource list: {_resources.Count} entries, consistency={_consistencyRequired}, wire={wireConsistencyCount}, marked={need}");
                }

                TouchSignonBurst();
                _resourceListUtc = DateTime.UtcNow;
                _consistencyPending = true;

                return data.Length;
            case ProtocolConstants.SvcResourceRequest:
                if (offset + 4 <= data.Length)
                {
                    _consistencySpawnCount = BitConverter.ToUInt32(data, offset);
                    _chan.SpawnCount = _consistencySpawnCount;
                }
                return offset + Math.Min(8, data.Length - offset);
            case ProtocolConstants.SvcResourceLocation:
            {
                var url = ReadCString(data, offset);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    _downloadUrl = url.Trim();
                    TouchSignonBurst();
                    if (DebugNet)
                        Console.WriteLine($"  [net] fastdl: {_downloadUrl}");
                }

                return SkipString(data, offset);
            }
            case ProtocolConstants.SvcDeltaDescription:
                TouchSignonBurst();
                return MessageSkipper.SkipDeltaDescription(data, offset);
            case ProtocolConstants.SvcCustomization:
            case ProtocolConstants.SvcSound:
            case ProtocolConstants.SvcSpawnBaseline:
            case ProtocolConstants.SvcPacketEntities:
            case ProtocolConstants.SvcClientData:
            case ProtocolConstants.SvcSetView:
            case ProtocolConstants.SvcTempEntity:
                return data.Length;
            case ProtocolConstants.SvcSetAngle:
                return offset + Math.Min(6, data.Length - offset);
            case ProtocolConstants.SvcLightStyle:
                if (offset < data.Length)
                    offset++;
                return SkipString(data, offset);
            case ProtocolConstants.SvcUpdateUserinfo:
                if (offset < data.Length)
                    offset++;
                return SkipString(data, offset);
            case ProtocolConstants.SvcDeltaPacketEntities:
                if (offset + 3 <= data.Length)
                    _validSequence = data[offset + 2];
                return data.Length;
            case ProtocolConstants.SvcTime:
                return offset + Math.Min(4, data.Length - offset);
            default:
                if (msgType >= ProtocolConstants.SvcUserMessageStart)
                    return HandleUserMessage(msgType, data, offset);
                if (DebugNet)
                    Console.WriteLine($"  [net] unknown svc 0x{msgType:X2} at {offset - 1}, skipping byte");
                return offset;
        }
    }

    private int ParseNewUserMsg(byte[] data, int offset)
    {
        if (offset + 9 > data.Length)
            return data.Length;

        var msgId = data[offset];
        var nameOffset = offset + 1;
        var nameEnd = nameOffset;
        while (nameEnd < data.Length && data[nameEnd] != 0)
            nameEnd++;
        var name = Encoding.ASCII.GetString(data, nameOffset, nameEnd - nameOffset);
        offset = nameEnd + 1;
        if (offset >= data.Length)
            return data.Length;

        var size = data[offset];
        _userMessages.Register(msgId, name, size);
        return offset + 1;
    }

    private int HandleUserMessage(byte msgType, byte[] data, int offset)
    {
        if (!_userMessages.TryGet(msgType, out var info))
            return data.Length;

        var msgSize = info.Size;
        if (msgSize == 255)
        {
            if (offset >= data.Length)
                return data.Length;
            msgSize = data[offset++];
        }

        if (offset + msgSize > data.Length)
            return data.Length;

        if (ReAuthCheckerHandler.IsReAuthStage(msgType, info))
        {
            // ReAuthChecker needs moves only after full signon
        }

        return offset + msgSize;
    }

    private int ParseServerInfo(byte[] data, int offset)
    {
        TouchSignonBurst();
        if (offset + 28 > data.Length)
            return data.Length;

        _chan.SpawnCount = BitConverter.ToUInt32(data, offset + 4);
        _chan.PlayerNumber = data[offset + 25];
        var crc = new byte[4];
        Buffer.BlockCopy(data, offset + 8, crc, 0, 4);
        ComMunge.UnMunge3(crc, (-1 - _chan.PlayerNumber) & 0xFF);
        _chan.WorldmapCrc = BitConverter.ToInt32(crc, 0);

        offset += 28;
        for (var i = 0; i < 4; i++)
            offset = SkipString(data, offset);
        if (offset < data.Length)
            offset++;
        return offset;
    }

    private int ReadPrint(byte[] data, int offset)
    {
        var start = offset;
        while (offset < data.Length && data[offset] != 0)
            offset++;
        if (offset <= data.Length)
        {
            var text = Encoding.UTF8.GetString(data, start, offset - start);
            _console.AppendLine(text);
            if (DebugNet)
                Console.WriteLine($"  [net] svc_print: {text.Trim()}");
            if (_chan.ConnectionState == 1 && text.Contains("Privileges", StringComparison.OrdinalIgnoreCase))
                _sendResAfterAck = true;
            IngestStatusText(text);
            if (offset < data.Length)
                offset++;
        }
        return offset;
    }

    private static string ReadCString(byte[] data, int offset)
    {
        var start = offset;
        while (offset < data.Length && data[offset] != 0)
            offset++;
        return start < offset ? Encoding.UTF8.GetString(data, start, offset - start) : "";
    }

    private static int SkipString(byte[] data, int offset)
    {
        while (offset < data.Length && data[offset] != 0)
            offset++;
        if (offset < data.Length)
            offset++;
        return offset;
    }

    private void AdvanceSignon()
    {
        if (_chan.HasPendingReliable || _chan.AwaitingFragments || _reliableOutbox.Count > 0)
            return;

        if (_chan.ConnectionState == 1 && !IsSignonBurstSettled())
            return;

        if (_chan.ConnectionState == 0)
            SendSignonNew();
        else if (_chan.ConnectionState == 1)
        {
            SendSignonSendRes();
            _chan.ConnectionState = 2;
            _state2Since = DateTime.UtcNow;
        }
        if (_consistencySent && !_consistencyAcked)
            return;

        if (_spawnSent)
            return;

        if (_chan.ConnectionState == 2 && CanSendSpawn())
            SendSignonSpawn();
    }

    private void SendSignonSendRes()
    {
        if (_sendResSent)
            return;
        var payload = new byte[] { ProtocolConstants.ClcStringCmd, (byte)'s', (byte)'e', (byte)'n', (byte)'d', (byte)'r', (byte)'e', (byte)'s', 0 };
        SendReliable(payload);
        _sendResSent = true;
        _sendResUtc = DateTime.UtcNow;
    }

    private void SendSignonNew()
    {
        var payload = new byte[] { ProtocolConstants.ClcStringCmd, (byte)'n', (byte)'e', (byte)'w', 0 };
        SendReliable(payload);
        _chan.ConnectionState = 1;
        _state1Since = DateTime.UtcNow;
    }

    private void SendSignonSpawn()
    {
        if (_spawnSent)
            return;

        _spawnSent = true;
        _spawnSentUtc = DateTime.UtcNow;
        if (DebugNet)
            Console.WriteLine($"  [net] spawn: count={_chan.SpawnCount} crc={_chan.WorldmapCrc}");
        var worldCrc = (int)_chan.WorldmapCrc;
        Span<byte> crcBytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(crcBytes, worldCrc);
        ComMunge.Munge2(crcBytes, (-1 - (int)_chan.SpawnCount) & 0xFF);
        worldCrc = BitConverter.ToInt32(crcBytes);

        var cmd = Encoding.ASCII.GetBytes($"spawn {_chan.SpawnCount} {worldCrc}\0");
        var payload = new byte[1 + cmd.Length];
        payload[0] = ProtocolConstants.ClcStringCmd;
        cmd.CopyTo(payload.AsSpan(1));
        SendReliable(payload);
    }

    private void MaybeCompleteSpawn()
    {
        if (!_spawnSent || _chan.ConnectionState >= 3)
            return;

        if (_chan.HasPendingReliable || _chan.AwaitingFragments || _reliableOutbox.Count > 0)
            return;

        if (_spawnAckUtc != default)
        {
            if (DateTime.UtcNow - _spawnAckUtc < TimeSpan.FromSeconds(1))
                return;
        }
        else if (_spawnSentUtc == default || DateTime.UtcNow - _spawnSentUtc < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _chan.ConnectionState = 3;
        _spawnCompletedUtc = DateTime.UtcNow;
    }

    private void SendFileConsistency()
    {
        var needCount = _resources.Count(r => r.NeedConsistency);
        var mungeKey = (uint)(_consistencySpawnCount != 0 ? _consistencySpawnCount : _chan.SpawnCount);
        if (DebugNet)
            Console.WriteLine($"  [net] file consistency: required={_consistencyRequired}, wire/marked={needCount}, mungeKey={mungeKey}");

        _consistencySent = true;

        if (_consistencyRequired && needCount == 0)
            Console.WriteLine("  [net] ERROR: server requires consistency but parser marked 0 files");

        if (_consistencyRequired || needCount > 0)
        {
            var body = FileConsistencyBuilder.Build(_resources, mungeKey, _gamePath, _consistencyCacheDir);
            if (DebugNet && needCount > 0)
                Console.WriteLine($"  [net] consistency packet {body.Length}b");
            if (needCount > 0)
                DumpConsistencyList(needCount);
            SendReliable(body);
        }
        else
        {
            _consistencyAcked = true;
            _consistencyAckUtc = DateTime.UtcNow;
        }
    }

    private void DumpConsistencyList(int needCount)
    {
        try
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "output", "consistency_debug.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var w = new StreamWriter(path, append: false);
            w.WriteLine($"# spawncount={_chan.SpawnCount} entries={needCount}");
            for (var i = 0; i < _resources.Count; i++)
            {
                var r = _resources[i];
                if (!r.NeedConsistency)
                    continue;
                var hash = BitConverter.ToUInt32(r.Md5, 0);
                var src = hash != 0 ? $"hash=0x{hash:X8}" : "hash=MISSING";
                w.WriteLine($"[{i,4}] {r.Name} {src} reserved={(IsReservedEmpty(r) ? "no" : "bounds")}");
            }
            if (DebugNet)
                Console.WriteLine($"  [net] consistency list -> {path}");
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsReservedEmpty(ServerResource r)
    {
        foreach (var b in r.Reserved)
        {
            if (b != 0)
                return false;
        }
        return true;
    }

    private void TryDisconnect()
    {
        try
        {
            SendUdp(_chan.BuildAckOnly());
        }
        catch (SocketException)
        {
            // ignore
        }
    }

    private void SendStringCommand(string command)
    {
        var cmd = Encoding.ASCII.GetBytes(command + "\0");
        var payload = new byte[1 + cmd.Length];
        payload[0] = ProtocolConstants.ClcStringCmd;
        cmd.CopyTo(payload.AsSpan(1));
        SendReliable(payload);

        if (command == "sendents")
            _sendEntsSent = true;
        if (command == "spectate")
            _spectateSent = true;
    }

    private void SendReliable(ReadOnlySpan<byte> message)
    {
        if (_chan.ConnectionState < 3 &&
            (_chan.HasPendingReliable || _reliableOutbox.Count > 0 ||
             DateTime.UtcNow < _signonReliableCooldownUtc))
        {
            if (DebugNet)
                Console.WriteLine($"  [net] drop reliable enqueue (signon busy) len={message.Length}");
            return;
        }

        _reliableOutbox.Enqueue(message.ToArray());
    }

    private async Task<IPEndPoint> ResolveAsync(ServerEndpoint server, CancellationToken ct)
    {
        if (IPAddress.TryParse(server.Host, out var ip))
            return new IPEndPoint(ip, server.Port);

        var addresses = await Dns.GetHostAddressesAsync(server.Host, ct);
        var addr = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                   ?? addresses.FirstOrDefault()
                   ?? throw new InvalidOperationException($"Cannot resolve {server.Host}");
        return new IPEndPoint(addr, server.Port);
    }

    private async Task<string> RequestChallengeAsync(CancellationToken ct, string? manualChallenge = null, bool fast = false)
    {
        if (!string.IsNullOrWhiteSpace(manualChallenge))
        {
            Console.WriteLine($"  Challenge : {manualChallenge} (manual)");
            return manualChallenge.Trim();
        }

        if (!fast)
            DrainPending(100);

        var payloads = new[]
        {
            "getchallenge steam\n",
            "getchallenge\n"
        };

        var maxAttempts = fast ? 2 : 5;
        var waitMs = fast ? 1500 : 2500;

        foreach (var payload in payloads)
        {
            var packet = BuildConnectionlessOob(payload);

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                await SendUdpAsync(packet, ct);
                var response = await ReceiveFirstOobAsync(ct, waitMs);
                var probe = ConnectProbe.Analyze(response);
                if (probe.CanJoin && probe.Challenge is not null)
                {
                    if (!fast)
                        Console.WriteLine($"  Challenge : {probe.Challenge} ({payload.Trim()} attempt {attempt + 1})");
                    return probe.Challenge;
                }

                if (!fast && probe.Reachability == ConnectReachability.A2SShield)
                    Console.WriteLine($"  getchallenge shield: {probe.Summary}");

                if (probe.Reachability == ConnectReachability.A2SShield)
                    throw new TimeoutException(probe.Summary);

                if (!fast)
                    Console.WriteLine($"  getchallenge retry ({payload.Trim()}) {attempt + 1}/{maxAttempts}...");

                await Task.Delay(fast ? 150 : 300, ct);
            }
        }

        throw new TimeoutException(
            "getchallenge blocked — IP-ul public are scut A2S (raspuns m/I in loc de A). " +
            "ReHLDS poate fi in spate, dar botul nu poate intra pe IP-ul din browser; foloseste IP HLDS direct.");
    }

    private async Task<byte[]?> ReceiveFirstOobAsync(CancellationToken ct, int timeoutMs)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);

        try
        {
            while (!timeout.Token.IsCancellationRequested)
            {
                var result = await _udp.ReceiveAsync(timeout.Token);
                if (!_udpConnected &&
                    (_remote is null || !result.RemoteEndPoint.Address.Equals(_remote.Address)))
                    continue;

                if (result.Buffer.Length >= 5 && BitConverter.ToInt32(result.Buffer, 0) == -1)
                    return result.Buffer;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        return null;
    }

    private void DrainPending(int ms)
    {
        using var cts = new CancellationTokenSource(ms);
        try
        {
            while (!cts.Token.IsCancellationRequested)
                _ = _udp.ReceiveAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // drained
        }
    }

    /// <summary>Probe if real HLDS/ReHLDS challenge is reachable (not A2S shield).</summary>
    public async Task<ConnectProbeResult> ProbeConnectAsync(ServerEndpoint server, CancellationToken ct = default)
    {
        ResetState();
        try
        {
            _remote = await ResolveAsync(server, ct);
            BindUdpToServer();
            Console.WriteLine($"  Local UDP :{LocalPort} -> {server.Key} (connected)");
            DrainPending(100);

            var payloads = new[] { "getchallenge steam\n", "getchallenge\n" };
            ConnectProbeResult? lastResult = null;

            foreach (var payload in payloads)
            {
                for (var burst = 0; burst < 3; burst++)
                {
                    var packet = BuildConnectionlessOob(payload);
                    await SendUdpAsync(packet, ct);

                    var deadline = DateTime.UtcNow.AddMilliseconds(2500);
                    while (DateTime.UtcNow < deadline)
                    {
                        var response = await ReceiveFirstOobAsync(ct, 400);
                        if (response is null)
                            continue;

                        var result = ConnectProbe.Analyze(response);
                        if (result.CanJoin)
                            return result;

                        lastResult = result;
                    }

                    if (lastResult?.Reachability == ConnectReachability.A2SShield)
                        return lastResult.Value;

                    await Task.Delay(100, ct);
                }
            }

            return lastResult ?? ConnectProbe.Analyze(null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Eroare probe: {ex.GetType().Name}: {ex.Message}");
            return ConnectProbe.Analyze(null);
        }
    }

    public async Task<bool> CanConnectAsync(ServerEndpoint server, CancellationToken ct = default) =>
        (await ProbeConnectAsync(server, ct)).CanJoin;

    private async Task SendConnectAsync(string challenge, CancellationToken ct)
    {
        var cdkey = RevEmuTicket.CreateCdKey();
        var protinfo = $@"\prot\{_protLevel}\unique\-1\raw\steam\cdkey\{cdkey}\qport\{LocalPort} ";
        var userinfo =
            $@"\name\{_playerName}\_cl_autowepswitch\1\bottomcolor\6\cl_dlmax\512\cl_lc\1\cl_lw\1\cl_updaterate\60\model\urban\topcolor\30\_vgui_menus\1\rate\25000";

        var header = BuildConnectionlessOob(
            $"connect {ProtocolConstants.ProtocolVersion} {challenge} {protinfo}{userinfo}\n");
        var ticket = AuthTicketProvider.Generate(_activeAuthType);
        var packet = new byte[header.Length + ticket.Length];
        Buffer.BlockCopy(header, 0, packet, 0, header.Length);
        Buffer.BlockCopy(ticket, 0, packet, header.Length, ticket.Length);

        await SendUdpAsync(packet, ct);
    }

    private async Task<byte[]> ReceiveUntilAsync(CancellationToken ct, int timeoutMs)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);

        try
        {
            while (!timeout.Token.IsCancellationRequested)
            {
                var result = await _udp.ReceiveAsync(timeout.Token);
                if (_remote is null ||
                    (result.RemoteEndPoint.Address.Equals(_remote.Address) &&
                     result.RemoteEndPoint.Port == _remote.Port))
                {
                    if (result.Buffer.Length >= 5 && BitConverter.ToInt32(result.Buffer, 0) == -1)
                        return result.Buffer;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("UDP receive timeout");
        }

        throw new TimeoutException("UDP receive timeout");
    }

    public void Dispose() => _udp.Dispose();
}
