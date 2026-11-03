namespace Marquee.Playback.Demuxing;

/// <summary>H.264/HEVC byte streams with start codes (Annex B of both standards), as MPEG-TS carries them.</summary>
internal static class AnnexB
{
    private static readonly byte[] StartCode = [0, 0, 1];

    /// <summary>The NAL units between start codes, without the zero bytes that may pad before the next one.</summary>
    public static List<ReadOnlyMemory<byte>> Units(byte[] data)
    {
        var units = new List<ReadOnlyMemory<byte>>();
        int start = -1;
        int pos = 0;
        while (pos < data.Length)
        {
            int found = data.AsSpan(pos).IndexOf(StartCode);
            int end = found < 0 ? data.Length : pos + found;
            if (start >= 0)
            {
                int last = end;
                while (last > start && data[last - 1] == 0)
                {
                    last--;
                }

                if (last > start)
                {
                    units.Add(data.AsMemory(start, last - start));
                }
            }

            if (found < 0)
            {
                break;
            }

            start = pos = end + StartCode.Length;
        }

        return units;
    }

    public static int HevcType(ReadOnlySpan<byte> unit) => (unit[0] >> 1) & 0x3F;
}
