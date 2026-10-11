namespace USMHandler.Codecs
{
    /// <summary>
    /// Constant tables from ISO/IEC 11172-2 (MPEG-1) and ISO/IEC 13818-2 (MPEG-2) Annex B.
    /// VLC lookup tables are indexed by the next N bits of the bitstream and hold (value &lt;&lt; 8) | length.
    /// A length of 0 marks an invalid code.
    /// </summary>
    internal static class Mpeg2Tables
    {
        // Macroblock type flags
        public const int MbIntra = 1;
        public const int MbPattern = 2;
        public const int MbBackward = 4;
        public const int MbForward = 8;
        public const int MbQuant = 16;

        public const int MbaEscape = 34;
        public const int MbaStuffing = 35;

        public const int DctEob = 64;
        public const int DctEscape = 65;

        public static readonly byte[] ZigZag =
        {
             0,  1,  8, 16,  9,  2,  3, 10, 17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
        };

        public static readonly byte[] AlternateScan =
        {
             0,  8, 16, 24,  1,  9,  2, 10, 17, 25, 32, 40, 48, 56, 57, 49,
            41, 33, 26, 18,  3, 11,  4, 12, 19, 27, 34, 42, 50, 58, 35, 43,
            51, 59, 20, 28,  5, 13,  6, 14, 21, 29, 36, 44, 52, 60, 37, 45,
            53, 61, 22, 30,  7, 15, 23, 31, 38, 46, 54, 62, 39, 47, 55, 63,
        };

        // Natural (raster) order
        public static readonly byte[] DefaultIntraMatrix =
        {
             8, 16, 19, 22, 26, 27, 29, 34,
            16, 16, 22, 24, 27, 29, 34, 37,
            19, 22, 26, 27, 29, 34, 34, 38,
            22, 22, 26, 27, 29, 34, 37, 40,
            22, 26, 27, 29, 32, 35, 40, 48,
            26, 27, 29, 32, 35, 40, 48, 58,
            26, 27, 29, 34, 38, 46, 56, 69,
            27, 29, 35, 38, 46, 56, 69, 83,
        };

        public static readonly byte[] NonLinearQScale =
        {
             0,  1,  2,  3,  4,  5,  6,  7,  8, 10, 12, 14, 16, 18, 20, 22,
            24, 28, 32, 36, 40, 44, 48, 52, 56, 64, 72, 80, 88, 96, 104, 112,
        };

        public static readonly double[] FrameRates =
        {
            0, 24000.0 / 1001, 24, 25, 30000.0 / 1001, 30, 50, 60000.0 / 1001, 60,
        };

        public const int MbaBits = 11;
        public static readonly int[] MbAddressIncrement = Build(MbaBits, new (int, int, int)[]
        {
            (0x1, 1, 1), (0x3, 3, 2), (0x2, 3, 3), (0x3, 4, 4), (0x2, 4, 5), (0x3, 5, 6), (0x2, 5, 7),
            (0x7, 7, 8), (0x6, 7, 9), (0xb, 8, 10), (0xa, 8, 11), (0x9, 8, 12), (0x8, 8, 13),
            (0x7, 8, 14), (0x6, 8, 15), (0x17, 10, 16), (0x16, 10, 17), (0x15, 10, 18), (0x14, 10, 19),
            (0x13, 10, 20), (0x12, 10, 21), (0x23, 11, 22), (0x22, 11, 23), (0x21, 11, 24), (0x20, 11, 25),
            (0x1f, 11, 26), (0x1e, 11, 27), (0x1d, 11, 28), (0x1c, 11, 29), (0x1b, 11, 30), (0x1a, 11, 31),
            (0x19, 11, 32), (0x18, 11, 33), (0x08, 11, MbaEscape), (0x0f, 11, MbaStuffing),
        });

        public const int MbTypeIBits = 2;
        public static readonly int[] MbTypeI = Build(MbTypeIBits, new (int, int, int)[]
        {
            (1, 1, MbIntra), (1, 2, MbQuant | MbIntra),
        });

        public const int MbTypePBits = 6;
        public static readonly int[] MbTypeP = Build(MbTypePBits, new (int, int, int)[]
        {
            (1, 1, MbForward | MbPattern),
            (1, 2, MbPattern),
            (1, 3, MbForward),
            (3, 5, MbIntra),
            (2, 5, MbQuant | MbForward | MbPattern),
            (1, 5, MbQuant | MbPattern),
            (1, 6, MbQuant | MbIntra),
        });

        public const int MbTypeBBits = 6;
        public static readonly int[] MbTypeB = Build(MbTypeBBits, new (int, int, int)[]
        {
            (2, 2, MbForward | MbBackward),
            (3, 2, MbForward | MbBackward | MbPattern),
            (2, 3, MbBackward),
            (3, 3, MbBackward | MbPattern),
            (2, 4, MbForward),
            (3, 4, MbForward | MbPattern),
            (3, 5, MbIntra),
            (2, 5, MbQuant | MbForward | MbBackward | MbPattern),
            (3, 6, MbQuant | MbForward | MbPattern),
            (2, 6, MbQuant | MbBackward | MbPattern),
            (1, 6, MbQuant | MbIntra),
        });

        public const int CbpBits = 9;
        public static readonly int[] CodedBlockPattern = BuildCbp();

        public const int MotionCodeBits = 10;
        // Absolute motion_code value; a sign bit follows every non-zero code.
        public static readonly int[] MotionCode = Build(MotionCodeBits, new (int, int, int)[]
        {
            (0x1, 1, 0), (0x1, 2, 1), (0x1, 3, 2), (0x1, 4, 3), (0x3, 6, 4), (0x5, 7, 5), (0x4, 7, 6),
            (0x3, 7, 7), (0xb, 9, 8), (0xa, 9, 9), (0x9, 9, 10), (0x11, 10, 11), (0x10, 10, 12),
            (0xf, 10, 13), (0xe, 10, 14), (0xd, 10, 15), (0xc, 10, 16),
        });

        public const int DcSizeLumaBits = 9;
        public static readonly int[] DcSizeLuma = Build(DcSizeLumaBits, new (int, int, int)[]
        {
            (0x4, 3, 0), (0x0, 2, 1), (0x1, 2, 2), (0x5, 3, 3), (0x6, 3, 4), (0xe, 4, 5),
            (0x1e, 5, 6), (0x3e, 6, 7), (0x7e, 7, 8), (0xfe, 8, 9), (0x1fe, 9, 10), (0x1ff, 9, 11),
        });

        public const int DcSizeChromaBits = 10;
        public static readonly int[] DcSizeChroma = Build(DcSizeChromaBits, new (int, int, int)[]
        {
            (0x0, 2, 0), (0x1, 2, 1), (0x2, 2, 2), (0x6, 3, 3), (0xe, 4, 4), (0x1e, 5, 5),
            (0x3e, 6, 6), (0x7e, 7, 7), (0xfe, 8, 8), (0x1fe, 9, 9), (0x3fe, 10, 10), (0x3ff, 10, 11),
        });

        // DCT coefficient tables. Entry value = run | (level << 8); run DctEob / DctEscape are special.
        public const int DctBits = 16;
        public static readonly int[] DctTableZero = BuildDct(Source.DctB14Codes, 0x2, 2);   // Table B.14
        public static readonly int[] DctTableOne = BuildDct(Source.DctB15Codes, 0x6, 4);    // Table B.15

        // Source data for the lookup tables above; nested so it is initialised before first use.
        private static class Source
        {
            public static readonly sbyte[] DctRun =
            {
                 0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
                 0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
                 0,  0,  0,  0,  0,  0,  0,  0,  1,  1,  1,  1,  1,  1,  1,  1,
                 1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  2,  2,  2,  2,  2,  3,
                 3,  3,  3,  4,  4,  4,  5,  5,  5,  6,  6,  6,  7,  7,  8,  8,
                 9,  9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16,
                17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
            };

            public static readonly sbyte[] DctLevel =
            {
                 1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15, 16,
                17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
                33, 34, 35, 36, 37, 38, 39, 40,  1,  2,  3,  4,  5,  6,  7,  8,
                 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,  1,  2,  3,  4,  5,  1,
                 2,  3,  4,  1,  2,  3,  1,  2,  3,  1,  2,  3,  1,  2,  1,  2,
                 1,  2,  1,  2,  1,  2,  1,  2,  1,  2,  1,  2,  1,  2,  1,  2,
                 1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
            };

            // (code, length) per run/level index above; sign bit not included.
            public static readonly ushort[,] DctB14Codes =
            {
                { 0x3, 2 }, { 0x4, 4 }, { 0x5, 5 }, { 0x6, 7 }, { 0x26, 8 }, { 0x21, 8 }, { 0xa, 10 }, { 0x1d, 12 },
                { 0x18, 12 }, { 0x13, 12 }, { 0x10, 12 }, { 0x1a, 13 }, { 0x19, 13 }, { 0x18, 13 }, { 0x17, 13 }, { 0x1f, 14 },
                { 0x1e, 14 }, { 0x1d, 14 }, { 0x1c, 14 }, { 0x1b, 14 }, { 0x1a, 14 }, { 0x19, 14 }, { 0x18, 14 }, { 0x17, 14 },
                { 0x16, 14 }, { 0x15, 14 }, { 0x14, 14 }, { 0x13, 14 }, { 0x12, 14 }, { 0x11, 14 }, { 0x10, 14 }, { 0x18, 15 },
                { 0x17, 15 }, { 0x16, 15 }, { 0x15, 15 }, { 0x14, 15 }, { 0x13, 15 }, { 0x12, 15 }, { 0x11, 15 }, { 0x10, 15 },
                { 0x3, 3 }, { 0x6, 6 }, { 0x25, 8 }, { 0xc, 10 }, { 0x1b, 12 }, { 0x16, 13 }, { 0x15, 13 }, { 0x1f, 15 },
                { 0x1e, 15 }, { 0x1d, 15 }, { 0x1c, 15 }, { 0x1b, 15 }, { 0x1a, 15 }, { 0x19, 15 }, { 0x13, 16 }, { 0x12, 16 },
                { 0x11, 16 }, { 0x10, 16 }, { 0x5, 4 }, { 0x4, 7 }, { 0xb, 10 }, { 0x14, 12 }, { 0x14, 13 }, { 0x7, 5 },
                { 0x24, 8 }, { 0x1c, 12 }, { 0x13, 13 }, { 0x6, 5 }, { 0xf, 10 }, { 0x12, 12 }, { 0x7, 6 }, { 0x9, 10 },
                { 0x12, 13 }, { 0x5, 6 }, { 0x1e, 12 }, { 0x14, 16 }, { 0x4, 6 }, { 0x15, 12 }, { 0x7, 7 }, { 0x11, 12 },
                { 0x5, 7 }, { 0x11, 13 }, { 0x27, 8 }, { 0x10, 13 }, { 0x23, 8 }, { 0x1a, 16 }, { 0x22, 8 }, { 0x19, 16 },
                { 0x20, 8 }, { 0x18, 16 }, { 0xe, 10 }, { 0x17, 16 }, { 0xd, 10 }, { 0x16, 16 }, { 0x8, 10 }, { 0x15, 16 },
                { 0x1f, 12 }, { 0x1a, 12 }, { 0x19, 12 }, { 0x17, 12 }, { 0x16, 12 }, { 0x1f, 13 }, { 0x1e, 13 }, { 0x1d, 13 },
                { 0x1c, 13 }, { 0x1b, 13 }, { 0x1f, 16 }, { 0x1e, 16 }, { 0x1d, 16 }, { 0x1c, 16 }, { 0x1b, 16 },
            };

            public static readonly ushort[,] DctB15Codes =
            {
                { 0x02, 2 }, { 0x06, 3 }, { 0x07, 4 }, { 0x1c, 5 }, { 0x1d, 5 }, { 0x05, 6 }, { 0x04, 6 }, { 0x7b, 7 },
                { 0x7c, 7 }, { 0x23, 8 }, { 0x22, 8 }, { 0xfa, 8 }, { 0xfb, 8 }, { 0xfe, 8 }, { 0xff, 8 }, { 0x1f, 14 },
                { 0x1e, 14 }, { 0x1d, 14 }, { 0x1c, 14 }, { 0x1b, 14 }, { 0x1a, 14 }, { 0x19, 14 }, { 0x18, 14 }, { 0x17, 14 },
                { 0x16, 14 }, { 0x15, 14 }, { 0x14, 14 }, { 0x13, 14 }, { 0x12, 14 }, { 0x11, 14 }, { 0x10, 14 }, { 0x18, 15 },
                { 0x17, 15 }, { 0x16, 15 }, { 0x15, 15 }, { 0x14, 15 }, { 0x13, 15 }, { 0x12, 15 }, { 0x11, 15 }, { 0x10, 15 },
                { 0x02, 3 }, { 0x06, 5 }, { 0x79, 7 }, { 0x27, 8 }, { 0x20, 8 }, { 0x16, 13 }, { 0x15, 13 }, { 0x1f, 15 },
                { 0x1e, 15 }, { 0x1d, 15 }, { 0x1c, 15 }, { 0x1b, 15 }, { 0x1a, 15 }, { 0x19, 15 }, { 0x13, 16 }, { 0x12, 16 },
                { 0x11, 16 }, { 0x10, 16 }, { 0x05, 5 }, { 0x07, 7 }, { 0xfc, 8 }, { 0x0c, 10 }, { 0x14, 13 }, { 0x07, 5 },
                { 0x26, 8 }, { 0x1c, 12 }, { 0x13, 13 }, { 0x06, 6 }, { 0xfd, 8 }, { 0x12, 12 }, { 0x07, 6 }, { 0x04, 9 },
                { 0x12, 13 }, { 0x06, 7 }, { 0x1e, 12 }, { 0x14, 16 }, { 0x04, 7 }, { 0x15, 12 }, { 0x05, 7 }, { 0x11, 12 },
                { 0x78, 7 }, { 0x11, 13 }, { 0x7a, 7 }, { 0x10, 13 }, { 0x21, 8 }, { 0x1a, 16 }, { 0x25, 8 }, { 0x19, 16 },
                { 0x24, 8 }, { 0x18, 16 }, { 0x05, 9 }, { 0x17, 16 }, { 0x07, 9 }, { 0x16, 16 }, { 0x0d, 10 }, { 0x15, 16 },
                { 0x1f, 12 }, { 0x1a, 12 }, { 0x19, 12 }, { 0x17, 12 }, { 0x16, 12 }, { 0x1f, 13 }, { 0x1e, 13 }, { 0x1d, 13 },
                { 0x1c, 13 }, { 0x1b, 13 }, { 0x1f, 16 }, { 0x1e, 16 }, { 0x1d, 16 }, { 0x1c, 16 }, { 0x1b, 16 },
            };

            // (code, length) indexed by coded_block_pattern value (Table B.9).
            public static readonly byte[,] CbpCodes =
            {
                { 0x01, 9 }, { 0x0b, 5 }, { 0x09, 5 }, { 0x0d, 6 }, { 0x0d, 4 }, { 0x17, 7 }, { 0x13, 7 }, { 0x1f, 8 },
                { 0x0c, 4 }, { 0x16, 7 }, { 0x12, 7 }, { 0x1e, 8 }, { 0x13, 5 }, { 0x1b, 8 }, { 0x17, 8 }, { 0x13, 8 },
                { 0x0b, 4 }, { 0x15, 7 }, { 0x11, 7 }, { 0x1d, 8 }, { 0x11, 5 }, { 0x19, 8 }, { 0x15, 8 }, { 0x11, 8 },
                { 0x0f, 6 }, { 0x0f, 8 }, { 0x0d, 8 }, { 0x03, 9 }, { 0x0f, 5 }, { 0x0b, 8 }, { 0x07, 8 }, { 0x07, 9 },
                { 0x0a, 4 }, { 0x14, 7 }, { 0x10, 7 }, { 0x1c, 8 }, { 0x0e, 6 }, { 0x0e, 8 }, { 0x0c, 8 }, { 0x02, 9 },
                { 0x10, 5 }, { 0x18, 8 }, { 0x14, 8 }, { 0x10, 8 }, { 0x0e, 5 }, { 0x0a, 8 }, { 0x06, 8 }, { 0x06, 9 },
                { 0x12, 5 }, { 0x1a, 8 }, { 0x16, 8 }, { 0x12, 8 }, { 0x0d, 5 }, { 0x09, 8 }, { 0x05, 8 }, { 0x05, 9 },
                { 0x0c, 5 }, { 0x08, 8 }, { 0x04, 8 }, { 0x04, 9 }, { 0x07, 3 }, { 0x0a, 5 }, { 0x08, 5 }, { 0x0c, 6 },
            };
        }

        private static int[] BuildCbp()
        {
            var entries = new (int, int, int)[64];
            for (int i = 0; i < 64; i++)
                entries[i] = (Source.CbpCodes[i, 0], Source.CbpCodes[i, 1], i);
            return Build(CbpBits, entries);
        }

        private static int[] BuildDct(ushort[,] codes, int eobCode, int eobLength)
        {
            int n = codes.GetLength(0);
            var entries = new (int, int, int)[n + 2];
            for (int i = 0; i < n; i++)
                entries[i] = (codes[i, 0], codes[i, 1], Source.DctRun[i] | (Source.DctLevel[i] << 8));
            entries[n] = (0x1, 6, DctEscape);
            entries[n + 1] = (eobCode, eobLength, DctEob);
            return Build(DctBits, entries);
        }

        private static int[] Build(int bits, (int code, int length, int value)[] entries)
        {
            var table = new int[1 << bits];
            foreach (var (code, length, value) in entries)
            {
                int shift = bits - length;
                int start = code << shift;
                int count = 1 << shift;
                for (int i = 0; i < count; i++)
                {
                    if (table[start + i] != 0)
                        throw new InvalidOperationException("Overlapping VLC code in MPEG table.");
                    table[start + i] = (value << 8) | length;
                }
            }
            return table;
        }
    }
}
