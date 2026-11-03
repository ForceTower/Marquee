using FFmpeg.AutoGen;
using Marquee.Playback.Demuxing;

namespace Marquee.Playback.Previews;

/// <summary>
/// Turns a decoded frame into a small SDR picture (BGRA). Each output pixel averages a 4×4 grid of
/// samples from its area of the frame, then converts from Y'CbCr. SDR goes straight to the screen.
/// HDR10 and HLG are tone-mapped into BT.709 with BT.2390's roll-off, against the frame's own
/// brightest highlights as mpv's peak detection would measure them. Dolby Vision profile 5, whose
/// base layer is IPTPQc2 rather than Y'CbCr, is first reshaped through the frame's RPU, the way
/// libplacebo applies it.
/// </summary>
internal static unsafe class PreviewPicture
{
    /// <summary>
    /// The HDR brightness that lands on SDR white. 100 nits, as Jellyfin's trickplay maps it, keeps
    /// faces about as bright as an SDR grade has them; BT.2408's 203 makes previews look dim.
    /// </summary>
    private const double SdrWhiteNits = 100;

    private const int Grid = 4;
    private const int LutSize = 4096;

    private static readonly float[] PqLut = BuildLut(e => (float)PqToNits(e));
    private static readonly float[] SrgbLut = BuildLut(l => (float)(l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055));

    // Hunt-Pointer-Estevez LMS to BT.2020 RGB, as libplacebo hard-codes it for Dolby Vision.
    private static readonly double[] HpeLmsToRgb =
    [
        3.06441879, -2.16597676, 0.10155818,
        -0.65612108, 1.78554118, -0.12943749,
        0.01736321, -0.04725154, 1.03004253,
    ];

    private static readonly double[] Bt2020ToBt709 =
    [
        1.660491, -0.587641, -0.072850,
        -0.124550, 1.132900, -0.008349,
        -0.018151, -0.100579, 1.118730,
    ];

    /// <summary>What the frame turned out to be, for logs ("PQ", "HLG", "Dolby Vision 5 reshaped").</summary>
    public readonly record struct Result(byte[] Pixels, int Width, int Height, string Kind);

    /// <param name="reshapeDolbyVision">The base layer isn't Y'CbCr (profile 5): reshape it through the RPU.</param>
    public static Result? ToBgra(AVFrame* frame, int width, ColourInfo? trackColour, bool reshapeDolbyVision)
    {
        AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)frame->format);
        if (desc == null || desc->nb_components < 3 || (desc->flags & (ffmpeg.AV_PIX_FMT_FLAG_RGB | ffmpeg.AV_PIX_FMT_FLAG_BE)) != 0 ||
            (desc->flags & ffmpeg.AV_PIX_FMT_FLAG_PLANAR) == 0 || desc->comp[0].plane != 0 || desc->comp[1].plane != 1 ||
            desc->comp[2].plane != 2 || desc->comp[0].depth > 16 || desc->comp[0].shift != 0)
        {
            return null;
        }

        int w = frame->width, h = frame->height;
        if (w <= 0 || h <= 0 || width <= 0)
        {
            return null;
        }

        double sar = frame->sample_aspect_ratio is { num: > 0, den: > 0 } aspect ? (double)aspect.num / aspect.den : 1;
        int height = Math.Max(1, (int)Math.Round(width * h / (w * sar)));
        int depth = desc->comp[0].depth;
        var planes = new Planes(frame, desc->log2_chroma_w, desc->log2_chroma_h, wide: depth > 8);
        float[] ycc = Average(planes, w, h, width, height, 1.0 / ((1 << depth) - 1));

        int transfer = Known((int)frame->color_trc, (int)AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED) ?? trackColour?.Transfer ?? 1;
        int matrix = Known((int)frame->colorspace, (int)AVColorSpace.AVCOL_SPC_UNSPECIFIED) ?? trackColour?.Matrix ?? (h >= 720 ? 1 : 6);
        int? range = Known((int)frame->color_range, (int)AVColorRange.AVCOL_RANGE_UNSPECIFIED) ?? trackColour?.Range;
        bool full = range == 2 || range is null && (AVPixelFormat)frame->format is AVPixelFormat.AV_PIX_FMT_YUVJ420P
            or AVPixelFormat.AV_PIX_FMT_YUVJ422P or AVPixelFormat.AV_PIX_FMT_YUVJ444P;

        var pixels = new byte[width * height * 4];
        if (reshapeDolbyVision && DoviTransform.From(frame) is { } dovi)
        {
            float[] linear = new float[ycc.Length];
            for (int i = 0; i < ycc.Length; i += 3)
            {
                dovi.Apply(ycc[i], ycc[i + 1], ycc[i + 2], out linear[i], out linear[i + 1], out linear[i + 2]);
            }

            ToneMap(linear, pixels);
            return new Result(pixels, width, height, "Dolby Vision 5 reshaped");
        }

        var (kr, kb) = matrix switch
        {
            5 or 6 => (0.299, 0.114),
            9 or 10 => (0.2627, 0.0593),
            4 => (0.30, 0.11),
            7 => (0.212, 0.087),
            _ => (0.2126, 0.0722),
        };
        ToRgb(ycc, kr, kb, full, depth);

        if (transfer is 16 or 18)
        {
            for (int i = 0; i < ycc.Length; i += 3)
            {
                if (transfer == 16)
                {
                    ycc[i] = Lut(PqLut, ycc[i]);
                    ycc[i + 1] = Lut(PqLut, ycc[i + 1]);
                    ycc[i + 2] = Lut(PqLut, ycc[i + 2]);
                }
                else
                {
                    HlgToNits(ref ycc[i], ref ycc[i + 1], ref ycc[i + 2]);
                }
            }

            ToneMap(ycc, pixels);
            return new Result(pixels, width, height, transfer == 16 ? "PQ" : "HLG");
        }

        for (int i = 0, o = 0; i < ycc.Length; i += 3, o += 4)
        {
            pixels[o] = Byte(ycc[i + 2]);
            pixels[o + 1] = Byte(ycc[i + 1]);
            pixels[o + 2] = Byte(ycc[i]);
            pixels[o + 3] = 255;
        }

        return new Result(pixels, width, height, "SDR");
    }

    private static int? Known(int code, int unspecified) => code == unspecified || code <= 0 ? null : code;

    private readonly struct Planes(AVFrame* frame, int chromaShiftX, int chromaShiftY, bool wide)
    {
        public readonly byte* Y = frame->data[0];
        public readonly byte* Cb = frame->data[1];
        public readonly byte* Cr = frame->data[2];
        public readonly int YStride = frame->linesize[0];
        public readonly int CbStride = frame->linesize[1];
        public readonly int CrStride = frame->linesize[2];
        public readonly int ShiftX = chromaShiftX;
        public readonly int ShiftY = chromaShiftY;
        public readonly bool Wide = wide;
    }

    /// <summary>Each output pixel's mean code values (Y, Cb, Cr), normalized to 0–1, over a grid inside its area.</summary>
    private static float[] Average(Planes p, int w, int h, int outW, int outH, double normalize)
    {
        int[] xs = Positions(w, outW);
        int[] ys = Positions(h, outH);
        var ycc = new float[outW * outH * 3];
        double scale = normalize / (Grid * Grid);
        for (int oy = 0, i = 0; oy < outH; oy++)
        {
            for (int ox = 0; ox < outW; ox++, i += 3)
            {
                long sy = 0, scb = 0, scr = 0;
                for (int gy = 0; gy < Grid; gy++)
                {
                    int y = ys[oy * Grid + gy];
                    int cy = y >> p.ShiftY;
                    byte* yRow = p.Y + (long)y * p.YStride;
                    byte* cbRow = p.Cb + (long)cy * p.CbStride;
                    byte* crRow = p.Cr + (long)cy * p.CrStride;
                    for (int gx = 0; gx < Grid; gx++)
                    {
                        int x = xs[ox * Grid + gx];
                        int cx = x >> p.ShiftX;
                        if (p.Wide)
                        {
                            sy += ((ushort*)yRow)[x];
                            scb += ((ushort*)cbRow)[cx];
                            scr += ((ushort*)crRow)[cx];
                        }
                        else
                        {
                            sy += yRow[x];
                            scb += cbRow[cx];
                            scr += crRow[cx];
                        }
                    }
                }

                ycc[i] = (float)(sy * scale);
                ycc[i + 1] = (float)(scb * scale);
                ycc[i + 2] = (float)(scr * scale);
            }
        }

        return ycc;
    }

    private static int[] Positions(int size, int outSize)
    {
        var positions = new int[outSize * Grid];
        for (int o = 0; o < outSize; o++)
        {
            for (int k = 0; k < Grid; k++)
            {
                positions[o * Grid + k] = Math.Clamp((int)((o + (k + 0.5) / Grid) * size / outSize), 0, size - 1);
            }
        }

        return positions;
    }

    /// <summary>Normalized code values to non-linear R'G'B', in place.</summary>
    private static void ToRgb(float[] ycc, double kr, double kb, bool full, int depth)
    {
        double kg = 1 - kr - kb;
        double max = (1 << depth) - 1;
        double k = (1 << depth) / 256.0;
        double yOffset = full ? 0 : 16 * k / max;
        double yScale = full ? 1 : max / (219 * k);
        double cOffset = (1 << (depth - 1)) / max;
        double cScale = full ? 1 : max / (224 * k);
        for (int i = 0; i < ycc.Length; i += 3)
        {
            double y = (ycc[i] - yOffset) * yScale;
            double cb = (ycc[i + 1] - cOffset) * cScale;
            double cr = (ycc[i + 2] - cOffset) * cScale;
            double r = y + 2 * (1 - kr) * cr;
            double b = y + 2 * (1 - kb) * cb;
            double g = (y - kr * r - kb * b) / kg;
            ycc[i] = (float)Math.Clamp(r, 0, 1);
            ycc[i + 1] = (float)Math.Clamp(g, 0, 1);
            ycc[i + 2] = (float)Math.Clamp(b, 0, 1);
        }
    }

    /// <summary>HLG signal to display light in nits, for a 1000-nit display (BT.2100's reference OOTF).</summary>
    private static void HlgToNits(ref float r, ref float g, ref float b)
    {
        static double Inverse(double e)
        {
            const double A = 0.17883277, B = 0.28466892, C = 0.55991073;
            return e <= 0.5 ? e * e / 3 : (Math.Exp((e - C) / A) + B) / 12;
        }

        double er = Inverse(r), eg = Inverse(g), eb = Inverse(b);
        double ys = 0.2627 * er + 0.6780 * eg + 0.0593 * eb;
        double gain = ys > 0 ? 1000 * Math.Pow(ys, 0.2) : 0;
        r = (float)(gain * er);
        g = (float)(gain * eg);
        b = (float)(gain * eb);
    }

    /// <summary>
    /// Linear BT.2020 light in nits to SDR BT.709 BGRA. The peak is the frame's 99.9th percentile
    /// of max(R, G, B), so a dark scene isn't compressed for highlights it doesn't have.
    /// </summary>
    private static void ToneMap(float[] rgb, byte[] pixels)
    {
        double peak = Math.Clamp(Percentile(rgb, 0.999), SdrWhiteNits, 10000);
        double pqPeak = NitsToPq(peak);
        double maxLum = NitsToPq(SdrWhiteNits) / pqPeak;
        double knee = Math.Max(0, 1.5 * maxLum - 0.5);
        double kneeNits = peak <= SdrWhiteNits ? double.MaxValue : PqToNits(knee * pqPeak);

        double Bt2390(double nits)
        {
            double e = NitsToPq(nits) / pqPeak;
            if (e >= 1)
            {
                return SdrWhiteNits;
            }

            double t = (e - knee) / (1 - knee);
            double t2 = t * t, t3 = t2 * t;
            double p = (2 * t3 - 3 * t2 + 1) * knee + (t3 - 2 * t2 + t) * (1 - knee) + (-2 * t3 + 3 * t2) * maxLum;
            return PqToNits(p * pqPeak);
        }

        var m = Bt2020ToBt709;
        for (int i = 0, o = 0; i < rgb.Length; i += 3, o += 4)
        {
            double r = Math.Max(0, rgb[i]), g = Math.Max(0, rgb[i + 1]), b = Math.Max(0, rgb[i + 2]);
            double max = Math.Max(r, Math.Max(g, b));
            double scale = 1 / SdrWhiteNits;
            if (max > kneeNits)
            {
                scale = Bt2390(max) / max / SdrWhiteNits;
            }

            r *= scale;
            g *= scale;
            b *= scale;
            pixels[o + 2] = Byte(Lut(SrgbLut, m[0] * r + m[1] * g + m[2] * b));
            pixels[o + 1] = Byte(Lut(SrgbLut, m[3] * r + m[4] * g + m[5] * b));
            pixels[o] = Byte(Lut(SrgbLut, m[6] * r + m[7] * g + m[8] * b));
            pixels[o + 3] = 255;
        }
    }

    private static double Percentile(float[] rgb, double fraction)
    {
        const int Bins = 2048;
        var histogram = new int[Bins];
        int count = rgb.Length / 3;
        for (int i = 0; i < rgb.Length; i += 3)
        {
            double max = Math.Max(rgb[i], Math.Max(rgb[i + 1], rgb[i + 2]));
            histogram[Math.Clamp((int)(NitsToPq(max) * (Bins - 1)), 0, Bins - 1)]++;
        }

        int target = (int)(count * fraction), seen = 0;
        for (int bin = 0; bin < Bins; bin++)
        {
            seen += histogram[bin];
            if (seen > target)
            {
                return PqToNits((bin + 1.0) / (Bins - 1));
            }
        }

        return 10000;
    }

    private const double M1 = 2610.0 / 16384, M2 = 2523.0 / 4096 * 128;
    private const double C1 = 3424.0 / 4096, C2 = 2413.0 / 4096 * 32, C3 = 2392.0 / 4096 * 32;

    private static double PqToNits(double e)
    {
        double p = Math.Pow(Math.Clamp(e, 0, 1), 1 / M2);
        return 10000 * Math.Pow(Math.Max(p - C1, 0) / (C2 - C3 * p), 1 / M1);
    }

    private static double NitsToPq(double nits)
    {
        double y = Math.Pow(Math.Clamp(nits / 10000, 0, 1), M1);
        return Math.Pow((C1 + C2 * y) / (1 + C3 * y), M2);
    }

    private static float[] BuildLut(Func<double, float> f)
    {
        var lut = new float[LutSize + 1];
        for (int i = 0; i <= LutSize; i++)
        {
            lut[i] = f((double)i / LutSize);
        }

        return lut;
    }

    private static float Lut(float[] lut, double x)
    {
        if (x <= 0)
        {
            return lut[0];
        }

        if (x >= 1)
        {
            return lut[LutSize];
        }

        double position = x * LutSize;
        int i = (int)position;
        double t = position - i;
        return (float)(lut[i] + (lut[i + 1] - lut[i]) * t);
    }

    private static byte Byte(double v) => (byte)Math.Clamp((int)(v * 255 + 0.5), 0, 255);

    /// <summary>
    /// A frame's Dolby Vision RPU as libplacebo applies it: each component reshaped through its
    /// pivoted polynomial or MMR curve, then the RPU's matrix to non-linear LMS, PQ to linear, and
    /// its LMS matrix followed by the fixed Hunt-Pointer-Estevez LMS→BT.2020 RGB.
    /// </summary>
    private sealed class DoviTransform
    {
        private readonly Curve[] _curves = new Curve[3];
        private readonly double[] _nonlinear = new double[9];
        private readonly double[] _offset = new double[3];
        private readonly double[] _linear = new double[9];

        private sealed class Curve
        {
            public double[] Pivots = [];
            public int[] Method = [];
            public double[][] Poly = [];
            public int[] MmrOrder = [];
            public double[] MmrConstant = [];
            public double[][] Mmr = [];
        }

        public static DoviTransform? From(AVFrame* frame)
        {
            AVFrameSideData* data = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DOVI_METADATA);
            if (data == null)
            {
                return null;
            }

            var metadata = (DoviMetadata*)data->data;
            var header = (DoviRpuDataHeader*)((byte*)metadata + metadata->HeaderOffset);
            var mapping = (DoviDataMapping*)((byte*)metadata + metadata->MappingOffset);
            var color = (DoviColorMetadata*)((byte*)metadata + metadata->ColorOffset);
            if (header->BlBitDepth is < 8 or > 16)
            {
                return null;
            }

            var transform = new DoviTransform();
            double pivotScale = 1.0 / ((1 << header->BlBitDepth) - 1);
            double coefScale = 1.0 / (1L << header->CoefLog2Denom);
            DoviReshapingCurve* curves = &mapping->Curve0;
            for (int c = 0; c < 3; c++)
            {
                DoviReshapingCurve* source = curves + c;
                int pivots = Math.Clamp((int)source->NumPivots, 2, DoviReshapingCurve.MaxPieces + 1);
                var curve = transform._curves[c] = new Curve
                {
                    Pivots = new double[pivots],
                    Method = new int[pivots - 1],
                    Poly = new double[pivots - 1][],
                    MmrOrder = new int[pivots - 1],
                    MmrConstant = new double[pivots - 1],
                    Mmr = new double[pivots - 1][],
                };
                for (int i = 0; i < pivots; i++)
                {
                    curve.Pivots[i] = source->Pivots[i] * pivotScale;
                }

                for (int i = 0; i < pivots - 1; i++)
                {
                    curve.Method[i] = source->MappingIdc[i];
                    curve.Poly[i] = new double[3];
                    for (int k = 0; k < 3 && k <= source->PolyOrder[i]; k++)
                    {
                        curve.Poly[i][k] = source->PolyCoef[i * 3 + k] * coefScale;
                    }

                    curve.MmrOrder[i] = Math.Clamp((int)source->MmrOrder[i], 0, 3);
                    curve.MmrConstant[i] = source->MmrConstant[i] * coefScale;
                    curve.Mmr[i] = new double[21];
                    for (int k = 0; k < 21; k++)
                    {
                        curve.Mmr[i][k] = source->MmrCoef[i * 21 + k] * coefScale;
                    }
                }
            }

            for (int i = 0; i < 9; i++)
            {
                transform._nonlinear[i] = Rational(color->YccToRgbMatrix, i);
            }

            for (int i = 0; i < 3; i++)
            {
                transform._offset[i] = Rational(color->YccToRgbOffset, i);
            }

            var rgbToLms = new double[9];
            for (int i = 0; i < 9; i++)
            {
                rgbToLms[i] = Rational(color->RgbToLmsMatrix, i);
            }

            Multiply(HpeLmsToRgb, rgbToLms, transform._linear);
            return transform;
        }

        private static double Rational(int* pairs, int index) =>
            pairs[index * 2 + 1] == 0 ? 0 : (double)pairs[index * 2] / pairs[index * 2 + 1];

        private static void Multiply(double[] a, double[] b, double[] result)
        {
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    result[row * 3 + col] = a[row * 3] * b[col] + a[row * 3 + 1] * b[3 + col] + a[row * 3 + 2] * b[6 + col];
                }
            }
        }

        /// <summary>Normalized base-layer code values to linear BT.2020 light in nits.</summary>
        public void Apply(double y, double cb, double cr, out float r, out float g, out float b)
        {
            double i0 = Reshape(0, y, cb, cr) - _offset[0];
            double i1 = Reshape(1, y, cb, cr) - _offset[1];
            double i2 = Reshape(2, y, cb, cr) - _offset[2];
            var n = _nonlinear;
            double l = Lut(PqLut, n[0] * i0 + n[1] * i1 + n[2] * i2);
            double m = Lut(PqLut, n[3] * i0 + n[4] * i1 + n[5] * i2);
            double s = Lut(PqLut, n[6] * i0 + n[7] * i1 + n[8] * i2);
            var t = _linear;
            r = (float)(t[0] * l + t[1] * m + t[2] * s);
            g = (float)(t[3] * l + t[4] * m + t[5] * s);
            b = (float)(t[6] * l + t[7] * m + t[8] * s);
        }

        private double Reshape(int component, double y, double cb, double cr)
        {
            var curve = _curves[component];
            double[] pivots = curve.Pivots;
            double s = component == 0 ? y : component == 1 ? cb : cr;
            s = Math.Clamp(s, pivots[0], pivots[^1]);
            int piece = 0;
            for (int i = 1; i < pivots.Length - 1; i++)
            {
                if (s >= pivots[i])
                {
                    piece = i;
                }
            }

            double v;
            if (curve.Method[piece] == DoviReshapingCurve.Polynomial)
            {
                double[] c = curve.Poly[piece];
                v = c[0] + (c[1] + c[2] * s) * s;
            }
            else
            {
                double[] c = curve.Mmr[piece];
                v = curve.MmrConstant[piece];
                double x0 = y, x1 = cb, x2 = cr;
                double x3 = y * cb, x4 = y * cr, x5 = cb * cr, x6 = y * cb * cr;
                double p0 = x0, p1 = x1, p2 = x2, p3 = x3, p4 = x4, p5 = x5, p6 = x6;
                for (int order = 0; order < curve.MmrOrder[piece]; order++)
                {
                    int k = order * 7;
                    v += c[k] * p0 + c[k + 1] * p1 + c[k + 2] * p2 + c[k + 3] * p3 + c[k + 4] * p4 + c[k + 5] * p5 + c[k + 6] * p6;
                    p0 *= x0;
                    p1 *= x1;
                    p2 *= x2;
                    p3 *= x3;
                    p4 *= x4;
                    p5 *= x5;
                    p6 *= x6;
                }
            }

            return Math.Clamp(v, 0, 1);
        }
    }
}
