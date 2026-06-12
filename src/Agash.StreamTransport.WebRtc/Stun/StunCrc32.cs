namespace Agash.StreamTransport.WebRtc.Stun;

/// <summary>
/// The CRC-32 (IEEE 802.3, reflected, polynomial <c>0xEDB88320</c>) used by the STUN FINGERPRINT
/// attribute (RFC 8489 §14.7). Implemented locally to keep the core package free of extra dependencies.
/// </summary>
internal static class StunCrc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFF_FFFFu;
        foreach (byte b in data)
        {
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        }

        return crc ^ 0xFFFF_FFFFu;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB8_8320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
