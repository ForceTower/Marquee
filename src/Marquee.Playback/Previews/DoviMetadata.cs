using System.Runtime.InteropServices;

namespace Marquee.Playback.Previews;

// libavutil/dovi_meta.h (FFmpeg 9.0), which FFmpeg.AutoGen doesn't bind: the parsed RPU that
// libavcodec's HEVC decoder attaches to a frame as AV_FRAME_DATA_DOVI_METADATA. Only the parts a
// preview needs are declared; the layouts follow the C structs field for field. AVRationals are
// (num, den) int pairs.

[StructLayout(LayoutKind.Sequential)]
internal struct DoviMetadata
{
    public nuint HeaderOffset;
    public nuint MappingOffset;
    public nuint ColorOffset;
    public nuint ExtBlockOffset;
    public nuint ExtBlockSize;
    public int NumExtBlocks;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DoviRpuDataHeader
{
    public byte RpuType;
    public ushort RpuFormat;
    public byte VdrRpuProfile;
    public byte VdrRpuLevel;
    public byte ChromaResamplingExplicitFilterFlag;
    public byte CoefDataType;
    public byte CoefLog2Denom;
    public byte VdrRpuNormalizedIdc;
    public byte BlVideoFullRangeFlag;
    public byte BlBitDepth;
    public byte ElBitDepth;
    public byte VdrBitDepth;
    public byte SpatialResamplingFilterFlag;
    public byte ElSpatialResamplingFilterFlag;
    public byte DisableResidualFlag;
    public byte ExtMappingIdc04;
    public byte ExtMappingIdc57;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DoviReshapingCurve
{
    public const int MaxPieces = 8;
    public const int Polynomial = 0;
    public const int Mmr = 1;

    public byte NumPivots;
    public fixed ushort Pivots[MaxPieces + 1];
    public fixed int MappingIdc[MaxPieces];
    public fixed byte PolyOrder[MaxPieces];
    public fixed long PolyCoef[MaxPieces * 3];
    public fixed byte MmrOrder[MaxPieces];
    public fixed long MmrConstant[MaxPieces];
    public fixed long MmrCoef[MaxPieces * 3 * 7];
}

[StructLayout(LayoutKind.Sequential)]
internal struct DoviDataMapping
{
    public byte VdrRpuId;
    public byte MappingColorSpace;
    public byte MappingChromaFormatIdc;
    public DoviReshapingCurve Curve0;
    public DoviReshapingCurve Curve1;
    public DoviReshapingCurve Curve2;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DoviColorMetadata
{
    public byte DmMetadataId;
    public byte SceneRefreshFlag;
    public fixed int YccToRgbMatrix[9 * 2];
    public fixed int YccToRgbOffset[3 * 2];
    public fixed int RgbToLmsMatrix[9 * 2];
    public ushort SignalEotf;
    public ushort SignalEotfParam0;
    public ushort SignalEotfParam1;
    public uint SignalEotfParam2;
    public byte SignalBitDepth;
    public byte SignalColorSpace;
    public byte SignalChromaFormat;
    public byte SignalFullRangeFlag;
    public ushort SourceMinPq;
    public ushort SourceMaxPq;
    public ushort SourceDiagonal;
}
