using System.Runtime.CompilerServices;

namespace USMHandler.Codecs
{
    /// <summary>
    /// Integer 8x8 inverse DCT (IEEE 1180 compliant "simple" row/column transform).
    /// Both entry points clear the coefficient block afterwards.
    /// </summary>
    internal static unsafe class Idct
    {
        private const int W1 = 22725;
        private const int W2 = 21407;
        private const int W3 = 19266;
        private const int W4 = 16383;
        private const int W5 = 12873;
        private const int W6 = 8867;
        private const int W7 = 4520;
        private const int RowShift = 11;
        private const int ColShift = 20;

        public static void Put(short* block, byte* dst, int stride, bool acPresent)
        {
            if (!acPresent)
            {
                byte v = Clip(DcOnly(block[0]));
                for (int y = 0; y < 8; y++, dst += stride)
                    Unsafe.InitBlockUnaligned(dst, v, 8);
                block[0] = 0;
                return;
            }

            for (int i = 0; i < 8; i++)
                Row(block + i * 8);
            for (int i = 0; i < 8; i++)
                Column(block + i, dst + i, stride, false);
            Unsafe.InitBlockUnaligned(block, 0, 128);
        }

        public static void Add(short* block, byte* dst, int stride, bool acPresent)
        {
            if (!acPresent)
            {
                int v = DcOnly(block[0]);
                block[0] = 0;
                if (v == 0)
                    return;
                for (int y = 0; y < 8; y++, dst += stride)
                    for (int x = 0; x < 8; x++)
                        dst[x] = Clip(dst[x] + v);
                return;
            }

            for (int i = 0; i < 8; i++)
                Row(block + i * 8);
            for (int i = 0; i < 8; i++)
                Column(block + i, dst + i, stride, true);
            Unsafe.InitBlockUnaligned(block, 0, 128);
        }

        // Result of a full transform when only the DC coefficient is set.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int DcOnly(int dc) => (W4 * ((short)(dc << 3) + 32)) >> ColShift;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte Clip(int v) => (uint)v > 255 ? (byte)(v < 0 ? 0 : 255) : (byte)v;

        private static void Row(short* row)
        {
            if ((row[1] | row[2] | row[3] | row[4] | row[5] | row[6] | row[7]) == 0)
            {
                short dc = (short)(row[0] << 3);
                for (int i = 0; i < 8; i++)
                    row[i] = dc;
                return;
            }

            int a0 = W4 * row[0] + (1 << (RowShift - 1));
            int a1 = a0;
            int a2 = a0;
            int a3 = a0;

            a0 += W2 * row[2];
            a1 += W6 * row[2];
            a2 -= W6 * row[2];
            a3 -= W2 * row[2];

            int b0 = W1 * row[1] + W3 * row[3];
            int b1 = W3 * row[1] - W7 * row[3];
            int b2 = W5 * row[1] - W1 * row[3];
            int b3 = W7 * row[1] - W5 * row[3];

            if ((row[4] | row[5] | row[6] | row[7]) != 0)
            {
                a0 += W4 * row[4] + W6 * row[6];
                a1 += -W4 * row[4] - W2 * row[6];
                a2 += -W4 * row[4] + W2 * row[6];
                a3 += W4 * row[4] - W6 * row[6];

                b0 += W5 * row[5] + W7 * row[7];
                b1 += -W1 * row[5] - W5 * row[7];
                b2 += W7 * row[5] + W3 * row[7];
                b3 += W3 * row[5] - W1 * row[7];
            }

            row[0] = (short)((a0 + b0) >> RowShift);
            row[7] = (short)((a0 - b0) >> RowShift);
            row[1] = (short)((a1 + b1) >> RowShift);
            row[6] = (short)((a1 - b1) >> RowShift);
            row[2] = (short)((a2 + b2) >> RowShift);
            row[5] = (short)((a2 - b2) >> RowShift);
            row[3] = (short)((a3 + b3) >> RowShift);
            row[4] = (short)((a3 - b3) >> RowShift);
        }

        private static void Column(short* col, byte* dst, int stride, bool add)
        {
            int a0 = W4 * (col[8 * 0] + ((1 << (ColShift - 1)) / W4));
            int a1 = a0;
            int a2 = a0;
            int a3 = a0;

            a0 += W2 * col[8 * 2];
            a1 += W6 * col[8 * 2];
            a2 -= W6 * col[8 * 2];
            a3 -= W2 * col[8 * 2];

            int b0 = W1 * col[8 * 1] + W3 * col[8 * 3];
            int b1 = W3 * col[8 * 1] - W7 * col[8 * 3];
            int b2 = W5 * col[8 * 1] - W1 * col[8 * 3];
            int b3 = W7 * col[8 * 1] - W5 * col[8 * 3];

            int c4 = col[8 * 4];
            if (c4 != 0)
            {
                a0 += W4 * c4;
                a1 -= W4 * c4;
                a2 -= W4 * c4;
                a3 += W4 * c4;
            }

            int c5 = col[8 * 5];
            if (c5 != 0)
            {
                b0 += W5 * c5;
                b1 -= W1 * c5;
                b2 += W7 * c5;
                b3 += W3 * c5;
            }

            int c6 = col[8 * 6];
            if (c6 != 0)
            {
                a0 += W6 * c6;
                a1 -= W2 * c6;
                a2 += W2 * c6;
                a3 -= W6 * c6;
            }

            int c7 = col[8 * 7];
            if (c7 != 0)
            {
                b0 += W7 * c7;
                b1 -= W5 * c7;
                b2 += W3 * c7;
                b3 -= W1 * c7;
            }

            int v0 = (a0 + b0) >> ColShift;
            int v1 = (a1 + b1) >> ColShift;
            int v2 = (a2 + b2) >> ColShift;
            int v3 = (a3 + b3) >> ColShift;
            int v4 = (a3 - b3) >> ColShift;
            int v5 = (a2 - b2) >> ColShift;
            int v6 = (a1 - b1) >> ColShift;
            int v7 = (a0 - b0) >> ColShift;

            if (add)
            {
                dst[0] = Clip(dst[0] + v0); dst += stride;
                dst[0] = Clip(dst[0] + v1); dst += stride;
                dst[0] = Clip(dst[0] + v2); dst += stride;
                dst[0] = Clip(dst[0] + v3); dst += stride;
                dst[0] = Clip(dst[0] + v4); dst += stride;
                dst[0] = Clip(dst[0] + v5); dst += stride;
                dst[0] = Clip(dst[0] + v6); dst += stride;
                dst[0] = Clip(dst[0] + v7);
            }
            else
            {
                dst[0] = Clip(v0); dst += stride;
                dst[0] = Clip(v1); dst += stride;
                dst[0] = Clip(v2); dst += stride;
                dst[0] = Clip(v3); dst += stride;
                dst[0] = Clip(v4); dst += stride;
                dst[0] = Clip(v5); dst += stride;
                dst[0] = Clip(v6); dst += stride;
                dst[0] = Clip(v7);
            }
        }
    }
}
