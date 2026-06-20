using System.Runtime.InteropServices;

namespace GoldSrcProbe.Protocol;

/// <summary>
/// Builds clc_move per ReHLDS SV_ParseMove / xash3d CL_WritePacket (GoldSrc).
/// </summary>
internal static class ClcMoveBuilder
{
    private static readonly byte[] NullCmd = new byte[Marshal.SizeOf<UserCmd>()];

    public static void Reset() { }

    /// <summary>One idle usercmd per packet (numbackup=0, newcmds=1).</summary>
    public static byte[] Build(uint outgoingSequence, byte msec = 16)
    {
        var cmd = UserCmd.Idle(msec);
        var cmdBytes = UserCmd.AsBytes(cmd).ToArray();

        var msg = new List<byte>(24) { ProtocolConstants.ClcMove, 0, 0 };
        var bodyStart = msg.Count;

        msg.Add(0); // packet loss
        msg.Add(0); // numbackup
        msg.Add(1); // newcmds — always one cmd per move packet

        var bitWriter = new BitWriter();
        UsercmdDeltaWriter.WriteDelta(bitWriter, NullCmd, cmdBytes);
        msg.AddRange(bitWriter.ToArray());

        var bodyLen = msg.Count - bodyStart;
        if (bodyLen > 255)
            bodyLen = 255;

        var bodySpan = CollectionsMarshal.AsSpan(msg).Slice(bodyStart, bodyLen);
        msg[1] = (byte)bodyLen;
        msg[2] = ComBlockSequenceCrc.Compute(bodySpan, bodyLen, (int)outgoingSequence);
        ComMunge.Munge(bodySpan, (int)outgoingSequence);

        return [.. msg];
    }
}
