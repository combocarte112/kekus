using System.Net;
using System.Net.Sockets;
using System.Text;
using GoldSrcProbe.Models;

namespace GoldSrcProbe.Protocol;

public sealed class A2SClient : IDisposable
{
    private readonly UdpClient _udp;
    private readonly int _timeoutMs;

    public A2SClient(int timeoutMs = 4000)
    {
        _timeoutMs = timeoutMs;
        _udp = new UdpClient();
    }

    public async Task<(A2SInfo? Info, List<A2SPlayer> Players, string? Error)> QueryAsync(
        ServerEndpoint server,
        CancellationToken ct = default)
    {
        try
        {
            var endpoint = await ResolveAsync(server, ct);
            var info = await GetInfoAsync(endpoint, ct);
            var players = await GetPlayersAsync(endpoint, ct);
            await TryEnrichReHldsAsync(endpoint, info, ct);
            return (info, players, null);
        }
        catch (Exception ex)
        {
            return (null, [], ex.Message);
        }
    }

    private async Task TryEnrichReHldsAsync(IPEndPoint endpoint, A2SInfo info, CancellationToken ct)
    {
        try
        {
            var rules = await GetRulesAsync(endpoint, ct);
            foreach (var (key, value) in rules)
            {
                if (key.Contains("rehlds", StringComparison.OrdinalIgnoreCase) &&
                    key.Contains("version", StringComparison.OrdinalIgnoreCase))
                {
                    info.ReHldsVersion = value;
                    return;
                }
            }

            if (rules.TryGetValue("rehlds_version", out var ver))
                info.ReHldsVersion = ver;
            else if (rules.TryGetValue("sv_rehlds_version", out ver))
                info.ReHldsVersion = ver;
        }
        catch
        {
            // rules often blocked on shielded hosts
        }
    }

    private async Task<Dictionary<string, string>> GetRulesAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        const byte a2sRules = 0x56;
        var challenge = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        var packet = BuildOobPacket(a2sRules, challenge);
        var data = await SendAndReceiveAsync(endpoint, packet, ct);

        var reader = new MessageReader(data);
        reader.ReadInt32();
        var type = reader.ReadByte();

        if (type == ProtocolConstants.S2C_CHALLENGE_BYTE)
        {
            var ch = reader.RemainingSpan.Slice(0, 4).ToArray();
            packet = BuildOobPacket(a2sRules, ch);
            data = await SendAndReceiveAsync(endpoint, packet, ct);
            reader = new MessageReader(data);
            reader.ReadInt32();
            type = reader.ReadByte();
        }

        if (type != 0x45) // 'E' rules response
            return [];

        var count = reader.ReadInt16();
        var rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count && reader.Remaining > 0; i++)
        {
            var key = reader.ReadString();
            var value = reader.ReadString();
            if (!string.IsNullOrEmpty(key))
                rules[key] = value;
        }

        return rules;
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

    private async Task<A2SInfo> GetInfoAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes("Source Engine Query\0");
        var first = await SendAndReceiveAsync(endpoint, BuildOobPacket(ProtocolConstants.A2S_INFO, payload), ct);

        var reader = new MessageReader(first);
        reader.ReadInt32();
        var type = reader.ReadByte();

        if (type == ProtocolConstants.S2C_CHALLENGE_BYTE)
        {
            var challenge = reader.RemainingSpan.Slice(0, 4).ToArray();
            var packet = new byte[4 + 1 + payload.Length + 4];
            packet[0] = 0xFF;
            packet[1] = 0xFF;
            packet[2] = 0xFF;
            packet[3] = 0xFF;
            packet[4] = ProtocolConstants.A2S_INFO;
            Buffer.BlockCopy(payload, 0, packet, 5, payload.Length);
            Buffer.BlockCopy(challenge, 0, packet, 5 + payload.Length, 4);
            first = await SendAndReceiveAsync(endpoint, packet, ct);
            reader = new MessageReader(first);
            reader.ReadInt32();
            type = reader.ReadByte();
        }

        return ParseInfoType(type, reader);
    }

    private async Task<List<A2SPlayer>> GetPlayersAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var challenge = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        var packet = BuildOobPacket(ProtocolConstants.A2S_PLAYER, challenge);
        var data = await SendAndReceiveAsync(endpoint, packet, ct);

        var reader = new MessageReader(data);
        reader.ReadInt32();
        var type = reader.ReadByte();

        if (type == ProtocolConstants.S2C_CHALLENGE_BYTE)
        {
            var ch = reader.RemainingSpan.Slice(0, 4).ToArray();
            packet = BuildOobPacket(ProtocolConstants.A2S_PLAYER, ch);
            data = await SendAndReceiveAsync(endpoint, packet, ct);
            reader = new MessageReader(data);
            reader.ReadInt32();
            type = reader.ReadByte();
        }

        if (type != ProtocolConstants.S2A_PLAYER)
            return [];

        var count = reader.ReadByte();
        var players = new List<A2SPlayer>();
        for (var i = 0; i < count && reader.Remaining > 0; i++)
        {
            reader.ReadByte();
            var name = reader.ReadString();
            var score = reader.ReadInt32();
            var duration = reader.ReadFloat();
            players.Add(new A2SPlayer { Index = i, Name = name, Score = score, DurationSeconds = duration });
        }

        return players;
    }

    private static byte[] BuildOobPacket(byte type, byte[] payload)
    {
        var packet = new byte[4 + 1 + payload.Length];
        packet[0] = 0xFF;
        packet[1] = 0xFF;
        packet[2] = 0xFF;
        packet[3] = 0xFF;
        packet[4] = type;
        Buffer.BlockCopy(payload, 0, packet, 5, payload.Length);
        return packet;
    }

    private async Task<byte[]> SendAndReceiveAsync(IPEndPoint endpoint, byte[] packet, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeoutMs);

        await _udp.SendAsync(packet, endpoint, timeout.Token);

        while (!timeout.Token.IsCancellationRequested)
        {
            var result = await _udp.ReceiveAsync(timeout.Token);
            if (result.RemoteEndPoint.Address.Equals(endpoint.Address) &&
                result.RemoteEndPoint.Port == endpoint.Port)
                return result.Buffer;
        }

        throw new TimeoutException("A2S timeout");
    }

    private static A2SInfo ParseInfoType(byte header, MessageReader r)
    {
        var info = new A2SInfo();

        // GoldSource classic (0x6D) — also first packet from dproto
        if (header == ProtocolConstants.S2A_INFO)
        {
            info.GoldSource = true;
            if (r.Remaining > 0)
                r.ReadString(); // address
            info.Name = r.ReadString();
            info.Map = r.ReadString();
            info.GameDir = r.ReadString();
            info.GameDescription = r.ReadString();
            info.Players = r.ReadByte();
            info.MaxPlayers = r.ReadByte();
            if (r.Remaining >= 1)
                r.ReadByte(); // protocol
            if (r.Remaining >= 1)
                info.Dedicated = r.ReadByte() == (byte)'d';
            if (r.Remaining >= 1)
                info.Os = r.ReadByte();
            if (r.Remaining >= 1)
                r.ReadByte(); // password flag
            if (r.Remaining >= 1)
                r.ReadByte(); // is mod
            if (r.Remaining >= 1)
                info.Secure = r.ReadByte() != 0;
            if (r.Remaining >= 1)
                info.Bots = r.ReadByte();
            return info;
        }

        if (header == ProtocolConstants.S2A_INFO_DETAILED)
        {
            info.GoldSource = false;
            r.ReadByte();
            info.Name = r.ReadString();
            info.Map = r.ReadString();
            info.GameDir = r.ReadString();
            info.GameDescription = r.ReadString();
            r.ReadInt16();
            info.Players = r.ReadByte();
            info.MaxPlayers = r.ReadByte();
            info.Bots = r.ReadByte();
            info.Dedicated = r.ReadByte() == (byte)'d';
            info.Os = r.ReadByte();
            info.Secure = r.ReadByte() != 0;
            return info;
        }

        throw new InvalidOperationException($"Unknown INFO header 0x{header:X2}");
    }

    public void Dispose() => _udp.Dispose();
}

internal sealed class MessageReader
{
    private readonly byte[] _data;
    private int _pos;

    public MessageReader(byte[] data) => _data = data;
    public int Remaining => _data.Length - _pos;

    public int ReadInt32()
    {
        var v = BitConverter.ToInt32(_data, _pos);
        _pos += 4;
        return v;
    }

    public short ReadInt16()
    {
        var v = BitConverter.ToInt16(_data, _pos);
        _pos += 2;
        return v;
    }

    public byte ReadByte() => _data[_pos++];

    public float ReadFloat()
    {
        var v = BitConverter.ToSingle(_data, _pos);
        _pos += 4;
        return v;
    }

    public string ReadString()
    {
        if (_pos >= _data.Length)
            return string.Empty;
        var start = _pos;
        while (_pos < _data.Length && _data[_pos] != 0)
            _pos++;
        var s = Encoding.UTF8.GetString(_data, start, _pos - start);
        if (_pos < _data.Length)
            _pos++;
        return s;
    }

    public ReadOnlySpan<byte> RemainingSpan => _data.AsSpan(_pos);
}
