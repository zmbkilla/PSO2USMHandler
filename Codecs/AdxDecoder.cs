using System.Buffers.Binary;

namespace USMHandler.Codecs
{
    /// <summary>
    /// Streaming CRI ADX (4-bit ADPCM, encoding types 3 and 4) decoder.
    /// Feed it the raw ADX stream, header included; decoded audio is delivered one ADX frame
    /// (one block per channel) at a time as interleaved 16-bit samples.
    /// </summary>
    internal sealed class AdxDecoder
    {
        public delegate void FrameHandler(short[] interleaved, int sampleFrames);

        private const int CoeffBits = 12;

        private byte[] _buf = new byte[0x10000];
        private int _len;
        private bool _headerParsed;
        private int _blockSize;
        private int _samplesPerBlock;
        private int _coeff1;
        private int _coeff2;
        private int[] _s1 = Array.Empty<int>();
        private int[] _s2 = Array.Empty<int>();
        private short[] _out = Array.Empty<short>();

        public int Channels { get; private set; }
        public int SampleRate { get; private set; }

        /// <summary>True once the end-of-stream block was reached or the stream was rejected.</summary>
        public bool Finished { get; private set; }

        public void Decode(byte[] data, int offset, int count, FrameHandler onFrame)
        {
            if (Finished || count <= 0)
                return;

            if (_buf.Length < _len + count)
                Array.Resize(ref _buf, Math.Max(_len + count, _buf.Length * 2));
            Buffer.BlockCopy(data, offset, _buf, _len, count);
            _len += count;

            int pos = 0;
            if (!_headerParsed)
            {
                if (!TryParseHeader(out pos))
                    return;
            }

            int frameBytes = _blockSize * Channels;
            while (!Finished && _len - pos >= 2)
            {
                // A block whose scale has the top bit set marks the end of the stream.
                if ((_buf[pos] & 0x80) != 0)
                {
                    Finished = true;
                    break;
                }
                if (_len - pos < frameBytes)
                    break;

                for (int ch = 0; ch < Channels; ch++)
                    DecodeBlock(pos + ch * _blockSize, ch);
                pos += frameBytes;

                onFrame(_out, _samplesPerBlock);
            }

            if (pos > 0)
            {
                Buffer.BlockCopy(_buf, pos, _buf, 0, _len - pos);
                _len -= pos;
            }
        }

        private bool TryParseHeader(out int dataStart)
        {
            dataStart = 0;
            if (_len < 4)
                return false;

            if (_buf[0] != 0x80 || _buf[1] != 0x00)
            {
                Finished = true;
                return false;
            }

            int copyrightOffset = BinaryPrimitives.ReadUInt16BigEndian(_buf.AsSpan(2));
            int headerSize = copyrightOffset + 4;
            if (headerSize < 20 || _len < headerSize)
                return false;

            int encoding = _buf[4];
            int blockSize = _buf[5];
            int bitsPerSample = _buf[6];
            int channels = _buf[7];
            int sampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(_buf.AsSpan(8));
            int cutoff = BinaryPrimitives.ReadUInt16BigEndian(_buf.AsSpan(16));

            if ((encoding != 3 && encoding != 4) || bitsPerSample != 4 || blockSize <= 2 ||
                channels < 1 || channels > 8 || sampleRate <= 0)
            {
                Finished = true;
                return false;
            }

            _blockSize = blockSize;
            _samplesPerBlock = (blockSize - 2) * 2;
            Channels = channels;
            SampleRate = sampleRate;
            _s1 = new int[channels];
            _s2 = new int[channels];
            _out = new short[_samplesPerBlock * channels];

            double a = Math.Sqrt(2.0) - Math.Cos(2.0 * Math.PI * cutoff / sampleRate);
            double b = Math.Sqrt(2.0) - 1.0;
            double c = (a - Math.Sqrt((a + b) * (a - b))) / b;
            _coeff1 = (int)Math.Round(c * 2.0 * (1 << CoeffBits));
            _coeff2 = (int)Math.Round(-(c * c) * (1 << CoeffBits));

            _headerParsed = true;
            dataStart = headerSize;
            return true;
        }

        private void DecodeBlock(int offset, int ch)
        {
            byte[] buf = _buf;
            int scale = (buf[offset] << 8) | buf[offset + 1];
            int s1 = _s1[ch];
            int s2 = _s2[ch];
            int channels = Channels;
            short[] output = _out;

            for (int i = 0; i < _samplesPerBlock; i++)
            {
                int nibbles = buf[offset + 2 + (i >> 1)];
                int d = (i & 1) == 0 ? nibbles >> 4 : nibbles & 0xF;
                if (d >= 8)
                    d -= 16;

                int s0 = d * scale + ((_coeff1 * s1 + _coeff2 * s2) >> CoeffBits);
                s2 = s1;
                s1 = Math.Clamp(s0, short.MinValue, short.MaxValue);
                output[i * channels + ch] = (short)s1;
            }

            _s1[ch] = s1;
            _s2[ch] = s2;
        }
    }

    /// <summary>
    /// Converts interleaved 16-bit audio of any rate / channel count into 16-bit stereo at a fixed rate
    /// using linear interpolation. Mono is spread to both channels at -3 dB (matching libswresample).
    /// </summary>
    internal sealed class StereoResampler
    {
        private readonly int _inChannels;
        private readonly double _step;
        private double _pos;
        private bool _primed;
        private int _prevL;
        private int _prevR;
        private short[] _out = Array.Empty<short>();

        public StereoResampler(int inRate, int inChannels, int outRate)
        {
            _inChannels = inChannels;
            _step = (double)inRate / outRate;
        }

        /// <summary>Returns the number of stereo frames written to <paramref name="output"/>, which is reused between calls.</summary>
        public int Process(short[] input, int frames, out short[] output)
        {
            int maxOut = (int)Math.Ceiling((frames + 1) / _step) + 2;
            if (_out.Length < maxOut * 2)
                _out = new short[maxOut * 2];
            output = _out;

            if (frames <= 0)
                return 0;

            if (!_primed)
            {
                Sample(input, 0, out _prevL, out _prevR);
                _pos = 1.0;
                _primed = true;
            }

            int written = 0;
            while ((int)_pos + 1 <= frames)
            {
                int i = (int)_pos;
                double frac = _pos - i;

                int l0, r0, l1, r1;
                if (i == 0)
                {
                    l0 = _prevL;
                    r0 = _prevR;
                }
                else
                {
                    Sample(input, i - 1, out l0, out r0);
                }
                Sample(input, i, out l1, out r1);

                _out[written * 2] = (short)Math.Round(l0 + (l1 - l0) * frac);
                _out[written * 2 + 1] = (short)Math.Round(r0 + (r1 - r0) * frac);
                written++;
                _pos += _step;
            }

            _pos -= frames;
            Sample(input, frames - 1, out _prevL, out _prevR);
            return written;
        }

        // Virtual index k maps to input frame k (index 0 of the next call is the last frame of this one).
        private void Sample(short[] input, int frame, out int left, out int right)
        {
            if (_inChannels == 1)
            {
                int v = (int)Math.Round(input[frame] * 0.70710678118654752);
                left = v;
                right = v;
            }
            else
            {
                left = input[frame * _inChannels];
                right = input[frame * _inChannels + 1];
            }
        }
    }
}
