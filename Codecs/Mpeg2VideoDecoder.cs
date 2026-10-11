using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using static USMHandler.Codecs.Mpeg2Tables;

namespace USMHandler.Codecs
{
    /// <summary>
    /// A decoded YUV 4:2:0 picture. Planes cover the coded (macroblock aligned) size;
    /// the visible picture is the top-left Width x Height of it.
    /// </summary>
    internal sealed unsafe class Mpeg2Frame
    {
        public readonly int CodedWidth;
        public readonly int CodedHeight;
        public readonly byte[] Y;
        public readonly byte[] Cb;
        public readonly byte[] Cr;

        internal readonly byte* PY;
        internal readonly byte* PCb;
        internal readonly byte* PCr;
        internal bool IsB;
        internal bool Displayed;

        public Mpeg2Frame(int codedWidth, int codedHeight)
        {
            CodedWidth = codedWidth;
            CodedHeight = codedHeight;

            // Pinned so the decoder can keep raw pointers to the planes.
            Y = GC.AllocateArray<byte>(codedWidth * codedHeight, pinned: true);
            Cb = GC.AllocateArray<byte>(codedWidth * codedHeight / 4, pinned: true);
            Cr = GC.AllocateArray<byte>(codedWidth * codedHeight / 4, pinned: true);
            Array.Fill(Cb, (byte)128);
            Array.Fill(Cr, (byte)128);

            PY = (byte*)Unsafe.AsPointer(ref Y[0]);
            PCb = (byte*)Unsafe.AsPointer(ref Cb[0]);
            PCr = (byte*)Unsafe.AsPointer(ref Cr[0]);
        }

        public int LumaStride => CodedWidth;
        public int ChromaStride => CodedWidth / 2;
    }

    /// <summary>
    /// Pure managed MPEG-1 / MPEG-2 (4:2:0, main profile) elementary stream video decoder.
    /// Feed it raw stream bytes in any chunk size; frames are delivered in display order through <see cref="FrameReady"/>.
    /// </summary>
    internal sealed unsafe class Mpeg2VideoDecoder
    {
        public delegate void FrameHandler(Mpeg2Frame frame, int width, int height);

        /// <summary>Invoked synchronously for every output frame. The frame is reused after the handler returns.</summary>
        public FrameHandler? FrameReady;

        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>Frame rate from the sequence header, or 0 until one has been parsed.</summary>
        public double FrameRate { get; private set; }

        private sealed class BitstreamException : Exception { }

        private const int Padding = 512;

        private const int PicI = 1;
        private const int PicP = 2;
        private const int PicB = 3;

        private const int StructTop = 1;
        private const int StructBottom = 2;
        private const int StructFrame = 3;

        // Motion prediction kinds
        private const int MtFrame = 0;          // frame picture, frame prediction
        private const int MtFieldInFrame = 1;   // frame picture, field prediction
        private const int MtDualPrimeFrame = 2; // frame picture, dual prime
        private const int MtField = 3;          // field picture, field prediction
        private const int Mt16x8 = 4;           // field picture, 16x8 prediction
        private const int MtDualPrimeField = 5; // field picture, dual prime

        // ---- input buffering ----
        private byte[] _buf = new byte[1 << 16];
        private int _len;
        private int _scanPos;
        private int _unitStart = -1;
        private int _framesOut;

        // ---- bit reader ----
        private byte* _p;
        private int _bitPos;
        private int _bitEnd;

        // ---- sequence ----
        private bool _haveSequence;
        private bool _mpeg2;
        private int _horizontalSize;
        private int _verticalSize;
        private int _frameRateCode;
        private bool _progressiveSequence = true;
        private bool _tmpgexs;
        private int _chromaFormat = 1;
        private readonly int[] _intraMatrix = new int[64];
        private readonly int[] _nonIntraMatrix = new int[64];

        // ---- GOP ----
        private bool _closedGop;
        private bool _brokenLink;

        // ---- picture ----
        private bool _pictureHeaderSeen;
        private bool _pictureStarted;
        private bool _skipPicture;
        private int _pictureType;
        private readonly int[] _fCode = new int[4]; // [s * 2 + t]
        private readonly int[] _fullPel = new int[2];
        private int _intraDcPrecision;
        private int _pictureStructure = StructFrame;
        private bool _topFieldFirst;
        private bool _framePredFrameDct = true;
        private bool _concealmentMotionVectors;
        private bool _qScaleType;
        private bool _intraVlcFormat;
        private bool _alternateScan;

        // ---- frames ----
        private int _mbWidth;
        private int _mbHeight;      // in frame macroblock rows
        private int _mbHeightPic;   // rows in the current picture (half for field pictures)
        private int _codedWidth;
        private int _codedHeight;
        private readonly List<Mpeg2Frame> _pool = new();
        private Mpeg2Frame? _refOld;
        private Mpeg2Frame? _refNew;
        private Mpeg2Frame? _cur;
        private Mpeg2Frame? _fwdRef;
        private Mpeg2Frame? _bwdRef;
        private bool _firstFieldPending;
        private int _firstFieldStructure;
        private bool _secondField;
        private int _parity; // 0 top, 1 bottom (field pictures only)

        // ---- slice / macroblock ----
        private int _qScale;
        private readonly int[] _dcPred = new int[3];
        private readonly int[] _pmv = new int[8];      // [r * 4 + s * 2 + t]
        private readonly int[] _mv = new int[8];       // [r * 4 + s * 2 + t]
        private readonly int[] _fieldSelect = new int[4]; // [r * 2 + s]
        private readonly int[] _dualPrime = new int[4];
        private int _motionType;
        private bool _mbForward;
        private bool _mbBackward;

        private readonly short[] _block = GC.AllocateArray<short>(64, pinned: true);
        private readonly short* _pBlock;

        public Mpeg2VideoDecoder()
        {
            _pBlock = (short*)Unsafe.AsPointer(ref _block[0]);
            LoadDefaultMatrices();
        }

        /// <summary>Feeds stream bytes. Returns the number of frames output during this call.</summary>
        public int Decode(byte[] data, int offset, int count)
        {
            _framesOut = 0;
            if (count <= 0)
                return 0;

            EnsureCapacity(_len + count);
            Buffer.BlockCopy(data, offset, _buf, _len, count);
            _len += count;

            fixed (byte* p = _buf)
            {
                _p = p;
                ScanStartCodes();
                _p = null;
            }

            Compact();
            return _framesOut;
        }

        /// <summary>Decodes whatever is still buffered and outputs the last delayed frame.</summary>
        public int Flush()
        {
            _framesOut = 0;
            EnsureCapacity(_len);
            Array.Clear(_buf, _len, Padding);

            fixed (byte* p = _buf)
            {
                _p = p;
                if (_unitStart >= 0)
                    ProcessUnit(_unitStart, _len);
                _p = null;
            }

            _unitStart = -1;
            _len = 0;
            _scanPos = 0;

            FinishPicture();
            OutputPendingReference();
            return _framesOut;
        }

        // =====================================================================
        // Start code scanning
        // =====================================================================

        private void EnsureCapacity(int needed)
        {
            needed += Padding;
            if (_buf.Length >= needed)
                return;
            Array.Resize(ref _buf, Math.Max(needed, _buf.Length * 2));
        }

        private void ScanStartCodes()
        {
            byte* p = _p;
            int i = _scanPos;
            int end = _len - 3;

            while (i < end)
            {
                byte b2 = p[i + 2];
                if (b2 > 1)
                {
                    i += 3;
                    continue;
                }

                if (b2 == 1 && p[i] == 0 && p[i + 1] == 0)
                {
                    if (_unitStart >= 0)
                        ProcessUnit(_unitStart, i);
                    _unitStart = i;
                    i += 4;
                    continue;
                }

                i++;
            }

            _scanPos = i;
        }

        private void Compact()
        {
            int keepFrom = _unitStart >= 0 ? _unitStart : Math.Max(0, _len - 3);
            if (keepFrom <= 0)
                return;

            Buffer.BlockCopy(_buf, keepFrom, _buf, 0, _len - keepFrom);
            _len -= keepFrom;
            _scanPos = Math.Max(0, _scanPos - keepFrom);
            if (_unitStart >= 0)
                _unitStart = 0;
        }

        private void ProcessUnit(int start, int end)
        {
            int code = _p[start + 3];
            _bitPos = (start + 4) * 8;
            _bitEnd = end * 8;

            if (code >= 0x01 && code <= 0xAF)
            {
                if (_pictureHeaderSeen)
                    DecodeSlice(code);
                return;
            }

            try
            {
                switch (code)
                {
                    case 0x00:
                        FinishPicture();
                        ParsePictureHeader();
                        break;
                    case 0xB3:
                        FinishPicture();
                        ParseSequenceHeader();
                        break;
                    case 0xB5:
                        ParseExtension();
                        break;
                    case 0xB8:
                        FinishPicture();
                        ParseGopHeader();
                        break;
                    case 0xB7:
                        FinishPicture();
                        OutputPendingReference();
                        break;
                    case 0xB2:
                        ParseUserData(start + 4, end);
                        break;
                }
            }
            catch (BitstreamException)
            {
            }
        }

        // =====================================================================
        // Bit reader
        // =====================================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint Peek(int n)
        {
            ulong v = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(_p + (_bitPos >> 3)));
            return (uint)((v << (_bitPos & 7)) >> (64 - n));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int Read(int n)
        {
            uint v = Peek(n);
            _bitPos += n;
            return (int)v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ReadFlag()
        {
            uint v = Peek(1);
            _bitPos++;
            return v != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ReadVlc(int[] table, int bits)
        {
            int e = table[Peek(bits)];
            int length = e & 0xFF;
            if (length == 0)
                throw new BitstreamException();
            _bitPos += length;
            return e >> 8;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CheckOverrun()
        {
            if (_bitPos > _bitEnd)
                throw new BitstreamException();
        }

        // =====================================================================
        // Headers
        // =====================================================================

        private void LoadDefaultMatrices()
        {
            for (int i = 0; i < 64; i++)
            {
                _intraMatrix[i] = DefaultIntraMatrix[i];
                _nonIntraMatrix[i] = 16;
            }
        }

        private void LoadMatrix(int[] matrix)
        {
            for (int i = 0; i < 64; i++)
                matrix[ZigZag[i]] = Read(8);
        }

        private void ParseSequenceHeader()
        {
            _horizontalSize = Read(12);
            _verticalSize = Read(12);
            Read(4); // aspect ratio
            _frameRateCode = Read(4);
            Read(18); // bit rate
            Read(1);  // marker
            Read(10); // vbv buffer size
            Read(1);  // constrained parameters

            LoadDefaultMatrices();
            if (ReadFlag())
                LoadMatrix(_intraMatrix);
            if (ReadFlag())
                LoadMatrix(_nonIntraMatrix);

            _mpeg2 = false;
            _progressiveSequence = true;
            _chromaFormat = 1;
            FrameRate = _frameRateCode < FrameRates.Length ? FrameRates[_frameRateCode] : 0;
            _haveSequence = _horizontalSize > 0 && _verticalSize > 0;
        }

        private void ParseExtension()
        {
            int id = Read(4);
            switch (id)
            {
                case 1: // sequence extension
                    Read(8); // profile and level
                    _progressiveSequence = ReadFlag();
                    _chromaFormat = Read(2);
                    _horizontalSize = (_horizontalSize & 0xFFF) | (Read(2) << 12);
                    _verticalSize = (_verticalSize & 0xFFF) | (Read(2) << 12);
                    Read(12); // bit rate extension
                    Read(1);  // marker
                    Read(8);  // vbv buffer size extension
                    Read(1);  // low delay
                    int rateN = Read(2);
                    int rateD = Read(5);
                    if (_frameRateCode < FrameRates.Length)
                        FrameRate = FrameRates[_frameRateCode] * (rateN + 1) / (rateD + 1);
                    _mpeg2 = true;
                    break;

                case 3: // quant matrix extension
                    if (ReadFlag())
                        LoadMatrix(_intraMatrix);
                    if (ReadFlag())
                        LoadMatrix(_nonIntraMatrix);
                    // Chroma matrices only apply to 4:2:2 / 4:4:4.
                    break;

                case 8: // picture coding extension
                    _fCode[0] = Read(4);
                    _fCode[1] = Read(4);
                    _fCode[2] = Read(4);
                    _fCode[3] = Read(4);
                    _intraDcPrecision = Read(2);
                    _pictureStructure = Read(2);
                    if (_pictureStructure == 0)
                        _pictureStructure = StructFrame;
                    _topFieldFirst = ReadFlag();
                    _framePredFrameDct = ReadFlag();
                    _concealmentMotionVectors = ReadFlag();
                    _qScaleType = ReadFlag();
                    _intraVlcFormat = ReadFlag();
                    _alternateScan = ReadFlag();
                    break;
            }
        }

        private void ParseUserData(int start, int end)
        {
            // TMPGEnc / CRI Sofdec MPEG-1 streams tag themselves like this and use 11-bit intra DC precision.
            ReadOnlySpan<byte> tag = " TMPGEXS "u8;
            ReadOnlySpan<byte> data = new ReadOnlySpan<byte>(_p + start, end - start);
            if (data.Length > 29 && data.Slice(0, Math.Min(data.Length, 20 + tag.Length - 1)).IndexOf(tag) >= 0)
                _tmpgexs = true;
        }

        private void ParseGopHeader()
        {
            Read(25); // time code
            _closedGop = ReadFlag();
            _brokenLink = ReadFlag();
        }

        private void ParsePictureHeader()
        {
            Read(10); // temporal reference
            _pictureType = Read(3);
            Read(16); // vbv delay

            _fullPel[0] = _fullPel[1] = 0;
            if (_pictureType == PicP || _pictureType == PicB)
            {
                _fullPel[0] = Read(1);
                _fCode[0] = _fCode[1] = Read(3);
            }
            if (_pictureType == PicB)
            {
                _fullPel[1] = Read(1);
                _fCode[2] = _fCode[3] = Read(3);
            }

            // MPEG-1 defaults; a picture coding extension overrides these for MPEG-2.
            _intraDcPrecision = _tmpgexs && !_mpeg2 ? 3 : 0;
            _pictureStructure = StructFrame;
            _topFieldFirst = true;
            _framePredFrameDct = true;
            _concealmentMotionVectors = false;
            _qScaleType = false;
            _intraVlcFormat = false;
            _alternateScan = false;

            _pictureHeaderSeen = true;
            _pictureStarted = false;
            _skipPicture = false;
        }

        // =====================================================================
        // Picture / frame management
        // =====================================================================

        private void StartPicture()
        {
            _pictureStarted = true;
            _skipPicture = false;

            if (!_haveSequence || _chromaFormat != 1 || _pictureType < PicI || _pictureType > PicB)
            {
                _skipPicture = true;
                return;
            }

            int mbWidth = (_horizontalSize + 15) / 16;
            int mbHeight = (_mpeg2 && !_progressiveSequence)
                ? 2 * ((_verticalSize + 31) / 32)
                : (_verticalSize + 15) / 16;

            if (mbWidth != _mbWidth || mbHeight != _mbHeight)
            {
                _mbWidth = mbWidth;
                _mbHeight = mbHeight;
                _codedWidth = mbWidth * 16;
                _codedHeight = mbHeight * 16;
                _pool.Clear();
                _refOld = _refNew = _cur = null;
                _firstFieldPending = false;
            }
            Width = _horizontalSize;
            Height = _verticalSize;

            bool field = _pictureStructure != StructFrame;
            _parity = _pictureStructure == StructBottom ? 1 : 0;
            _mbHeightPic = field ? _mbHeight / 2 : _mbHeight;

            if (field && _firstFieldPending && _cur != null && _pictureStructure != _firstFieldStructure)
            {
                _secondField = true;
            }
            else
            {
                if (_firstFieldPending)
                    CompleteFrame(); // unpaired field

                if (_pictureType == PicP && _refNew == null)
                {
                    _skipPicture = true;
                    return;
                }
                if (_pictureType == PicB && (_refNew == null || (_refOld == null && (!_closedGop || _brokenLink))))
                {
                    // Leading B-frames of an open GOP reference a picture we never saw.
                    _skipPicture = true;
                    return;
                }

                _cur = AllocateFrame();
                _cur.IsB = _pictureType == PicB;
                _cur.Displayed = false;
                _secondField = false;
                _firstFieldPending = field;
                _firstFieldStructure = _pictureStructure;
            }

            if (_pictureType == PicB)
            {
                _fwdRef = _refOld ?? _refNew;
                _bwdRef = _refNew;
            }
            else
            {
                _fwdRef = _refNew ?? _cur;
                _bwdRef = null;
            }
        }

        private void FinishPicture()
        {
            if (!_pictureStarted)
            {
                _pictureHeaderSeen = false;
                return;
            }

            _pictureStarted = false;
            _pictureHeaderSeen = false;

            if (_skipPicture)
                return;

            if (_pictureStructure == StructFrame || _secondField)
                CompleteFrame();
        }

        private void CompleteFrame()
        {
            Mpeg2Frame? frame = _cur;
            _cur = null;
            _firstFieldPending = false;
            _secondField = false;

            if (frame == null)
                return;

            if (frame.IsB)
            {
                Output(frame);
            }
            else
            {
                OutputPendingReference();
                _refOld = _refNew;
                _refNew = frame;
            }
        }

        private void OutputPendingReference()
        {
            if (_refNew != null && !_refNew.Displayed)
                Output(_refNew);
        }

        private void Output(Mpeg2Frame frame)
        {
            frame.Displayed = true;
            _framesOut++;
            FrameReady?.Invoke(frame, Width, Height);
        }

        private Mpeg2Frame AllocateFrame()
        {
            foreach (Mpeg2Frame f in _pool)
            {
                if (f != _refOld && f != _refNew && f != _cur)
                    return f;
            }

            var frame = new Mpeg2Frame(_codedWidth, _codedHeight);
            _pool.Add(frame);
            return frame;
        }

        private Mpeg2Frame SelectFieldReference(Mpeg2Frame reference, int fieldSelect)
        {
            // The second field of a P frame may predict from the first field of the same frame.
            if (_secondField && _pictureType == PicP && fieldSelect != _parity)
                return _cur!;
            return reference;
        }

        // =====================================================================
        // Slice / macroblock layer
        // =====================================================================

        private int QuantiserScale(int code) => _qScaleType ? NonLinearQScale[code] : code * 2;

        private void ResetDcPredictors()
        {
            int v = 1 << (7 + _intraDcPrecision);
            _dcPred[0] = _dcPred[1] = _dcPred[2] = v;
        }

        private void DecodeSlice(int code)
        {
            if (!_pictureStarted)
                StartPicture();
            if (_skipPicture || _cur == null)
                return;

            try
            {
                int mbRow = code - 1;
                if (_mpeg2 && _verticalSize > 2800)
                    mbRow += Read(3) << 7;
                if (mbRow >= _mbHeightPic)
                    return;

                _qScale = QuantiserScale(Read(5));

                if (_mpeg2)
                {
                    if (Peek(1) == 1)
                    {
                        Read(1); // intra_slice_flag
                        Read(1); // intra_slice
                        Read(7); // reserved
                        while (ReadFlag())
                            Read(8);
                    }
                    else
                    {
                        Read(1);
                    }
                }
                else
                {
                    while (ReadFlag())
                        Read(8);
                }

                ResetDcPredictors();
                Array.Clear(_pmv);

                int total = _mbWidth * _mbHeightPic;
                int address = mbRow * _mbWidth - 1;
                bool first = true;

                while (true)
                {
                    int increment = ReadMacroblockAddressIncrement();
                    if (first)
                    {
                        address += increment;
                        first = false;
                    }
                    else
                    {
                        for (int k = 1; k < increment; k++)
                        {
                            int skipped = address + k;
                            if (skipped >= total)
                                return;
                            SkippedMacroblock(skipped % _mbWidth, skipped / _mbWidth);
                        }
                        address += increment;
                    }

                    if (address < 0 || address >= total)
                        return;

                    DecodeMacroblock(address % _mbWidth, address / _mbWidth);

                    if (_bitPos >= _bitEnd || Peek(23) == 0)
                        break;
                }
            }
            catch (BitstreamException)
            {
                // Corrupt slice: keep whatever was decoded and move on to the next one.
            }
        }

        private int ReadMacroblockAddressIncrement()
        {
            int increment = 0;
            while (true)
            {
                CheckOverrun();
                int v = ReadVlc(MbAddressIncrement, MbaBits);
                if (v == MbaStuffing)
                    continue;
                if (v == MbaEscape)
                {
                    increment += 33;
                    continue;
                }
                return increment + v;
            }
        }

        private void SkippedMacroblock(int mbx, int mby)
        {
            ResetDcPredictors();

            if (_pictureType == PicP)
            {
                Array.Clear(_pmv);
                Array.Clear(_mv);
                _mbForward = true;
                _mbBackward = false;
                if (_pictureStructure == StructFrame)
                {
                    _motionType = MtFrame;
                }
                else
                {
                    _motionType = MtField;
                    _fieldSelect[0] = _parity;
                }
            }
            else if (_pictureType == PicB)
            {
                // Same direction(s) as the previous macroblock, predicted from the PMVs (7.6.6.4).
                for (int s = 0; s < 2; s++)
                {
                    _mv[s * 2] = _pmv[s * 2] << _fullPel[s];
                    _mv[s * 2 + 1] = _pmv[s * 2 + 1] << _fullPel[s];
                }
                if (_pictureStructure == StructFrame)
                {
                    _motionType = MtFrame;
                }
                else
                {
                    _motionType = MtField;
                    _fieldSelect[0] = _fieldSelect[1] = _parity;
                }
            }
            else
            {
                throw new BitstreamException();
            }

            Predict(mbx, mby);
        }

        private void DecodeMacroblock(int mbx, int mby)
        {
            int mbType = _pictureType switch
            {
                PicI => ReadVlc(MbTypeI, MbTypeIBits),
                PicP => ReadVlc(MbTypeP, MbTypePBits),
                _ => ReadVlc(MbTypeB, MbTypeBBits),
            };

            bool intra = (mbType & MbIntra) != 0;
            bool pattern = (mbType & MbPattern) != 0;
            bool forward = (mbType & MbForward) != 0;
            bool backward = (mbType & MbBackward) != 0;
            bool frame = _pictureStructure == StructFrame;

            if (forward || backward)
            {
                if (frame)
                {
                    int t = _framePredFrameDct ? 2 : Read(2);
                    _motionType = t switch { 1 => MtFieldInFrame, 2 => MtFrame, 3 => MtDualPrimeFrame, _ => throw new BitstreamException() };
                }
                else
                {
                    int t = Read(2);
                    _motionType = t switch { 1 => MtField, 2 => Mt16x8, 3 => MtDualPrimeField, _ => throw new BitstreamException() };
                }
            }

            bool fieldDct = false;
            if (frame && !_framePredFrameDct && (intra || pattern))
                fieldDct = ReadFlag();

            if ((mbType & MbQuant) != 0)
                _qScale = QuantiserScale(Read(5));

            if (intra)
            {
                if (_concealmentMotionVectors)
                {
                    if (!frame)
                        Read(1); // field select
                    int mx = DecodeMotion(_fCode[0], _pmv[0]);
                    int my = DecodeMotion(_fCode[1], _pmv[1]);
                    _pmv[0] = _pmv[4] = mx;
                    _pmv[1] = _pmv[5] = my;
                    Read(1); // marker
                }
                else
                {
                    Array.Clear(_pmv);
                }

                DecodeBlocks(mbx, mby, 63, true, fieldDct);
                return;
            }

            ResetDcPredictors();

            if (_pictureType == PicP && !forward)
            {
                // "No MC": zero vector from the same parity.
                Array.Clear(_pmv);
                Array.Clear(_mv);
                forward = true;
                if (frame)
                {
                    _motionType = MtFrame;
                }
                else
                {
                    _motionType = MtField;
                    _fieldSelect[0] = _parity;
                }
            }
            else
            {
                if (forward)
                    DecodeMotionVectors(0);
                if (backward)
                    DecodeMotionVectors(1);
            }

            _mbForward = forward;
            _mbBackward = backward;

            int cbp = pattern ? ReadVlc(CodedBlockPattern, CbpBits) : 0;

            Predict(mbx, mby);

            if (cbp != 0)
                DecodeBlocks(mbx, mby, cbp, false, fieldDct);
        }

        // =====================================================================
        // Motion vectors
        // =====================================================================

        private int DecodeMotion(int fCode, int prediction)
        {
            int code = ReadVlc(MotionCode, MotionCodeBits);
            if (code == 0)
                return prediction;
            if (ReadFlag())
                code = -code;

            int rSize = fCode - 1;
            int delta;
            if (rSize <= 0)
            {
                delta = code;
                rSize = 0;
            }
            else
            {
                int residual = Read(rSize);
                delta = ((Math.Abs(code) - 1) << rSize) + residual + 1;
                if (code < 0)
                    delta = -delta;
            }

            int v = prediction + delta;
            int high = (16 << rSize) - 1;
            int low = -(16 << rSize);
            int range = 32 << rSize;
            if (v < low)
                v += range;
            else if (v > high)
                v -= range;
            return v;
        }

        private int ReadDualPrimeDelta()
        {
            if (!ReadFlag())
                return 0;
            return ReadFlag() ? -1 : 1;
        }

        private void DecodeMotionVectors(int s)
        {
            int fx = _fCode[s * 2];
            int fy = _fCode[s * 2 + 1];
            int b = s * 2;

            switch (_motionType)
            {
                case MtFrame:
                {
                    int mx = DecodeMotion(fx, _pmv[b]);
                    int my = DecodeMotion(fy, _pmv[b + 1]);
                    _pmv[b] = _pmv[4 + b] = mx;
                    _pmv[b + 1] = _pmv[4 + b + 1] = my;
                    _mv[b] = mx << _fullPel[s];
                    _mv[b + 1] = my << _fullPel[s];
                    break;
                }

                case MtFieldInFrame:
                    for (int r = 0; r < 2; r++)
                    {
                        int i = r * 4 + b;
                        _fieldSelect[r * 2 + s] = Read(1);
                        int mx = DecodeMotion(fx, _pmv[i]);
                        int my = DecodeMotion(fy, _pmv[i + 1] >> 1);
                        _pmv[i] = mx;
                        _pmv[i + 1] = my * 2;
                        _mv[i] = mx;
                        _mv[i + 1] = my;
                    }
                    break;

                case MtField:
                {
                    _fieldSelect[s] = Read(1);
                    int mx = DecodeMotion(fx, _pmv[b]);
                    int my = DecodeMotion(fy, _pmv[b + 1]);
                    _pmv[b] = _pmv[4 + b] = mx;
                    _pmv[b + 1] = _pmv[4 + b + 1] = my;
                    _mv[b] = mx;
                    _mv[b + 1] = my;
                    break;
                }

                case Mt16x8:
                    for (int r = 0; r < 2; r++)
                    {
                        int i = r * 4 + b;
                        _fieldSelect[r * 2 + s] = Read(1);
                        int mx = DecodeMotion(fx, _pmv[i]);
                        int my = DecodeMotion(fy, _pmv[i + 1]);
                        _pmv[i] = mx;
                        _pmv[i + 1] = my;
                        _mv[i] = mx;
                        _mv[i + 1] = my;
                    }
                    break;

                case MtDualPrimeFrame:
                case MtDualPrimeField:
                {
                    bool framePicture = _motionType == MtDualPrimeFrame;
                    int mx = DecodeMotion(fx, _pmv[b]);
                    int dmx = ReadDualPrimeDelta();
                    int my = DecodeMotion(fy, framePicture ? _pmv[b + 1] >> 1 : _pmv[b + 1]);
                    int dmy = ReadDualPrimeDelta();

                    _pmv[b] = _pmv[4 + b] = mx;
                    _pmv[b + 1] = _pmv[4 + b + 1] = framePicture ? my * 2 : my;
                    _mv[b] = mx;
                    _mv[b + 1] = my;

                    int rx = mx > 0 ? 1 : 0;
                    int ry = my > 0 ? 1 : 0;
                    if (framePicture)
                    {
                        int m = _topFieldFirst ? 1 : 3;
                        // top field predicted from the bottom reference field
                        _dualPrime[0] = ((mx * m + rx) >> 1) + dmx;
                        _dualPrime[1] = ((my * m + ry) >> 1) + dmy - 1;
                        m = 4 - m;
                        // bottom field predicted from the top reference field
                        _dualPrime[2] = ((mx * m + rx) >> 1) + dmx;
                        _dualPrime[3] = ((my * m + ry) >> 1) + dmy + 1;
                    }
                    else
                    {
                        _dualPrime[0] = ((mx + rx) >> 1) + dmx;
                        _dualPrime[1] = ((my + ry) >> 1) + dmy + (_parity == 0 ? -1 : 1);
                    }
                    break;
                }
            }
        }

        // =====================================================================
        // Motion compensation
        // =====================================================================

        private void Predict(int mbx, int mby)
        {
            bool average = false;
            if (_mbForward && _fwdRef != null)
            {
                PredictDirection(0, _fwdRef, mbx, mby, false);
                average = true;
            }
            if (_mbBackward && _bwdRef != null)
                PredictDirection(1, _bwdRef, mbx, mby, average);
        }

        private void PredictDirection(int s, Mpeg2Frame reference, int mbx, int mby, bool average)
        {
            int x = mbx * 16;
            int b = s * 2;

            switch (_motionType)
            {
                case MtFrame:
                    MotionCompensate(reference, -1, -1, x, mby * 16, _mv[b], _mv[b + 1], 16, average);
                    break;

                case MtFieldInFrame:
                    for (int r = 0; r < 2; r++)
                    {
                        int i = r * 4 + b;
                        MotionCompensate(reference, _fieldSelect[r * 2 + s], r, x, mby * 8, _mv[i], _mv[i + 1], 8, average);
                    }
                    break;

                case MtDualPrimeFrame:
                    MotionCompensate(reference, 0, 0, x, mby * 8, _mv[b], _mv[b + 1], 8, false);
                    MotionCompensate(reference, 1, 1, x, mby * 8, _mv[b], _mv[b + 1], 8, false);
                    MotionCompensate(reference, 1, 0, x, mby * 8, _dualPrime[0], _dualPrime[1], 8, true);
                    MotionCompensate(reference, 0, 1, x, mby * 8, _dualPrime[2], _dualPrime[3], 8, true);
                    break;

                case MtField:
                {
                    int sel = _fieldSelect[s];
                    MotionCompensate(SelectFieldReference(reference, sel), sel, _parity, x, mby * 16, _mv[b], _mv[b + 1], 16, average);
                    break;
                }

                case Mt16x8:
                    for (int r = 0; r < 2; r++)
                    {
                        int i = r * 4 + b;
                        int sel = _fieldSelect[r * 2 + s];
                        MotionCompensate(SelectFieldReference(reference, sel), sel, _parity, x, mby * 16 + r * 8, _mv[i], _mv[i + 1], 8, average);
                    }
                    break;

                case MtDualPrimeField:
                {
                    int opposite = 1 - _parity;
                    MotionCompensate(reference, _parity, _parity, x, mby * 16, _mv[b], _mv[b + 1], 16, false);
                    MotionCompensate(SelectFieldReference(reference, opposite), opposite, _parity, x, mby * 16, _dualPrime[0], _dualPrime[1], 16, true);
                    break;
                }
            }
        }

        /// <param name="srcField">-1 for frame addressing, otherwise the reference field parity.</param>
        /// <param name="dstField">-1 for frame addressing, otherwise the destination field parity.</param>
        /// <param name="x">Luma x of the block.</param>
        /// <param name="y">Luma y of the block, in rows of the chosen addressing.</param>
        private void MotionCompensate(Mpeg2Frame reference, int srcField, int dstField, int x, int y, int mvx, int mvy, int height, bool average)
        {
            Mpeg2Frame cur = _cur!;
            int w = _codedWidth;
            int h = _codedHeight;

            CompensatePlane(reference.PY, cur.PY, w, h, srcField, dstField, x, y, mvx, mvy, 16, height, average);

            int cmx = mvx / 2;
            int cmy = mvy / 2;
            CompensatePlane(reference.PCb, cur.PCb, w / 2, h / 2, srcField, dstField, x >> 1, y >> 1, cmx, cmy, 8, height >> 1, average);
            CompensatePlane(reference.PCr, cur.PCr, w / 2, h / 2, srcField, dstField, x >> 1, y >> 1, cmx, cmy, 8, height >> 1, average);
        }

        private static void CompensatePlane(byte* srcPlane, byte* dstPlane, int stride, int planeHeight,
            int srcField, int dstField, int x, int y, int mvx, int mvy, int bw, int bh, bool average)
        {
            byte* src = srcPlane;
            int srcStride = stride;
            int srcHeight = planeHeight;
            if (srcField >= 0)
            {
                src += srcField * stride;
                srcStride = stride * 2;
                srcHeight = planeHeight / 2;
            }

            byte* dst = dstPlane;
            int dstStride = stride;
            if (dstField >= 0)
            {
                dst += dstField * stride;
                dstStride = stride * 2;
            }
            dst += y * dstStride + x;

            int sx = x + (mvx >> 1);
            int sy = y + (mvy >> 1);
            int hx = mvx & 1;
            int hy = mvy & 1;

            byte* sp;
            int sps;

            if (sx < 0 || sy < 0 || sx + bw + hx > stride || sy + bh + hy > srcHeight)
            {
                // Reference block reaches outside the picture: replicate edge pixels.
                byte* edge = stackalloc byte[17 * 17];
                int tw = bw + 1;
                int th = bh + 1;
                for (int j = 0; j < th; j++)
                {
                    int yy = Math.Clamp(sy + j, 0, srcHeight - 1);
                    byte* row = src + yy * srcStride;
                    for (int i = 0; i < tw; i++)
                        edge[j * tw + i] = row[Math.Clamp(sx + i, 0, stride - 1)];
                }
                sp = edge;
                sps = tw;
            }
            else
            {
                sp = src + sy * srcStride + sx;
                sps = srcStride;
            }

            int mode = hx | (hy << 1);
            if (!average)
            {
                switch (mode)
                {
                    case 0:
                        for (int j = 0; j < bh; j++, sp += sps, dst += dstStride)
                            Buffer.MemoryCopy(sp, dst, bw, bw);
                        break;
                    case 1:
                        for (int j = 0; j < bh; j++, sp += sps, dst += dstStride)
                            for (int i = 0; i < bw; i++)
                                dst[i] = (byte)((sp[i] + sp[i + 1] + 1) >> 1);
                        break;
                    case 2:
                        for (int j = 0; j < bh; j++, sp += sps, dst += dstStride)
                            for (int i = 0; i < bw; i++)
                                dst[i] = (byte)((sp[i] + sp[i + sps] + 1) >> 1);
                        break;
                    default:
                        for (int j = 0; j < bh; j++, sp += sps, dst += dstStride)
                            for (int i = 0; i < bw; i++)
                                dst[i] = (byte)((sp[i] + sp[i + 1] + sp[i + sps] + sp[i + sps + 1] + 2) >> 2);
                        break;
                }
            }
            else
            {
                switch (mode)
                {
                    case 0:
                        for (int j = 0; j < bh; j++, sp += sps, dst += dstStride)
                            for (int i = 0; i < bw; i++)
                                dst[i] = (byte)((dst[i] + sp[i] + 1) >> 1);
                        break;
                    case 1:
                        for (int j = 0; j < bh; j++, sp += sps, dst += dstStride)
                            for (int i = 0; i < bw; i++)
                                dst[i] = (byte)((dst[i] + ((sp[i] + sp[i + 1] + 1) >> 1) + 1) >> 1);
                        break;
                    case 2:
                        for (int j = 0; j < bh; j++, sp += sps, dst += dstStride)
                            for (int i = 0; i < bw; i++)
                                dst[i] = (byte)((dst[i] + ((sp[i] + sp[i + sps] + 1) >> 1) + 1) >> 1);
                        break;
                    default:
                        for (int j = 0; j < bh; j++, sp += sps, dst += dstStride)
                            for (int i = 0; i < bw; i++)
                                dst[i] = (byte)((dst[i] + ((sp[i] + sp[i + 1] + sp[i + sps] + sp[i + sps + 1] + 2) >> 2) + 1) >> 1);
                        break;
                }
            }
        }

        // =====================================================================
        // Blocks
        // =====================================================================

        private void DecodeBlocks(int mbx, int mby, int cbp, bool intra, bool fieldDct)
        {
            Mpeg2Frame cur = _cur!;
            int w = _codedWidth;
            int cw = w / 2;
            bool field = _pictureStructure != StructFrame;

            int lumaStride = field ? w * 2 : w;
            byte* luma = cur.PY + (field ? _parity * w : 0) + mby * 16 * lumaStride + mbx * 16;

            int chromaStride = field ? cw * 2 : cw;
            int chromaOffset = (field ? _parity * cw : 0) + mby * 8 * chromaStride + mbx * 8;

            for (int b = 0; b < 6; b++)
            {
                if ((cbp & (32 >> b)) == 0)
                    continue;

                byte* dst;
                int stride;
                if (b < 4)
                {
                    if (fieldDct)
                    {
                        stride = lumaStride * 2;
                        dst = luma + (b >> 1) * lumaStride + (b & 1) * 8;
                    }
                    else
                    {
                        stride = lumaStride;
                        dst = luma + (b >> 1) * 8 * lumaStride + (b & 1) * 8;
                    }
                }
                else
                {
                    stride = chromaStride;
                    dst = (b == 4 ? cur.PCb : cur.PCr) + chromaOffset;
                }

                bool acPresent = intra
                    ? DecodeIntraBlock(b < 4 ? 0 : b - 3)
                    : DecodeNonIntraBlock();

                if (intra)
                    Idct.Put(_pBlock, dst, stride, acPresent);
                else
                    Idct.Add(_pBlock, dst, stride, acPresent);
            }
        }

        /// <summary>Returns true when any coefficient other than DC is non-zero.</summary>
        private bool DecodeIntraBlock(int component)
        {
            short* blk = _pBlock;

            int size = component == 0
                ? ReadVlc(DcSizeLuma, DcSizeLumaBits)
                : ReadVlc(DcSizeChroma, DcSizeChromaBits);

            int diff = 0;
            if (size > 0)
            {
                diff = Read(size);
                if ((diff & (1 << (size - 1))) == 0)
                    diff -= (1 << size) - 1;
            }

            _dcPred[component] += diff;
            int dc = _dcPred[component] << (3 - _intraDcPrecision);
            blk[0] = (short)dc;

            int[] table = _intraVlcFormat ? DctTableOne : DctTableZero;
            byte[] scan = _alternateScan ? AlternateScan : ZigZag;
            int[] matrix = _intraMatrix;
            int qScale = _qScale;
            bool mpeg2 = _mpeg2;
            int parity = dc;
            bool ac = false;
            int i = 0;

            while (true)
            {
                CheckOverrun();
                int e = table[Peek(DctBits)];
                int length = e & 0xFF;
                if (length == 0)
                    throw new BitstreamException();
                _bitPos += length;

                int run = (e >> 8) & 0xFF;
                if (run == DctEob)
                    break;

                int level;
                if (run == DctEscape)
                {
                    run = Read(6);
                    level = ReadEscapeLevel();
                }
                else
                {
                    level = e >> 16;
                    if (ReadFlag())
                        level = -level;
                }

                i += run + 1;
                if (i > 63)
                    throw new BitstreamException();

                int pos = scan[i];
                int magnitude = level < 0 ? -level : level;
                int v = (magnitude * qScale * matrix[pos]) >> 4;
                if (!mpeg2 && v != 0)
                    v = (v - 1) | 1;
                if (level < 0)
                    v = -v;
                v = Math.Clamp(v, -2048, 2047);

                blk[pos] = (short)v;
                parity ^= v;
                ac = true;
            }

            if (mpeg2 && (parity & 1) == 0)
            {
                blk[63] ^= 1;
                ac = true;
            }

            return ac;
        }

        private bool DecodeNonIntraBlock()
        {
            short* blk = _pBlock;
            int[] table = DctTableZero;
            byte[] scan = _alternateScan ? AlternateScan : ZigZag;
            int[] matrix = _nonIntraMatrix;
            int qScale = _qScale;
            bool mpeg2 = _mpeg2;
            int parity = 0;
            int i = -1;
            bool ac = false;
            bool first = true;

            while (true)
            {
                CheckOverrun();
                int run;
                int level;

                if (first && Peek(1) == 1)
                {
                    // First coefficient of a non-intra block: '1s' means run 0, level 1.
                    _bitPos++;
                    run = 0;
                    level = ReadFlag() ? -1 : 1;
                }
                else
                {
                    int e = table[Peek(DctBits)];
                    int length = e & 0xFF;
                    if (length == 0)
                        throw new BitstreamException();
                    _bitPos += length;

                    run = (e >> 8) & 0xFF;
                    if (run == DctEob)
                        break;

                    if (run == DctEscape)
                    {
                        run = Read(6);
                        level = ReadEscapeLevel();
                    }
                    else
                    {
                        level = e >> 16;
                        if (ReadFlag())
                            level = -level;
                    }
                }
                first = false;

                i += run + 1;
                if (i > 63)
                    throw new BitstreamException();

                int pos = scan[i];
                int magnitude = level < 0 ? -level : level;
                int v = ((magnitude * 2 + 1) * qScale * matrix[pos]) >> 5;
                if (!mpeg2 && v != 0)
                    v = (v - 1) | 1;
                if (level < 0)
                    v = -v;
                v = Math.Clamp(v, -2048, 2047);

                blk[pos] = (short)v;
                parity ^= v;
                if (pos != 0)
                    ac = true;
            }

            if (mpeg2 && (parity & 1) == 0)
            {
                blk[63] ^= 1;
                ac = true;
            }

            return ac;
        }

        private int ReadEscapeLevel()
        {
            if (_mpeg2)
            {
                int level = Read(12);
                if ((level & 0x800) != 0)
                    level -= 0x1000;
                return level;
            }
            else
            {
                int level = Read(8);
                if (level == 0)
                    return Read(8);
                if (level == 128)
                    return Read(8) - 256;
                return level > 128 ? level - 256 : level;
            }
        }
    }
}
