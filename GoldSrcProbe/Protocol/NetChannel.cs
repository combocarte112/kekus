namespace GoldSrcProbe.Protocol;

internal sealed class NetChannel
{
    private const int MaxStreams = 2;
    private const int MaxReliablePayload = 1200;
    private const int FragChunkSize = 1024;

    public uint OutSequence = 1;
    public uint InSequence;
    public uint InReliableSequence;
    public uint ReliableSequence;
    public int ConnectionState;
    public uint SpawnCount;
    public int WorldmapCrc;
    public byte PlayerNumber;
    public bool Connected;

    private readonly byte[][] _incomingFragParts = new byte[256][];
    private int _incomingFragCount;
    private int _incomingFragReceived;

    private int _pendingReliableSize = -1;
    private byte[]? _pendingReliableBody;
    private uint _lastReliableOutSequence;
    private uint _incomingAcknowledged;
    private uint _incomingReliableAcknowledged;

    private byte[]? _fragBody;
    private int _fragNextIndex;
    private int _fragTotal;
    private bool _fragReliableToggled;

    public event Action? OnServerFragmentComplete;

    public bool HasPendingReliable => _pendingReliableSize >= 0;

    public bool AwaitingFragments => _incomingFragCount > 0 && _incomingFragReceived < _incomingFragCount;

    public bool HasMoreOutboundFragments => _fragBody is not null && _fragNextIndex < _fragTotal;

    public void Reset()
    {
        OutSequence = 1;
        InSequence = 0;
        InReliableSequence = 0;
        ReliableSequence = 0;
        ConnectionState = 0;
        SpawnCount = 0;
        WorldmapCrc = 0;
        PlayerNumber = 0;
        Connected = false;
        Array.Clear(_incomingFragParts, 0, _incomingFragParts.Length);
        _incomingFragCount = 0;
        _incomingFragReceived = 0;
        _incomingAcknowledged = 0;
        _incomingReliableAcknowledged = 0;
        ClearPendingReliable();
        _fragBody = null;
        _fragNextIndex = 0;
        _fragTotal = 0;
        _fragReliableToggled = false;
    }

    public byte[]? ProcessIncoming(byte[] data, Action<byte[]> onPayload)
    {
        if (data.Length < 8)
            return null;

        var w1 = BitConverter.ToUInt32(data, 0);
        var w2 = BitConverter.ToUInt32(data, 4);
        var hasReliable = (w1 & 0x80000000) != 0;
        var isFragment = (w1 & 0x40000000) != 0;
        var seqIn = w1 & 0x3FFFFFFF;
        var seqAck = w2 & 0x3FFFFFFF;
        var reliableAck = w2 >> 31;

        if (GoldSrcClient.DebugNet)
            Console.WriteLine(
                $"  [net] in seq={seqIn} rel={hasReliable} frag={isFragment} len={data.Length} inSeq={InSequence} ack={seqAck} ackRel={reliableAck} pendRel={ReliableSequence}");

        if (reliableAck == ReliableSequence && _pendingReliableSize >= 0 && seqAck >= _lastReliableOutSequence &&
            (_fragBody is null || _fragNextIndex >= _fragTotal))
            ClearPendingReliable();

        _incomingAcknowledged = seqAck;
        _incomingReliableAcknowledged = reliableAck;

        if (!isFragment && seqIn <= InSequence)
        {
            if (GoldSrcClient.DebugNet)
                Console.WriteLine($"  [net] drop stale/duplicate seq={seqIn} have={InSequence}");
            return BuildAckOnly();
        }

        InSequence = seqIn;
        if (hasReliable)
            InReliableSequence ^= 1;

        if (data.Length <= 8)
            return BuildAckOnly();

        var payload = data[8..].ToArray();
        ComMunge.UnMunge2(payload, (int)(seqIn & 0xFF));

        if (isFragment)
        {
            ProcessFragmentPayload(payload, onPayload);
            return BuildAckOnly();
        }

        onPayload(payload);
        return BuildAckOnly();
    }

    private void ProcessFragmentPayload(byte[] payload, Action<byte[]> onPayload)
    {
        if (GoldSrcClient.DebugNet)
            Console.WriteLine($"  [net] frag payload {payload.Length}b");

        var p = 0;
        var stream0Exists = false;
        uint fragId = 0;
        var fragOffset = 0;
        var fragLength = 0;

        for (var stream = 0; stream < MaxStreams; stream++)
        {
            if (p >= payload.Length)
                break;
            if (payload[p++] == 0)
                continue;
            if (p + 8 > payload.Length)
                break;

            var id = BitConverter.ToUInt32(payload, p);
            p += 4;
            var offset = BitConverter.ToInt16(payload, p);
            p += 2;
            var length = BitConverter.ToInt16(payload, p);
            p += 2;

            if (stream == 0)
            {
                stream0Exists = true;
                fragId = id;
                fragOffset = offset;
                fragLength = length;
            }
        }

        if (!stream0Exists)
        {
            onPayload(payload);
            return;
        }

        var dataStart = p;
        if (fragLength <= 0)
            fragLength = payload.Length - dataStart - fragOffset;
        if (fragLength <= 0)
            return;

        var src = dataStart + fragOffset;
        if (src + fragLength > payload.Length)
            fragLength = payload.Length - src;
        if (fragLength <= 0)
            return;

        var fragIndex = (int)((fragId >> 16) & 0xFFFF);
        var fragCount = (int)(fragId & 0xFFFF);
        if (fragCount <= 0 || fragIndex <= 0 || fragIndex > fragCount)
        {
            fragCount = 1;
            fragIndex = 1;
        }

        if (fragIndex == 1 && (_incomingFragCount != fragCount || _incomingFragReceived == 0))
        {
            Array.Clear(_incomingFragParts, 0, _incomingFragParts.Length);
            _incomingFragCount = fragCount;
            _incomingFragReceived = 0;
        }

        if (fragIndex - 1 < _incomingFragParts.Length && _incomingFragParts[fragIndex - 1] is null)
        {
            _incomingFragParts[fragIndex - 1] = payload.AsSpan(src, fragLength).ToArray();
            _incomingFragReceived++;
        }

        if (GoldSrcClient.DebugNet)
            Console.WriteLine($"  [net] frag {fragIndex}/{fragCount} off={fragOffset} len={fragLength} got={_incomingFragReceived}");

        if (_incomingFragReceived < _incomingFragCount)
            return;

        var totalLen = 0;
        for (var i = 0; i < _incomingFragCount; i++)
            totalLen += _incomingFragParts[i]?.Length ?? 0;

        var assembled = new byte[totalLen];
        var pos = 0;
        for (var i = 0; i < _incomingFragCount; i++)
        {
            var part = _incomingFragParts[i];
            if (part is null)
                return;
            Buffer.BlockCopy(part, 0, assembled, pos, part.Length);
            pos += part.Length;
        }

        if (GoldSrcClient.DebugNet)
            Console.WriteLine($"  [net] frag complete {assembled.Length}b op=0x{(assembled.Length > 0 ? assembled[0] : 0):X2}");

        assembled = SignonPayload.MaybeDecompress(assembled);
        onPayload(assembled);
        OnServerFragmentComplete?.Invoke();

        Array.Clear(_incomingFragParts, 0, _incomingFragParts.Length);
        _incomingFragCount = 0;
        _incomingFragReceived = 0;
    }

    public byte[] BuildAckOnly()
    {
        const int minPacketSize = 16;
        var packet = new byte[minPacketSize];
        BitConverter.TryWriteBytes(packet.AsSpan(0, 4), OutSequence);
        BitConverter.TryWriteBytes(packet.AsSpan(4, 4), InSequence | (InReliableSequence << 31));
        for (var i = ProtocolConstants.ConnectedHeaderSize; i < minPacketSize; i++)
            packet[i] = ProtocolConstants.SvcNop;
        ComMunge.Munge2(packet.AsSpan(ProtocolConstants.ConnectedHeaderSize), (int)(OutSequence & 0xFF));
        OutSequence++;
        return packet;
    }

    public byte[]? TryEmitNextReliable(Queue<byte[]> outbox)
    {
        // Outbound fragments must be sent back-to-back; pending ACK must not block the next chunk.
        if (_fragBody is not null && _fragNextIndex < _fragTotal)
            return EmitFragmentPacket();

        if (HasPendingReliable)
            return null;

        _fragBody = null;
        _fragNextIndex = 0;
        _fragTotal = 0;
        _fragReliableToggled = false;

        if (outbox.Count == 0)
            return null;

        var body = outbox.Dequeue();
        if (body.Length <= MaxReliablePayload)
            return BuildReliable(body);

        _fragBody = body;
        _fragTotal = body.Length / FragChunkSize + (body.Length % FragChunkSize != 0 ? 1 : 0);
        _fragNextIndex = 0;
        return EmitFragmentPacket();
    }

    /// <summary>No reliable retry — signon ackRel alternates; re-sending spawn causes overflow on strict ReHLDS.</summary>
    public byte[]? RetryPendingReliable() => null;

    public byte[] BuildReliable(ReadOnlySpan<byte> message)
    {
        _pendingReliableBody = message.ToArray();
        ReliableSequence ^= 1;
        _pendingReliableSize = message.Length;
        var packet = EmitReliablePacket(_pendingReliableBody, fragment: false);
        RememberSentReliable(packet);
        return packet;
    }

    private byte[] EmitFragmentPacket()
    {
        if (_fragBody is null || _fragNextIndex >= _fragTotal)
            throw new InvalidOperationException("No fragmented reliable body queued");

        if (!_fragReliableToggled)
        {
            ReliableSequence ^= 1;
            _fragReliableToggled = true;
        }

        _pendingReliableSize = _fragBody.Length;
        _pendingReliableBody = _fragBody;

        var fragIndex = _fragNextIndex + 1;
        var bodyOffset = _fragNextIndex * FragChunkSize;
        var chunkSize = Math.Min(FragChunkSize, _fragBody.Length - bodyOffset);
        var chunk = _fragBody.AsSpan(bodyOffset, chunkSize);

        const int headerSize = 1 + 8 + 1;
        var packet = new byte[ProtocolConstants.ConnectedHeaderSize + headerSize + chunkSize];
        var w1 = OutSequence | (1u << 31) | (1u << 30);
        var w2 = InSequence | (InReliableSequence << 31);
        BitConverter.TryWriteBytes(packet.AsSpan(0, 4), w1);
        BitConverter.TryWriteBytes(packet.AsSpan(4, 4), w2);

        var o = 8;
        packet[o++] = 1;
        var fragId = ((uint)fragIndex << 16) | (uint)_fragTotal;
        BitConverter.TryWriteBytes(packet.AsSpan(o, 4), fragId);
        o += 4;
        BitConverter.TryWriteBytes(packet.AsSpan(o, 2), (short)0);
        o += 2;
        BitConverter.TryWriteBytes(packet.AsSpan(o, 2), (short)chunkSize);
        o += 2;
        packet[o++] = 0;
        chunk.CopyTo(packet.AsSpan(o));

        ComMunge.Munge2(packet.AsSpan(8, headerSize + chunkSize), (int)(OutSequence & 0xFF));
        OutSequence++;
        _fragNextIndex = fragIndex;
        RememberSentReliable(packet);

        if (GoldSrcClient.DebugNet)
            Console.WriteLine($"  [net] frag out {fragIndex}/{_fragTotal} chunk={chunkSize}b relSeq={ReliableSequence}");

        if (_fragNextIndex >= _fragTotal)
        {
            _fragBody = null;
            _fragNextIndex = 0;
            _fragTotal = 0;
            _fragReliableToggled = false;
        }

        return packet;
    }

    private byte[] EmitReliablePacket(ReadOnlySpan<byte> message, bool fragment)
    {
        var packet = new byte[ProtocolConstants.ConnectedHeaderSize + message.Length];
        var w1 = OutSequence | (1u << 31);
        if (fragment)
            w1 |= 1u << 30;
        var w2 = InSequence | (InReliableSequence << 31);
        BitConverter.TryWriteBytes(packet.AsSpan(0, 4), w1);
        BitConverter.TryWriteBytes(packet.AsSpan(4, 4), w2);
        message.CopyTo(packet.AsSpan(8));
        ComMunge.Munge2(packet.AsSpan(8), (int)(OutSequence & 0xFF));
        OutSequence++;
        return packet;
    }

    private void RememberSentReliable(byte[] packet)
    {
        _lastReliableOutSequence = OutSequence - 1;
    }

    public void ClearPendingReliable()
    {
        _pendingReliableSize = -1;
        _pendingReliableBody = null;
        _fragBody = null;
        _fragNextIndex = 0;
        _fragTotal = 0;
        _fragReliableToggled = false;
    }
}
