namespace USMHandler.Codecs
{
    /// <summary>YUV 4:2:0 (BT.601, limited range) to packed RGB24 conversion.</summary>
    internal static unsafe class YuvToRgb
    {
        private static readonly int[] s_y = new int[256];
        private static readonly int[] s_rv = new int[256];
        private static readonly int[] s_gu = new int[256];
        private static readonly int[] s_gv = new int[256];
        private static readonly int[] s_bu = new int[256];

        static YuvToRgb()
        {
            for (int i = 0; i < 256; i++)
            {
                s_y[i] = (int)Math.Round(1.164383 * (i - 16) * 65536) + 32768;
                s_rv[i] = (int)Math.Round(1.596027 * (i - 128) * 65536);
                s_gu[i] = (int)Math.Round(-0.391762 * (i - 128) * 65536);
                s_gv[i] = (int)Math.Round(-0.812968 * (i - 128) * 65536);
                s_bu[i] = (int)Math.Round(2.017232 * (i - 128) * 65536);
            }
        }

        /// <summary>Writes width * height * 3 bytes (R, G, B) into <paramref name="rgb"/>.</summary>
        public static void Convert(Mpeg2Frame frame, int width, int height, byte[] rgb)
        {
            const int rowsPerChunk = 32;
            int chunks = (height + rowsPerChunk - 1) / rowsPerChunk;

            if (chunks <= 1)
            {
                ConvertRows(frame, width, 0, height, rgb);
                return;
            }

            Parallel.For(0, chunks, c =>
            {
                int y0 = c * rowsPerChunk;
                ConvertRows(frame, width, y0, Math.Min(height, y0 + rowsPerChunk), rgb);
            });
        }

        private static void ConvertRows(Mpeg2Frame frame, int width, int y0, int y1, byte[] rgb)
        {
            int ys = frame.LumaStride;
            int cs = frame.ChromaStride;

            fixed (byte* py = frame.Y, pu = frame.Cb, pv = frame.Cr, prgb = rgb)
            fixed (int* ty = s_y, trv = s_rv, tgu = s_gu, tgv = s_gv, tbu = s_bu)
            {
                for (int y = y0; y < y1; y++)
                {
                    byte* yRow = py + y * ys;
                    byte* uRow = pu + (y >> 1) * cs;
                    byte* vRow = pv + (y >> 1) * cs;
                    byte* d = prgb + (long)y * width * 3;

                    for (int x = 0; x < width; x++)
                    {
                        int u = uRow[x >> 1];
                        int v = vRow[x >> 1];
                        int l = ty[yRow[x]];

                        int r = (l + trv[v]) >> 16;
                        int g = (l + tgu[u] + tgv[v]) >> 16;
                        int b = (l + tbu[u]) >> 16;

                        d[0] = (uint)r > 255 ? (byte)(r < 0 ? 0 : 255) : (byte)r;
                        d[1] = (uint)g > 255 ? (byte)(g < 0 ? 0 : 255) : (byte)g;
                        d[2] = (uint)b > 255 ? (byte)(b < 0 ? 0 : 255) : (byte)b;
                        d += 3;
                    }
                }
            }
        }
    }
}
