namespace GoldSrcProbe.Protocol;

internal static class ProtocolConstants
{
    public const int ProtocolVersion = 48;
    public const int DefaultQueryPort = 27015;
    public const int ConnectedHeaderSize = 8;

    public const byte OobHeader0 = 0xFF;
    public const byte S2CChallenge = (byte)'A';
    public const byte S2CConnection = (byte)'B';
    public const byte S2CReject = (byte)'9';

    public const byte A2S_INFO = 0x54;
    public const byte A2S_PLAYER = 0x55;
    public const byte A2S_GETCHALLENGE = 0x57;
    public const byte S2A_INFO = 0x6D;
    public const byte S2A_INFO_DETAILED = 0x49;
    public const byte S2A_PLAYER = 0x44;
    public const byte S2C_CHALLENGE_BYTE = 0x41;

    // svc_* (ReHLDS / goldsrc-netclient)
    public const byte SvcBad = 0x00;
    public const byte SvcPrint = 0x08;
    public const byte SvcCenterPrint = 0x13;
    public const byte SvcStuffText = 0x09;
    public const byte SvcServerInfo = 0x0B;
    public const byte SvcSignonNum = 0x19;
    public const byte SvcDisconnect = 0x02;
    public const byte SvcNop = 0x01;
    public const byte SvcResourceList = 0x2B;
    public const byte SvcResourceRequest = 0x2D;
    public const byte SvcResourceLocation = 0x38;
    public const byte SvcNewMoveVars = 0x2C;
    public const byte SvcSendExtraInfo = 0x36;
    public const byte SvcDeltaDescription = 0x0E;
    public const byte SvcCustomization = 0x2E;
    public const byte SvcChoke = 0x2A;
    public const byte SvcSound = 0x06;
    public const byte SvcUserMessageStart = 0x40;

    public const byte ClcStringCmd = 0x03;
    public const byte ClcMove = 0x02;
    public const byte ClcFileConsistency = 0x07;
    public const byte ClcDelta = 0x04;

    public const byte SvcNewUserMsg = 0x1A;

    public const byte SvcPacketEntities = 0x28;
    public const byte SvcDeltaPacketEntities = 0x29;
    public const byte SvcClientData = 0x0F;
    public const byte SvcTime = 0x07;
    public const byte SvcSetView = 0x05;
    public const byte SvcLightStyle = 0x0C;
    public const byte SvcUpdateUserinfo = 0x0D;
    public const byte SvcSetAngle = 0x0A;
    public const byte SvcTempEntity = 0x17;
    public const byte SvcSpawnBaseline = 0x16;
}

internal static class BinaryPrimitivesExt
{
    public static uint Swap32(uint v) =>
        ((v & 0x000000FFu) << 24) |
        ((v & 0x0000FF00u) << 8) |
        ((v & 0x00FF0000u) >> 8) |
        ((v & 0xFF000000u) >> 24);
}
