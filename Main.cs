using FFmpeg;
using FFmpeg.AutoGen;
using Microsoft.VisualBasic;
using System.Numerics;
using System.Text;
using System.Runtime.InteropServices;
using System.IO.Pipelines;

namespace USMHandler
{
    public class Main
    {
        public CancellationTokenSource? _playbackCts;

        public bool smallerthanmb = false;
        public bool hasAudio = false;

        //video fps fix
        private static int _displayFrameCount = 0;
        private static double _frameDurationMs = 0;
        private static System.Diagnostics.Stopwatch _playbackStopwatch = new System.Diagnostics.Stopwatch();

        //audio param
        private const int AUDIO_FRAME_SIZE = 4096;
        private unsafe AVCodecParserContext* _audioParser = null;


        // ADX Audio Decompressor State Keepers
        private static int _adxPrevSampleL = 0;
        private static int _adxPrevSampleR = 0;
        private unsafe AVCodecContext* _audioCodecContext = null;
        private unsafe AVFrame* _audioFrame = null;
        private static unsafe SwrContext* _audioSwrContext = null;



        private byte[] permanentPixelBuffer;
        private bool _isTextureInitialized = false;



        private static unsafe SwsContext* _swsContext = null;
        private static unsafe AVFrame* _rgbFrame = null;
        private static byte[] _rgbBuffer;
        // Low-level configuration variables required by FFmpeg.AutoGen
        private const int INBUF_SIZE = 4096;

        private unsafe AVCodecParserContext* _parser = null;
        private unsafe AVCodecContext* _pCodecContext = null;
        private unsafe AVFrame* _decodedFrame = null;


        private unsafe bool CheckMpeg2Decoder()
        {
            AVCodec* codec = FFmpeg.AutoGen.ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_MPEG2VIDEO);

            if (codec == null)
            {
                return false;
            }

            Console.WriteLine("MPEG-2 decoder found.");
            return true;
        }


        public async Task<Stream> LoadDirectUSM(string filepath, byte[] key1, byte[] key2)
        {
            _playbackCts?.Cancel();
            _playbackCts?.Dispose();
            _playbackCts = new CancellationTokenSource();

            FFmpeg.AutoGen.ffmpeg.RootPath = AppDomain.CurrentDomain.BaseDirectory + "FFMPEG";

            Console.WriteLine(ffmpeg.av_version_info());

            smallerthanmb = false;
            hasAudio = false;

            string outputPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "demux_test.usm");

            FileStream outputFile = File.Create(outputPath);

            try
            {
                await Task.Run(() =>
                    DirectDemuxASync(
                        filepath,
                        key1,
                        key2,
                        outputFile,
                        _playbackCts.Token));

                outputFile.Position = 0;

                Console.WriteLine($"Demux output written to: {outputPath}");
                Console.WriteLine($"Size: {outputFile.Length:N0} bytes");

                return outputFile;
            }
            catch
            {
                outputFile.Dispose();
                throw;
            }
        }




        public async Task LoadUsmAsync(string filepath, byte[] key1, byte[] key2)
        {
            _playbackCts?.Cancel();
            _playbackCts?.Dispose();
            _playbackCts = new CancellationTokenSource();

            CancellationToken token = _playbackCts.Token;


            FFmpeg.AutoGen.ffmpeg.RootPath = AppDomain.CurrentDomain.BaseDirectory + "FFMPEG";

            if (!CheckMpeg2Decoder())
            {
                Console.WriteLine("MPEG-2 decoder was not found.");
                return;
            }



            var vms = new MemoryStream();
            var ams = new MemoryStream();

            smallerthanmb = false;
            hasAudio = false;

            await Task.Run(() => DemuxASync(filepath, key1, key2, ref vms, ref ams));

            while (vms.Length < 1024 * 1024 && !smallerthanmb)
            {
                await Task.Delay(100);
            }

            hasAudio = ams.Length > 0;

            var videoStream = new DynamicMemoryStream(vms);
            var audioStream = new DynamicMemoryStream(ams);

            PlayMediaFromMemoryStreamAsync(videoStream, audioStream, token);
        }

        private void PlayMediaFromMemoryStreamAsync(DynamicMemoryStream vms, DynamicMemoryStream ams, CancellationToken token)
        {
            // Ensure the MemoryStream is at the beginning
            vms.Seek(0, SeekOrigin.Begin);
            ams.Seek(0, SeekOrigin.Begin);

            Task.Run(() =>
            {
                if (hasAudio == true)
                {
                    StreamplayeradxAs(vms, ams, token);
                }
                else
                {
                    streamplayeradx(vms, token);
                }
            });
        }



        public async void streamplayeradx(DynamicMemoryStream vidData, CancellationToken token)
        {
            // Make sure the memory stream starts at byte 0
            vidData.Seek(0, SeekOrigin.Begin);


            // Fire off your video loop task completely independent of any audio tracks
            await System.Threading.Tasks.Task.Run(() => StreamVideoOnlyWorker(vidData, token));
        }


        private void StreamplayeradxAs(DynamicMemoryStream media, DynamicMemoryStream adxData, CancellationToken token)
        {
            if (hasAudio && adxData != null)
            {

                // Task 1: Fire off an independent worker thread strictly for video rendering frames
                Task.Run(() => StreamVideoOnlyWorker(media, token), token);

                // Task 2: Fire off a parallel worker thread strictly for synchronized NAudio streaming
                Task.Run(() => StreamAudioOnlyWorker(adxData, token), token);
            }
            else
            {
                Task.Run(() => StreamVideoOnlyWorker(media, token), token);
            }
        }

        // --- Brand New Method 1: Isolated Video Frame Decoder ---
        private unsafe void StreamVideoOnlyWorker(DynamicMemoryStream videoData, CancellationToken token)
        {
            AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_MPEG2VIDEO);
            if (codec == null)
            {
                return;
            }

            AVCodecContext* videoCtx = ffmpeg.avcodec_alloc_context3(codec);
            AVCodecParserContext* videoParser = ffmpeg.av_parser_init(AVCodecID.AV_CODEC_ID_MPEG2VIDEO);
            AVFrame* videoFrame = ffmpeg.av_frame_alloc();

            if (ffmpeg.avcodec_open2(videoCtx, codec, null) < 0)
            {
                return;
            }

            AVPacket* pkt = ffmpeg.av_packet_alloc();
            byte[] inbuf = new byte[INBUF_SIZE + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE];

            double frameDurationMs = 0;
            System.Diagnostics.Stopwatch videoClock = new System.Diagnostics.Stopwatch();
            long processedVideoFrames = 0;
            bool parse_succeed = true;

            while (parse_succeed)
            {
                if (token.IsCancellationRequested)
                    return;
                if (!videoClock.IsRunning)
                {
                    if (videoCtx->framerate.den > 0 && videoCtx->framerate.num > 0)
                        frameDurationMs = (1000.0 * videoCtx->framerate.den) / videoCtx->framerate.num;
                    else
                        frameDurationMs = 1000.0 / 29.97;

                    videoClock.Start();
                }

                int data_size = videoData.Read(inbuf, 0, INBUF_SIZE);
                if (data_size == 0) break;

                fixed (byte* ptr = inbuf)
                {
                    byte* data = ptr;
                    while (data_size > 0)
                    {
                        byte* outData = null;
                        int outSize = 0;

                        int ret = ffmpeg.av_parser_parse2(videoParser, videoCtx,
                            &outData, &outSize, data, data_size, ffmpeg.AV_NOPTS_VALUE, ffmpeg.AV_NOPTS_VALUE, 0);

                        if (ret < 0) break;

                        pkt->data = outData;
                        pkt->size = outSize;
                        data += ret;
                        data_size -= ret;

                        if (pkt->size != 0)
                        {
                            parse_succeed = decode(videoCtx, videoFrame, pkt);
                            if (!parse_succeed) break;
                            processedVideoFrames++;
                        }
                    }
                }

                // Throttle the video loop to real-time target framerate
                long expectedVideoTimeMs = (long)(processedVideoFrames * frameDurationMs);
                long actualElapsedTimeMs = videoClock.ElapsedMilliseconds;
                long timeAhead = expectedVideoTimeMs - actualElapsedTimeMs;

                if (timeAhead > 0)
                {
                    System.Threading.Thread.Sleep((int)timeAhead);
                }
            }

            // Cleanup video allocations
            decode(videoCtx, videoFrame, null);
            ffmpeg.av_packet_free(&pkt);
            if (videoParser != null) ffmpeg.av_parser_close(videoParser);
            if (videoCtx != null) ffmpeg.avcodec_free_context(&videoCtx);
            if (videoFrame != null) ffmpeg.av_frame_free(&videoFrame);
        }

        public delegate void AudioPcmDataHandler(    byte[] pcmData,    int sampleRate,    int channels,    int bitsPerSample);

        public event AudioPcmDataHandler? AudioPcmDataDecoded;

        private unsafe void StreamAudioOnlyWorker(DynamicMemoryStream audioData, CancellationToken token)
        {
            AVCodec* audioCodec =
                ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_ADPCM_ADX);

            if (audioCodec == null)
                return;

            AVCodecContext* audioCtx =
                ffmpeg.avcodec_alloc_context3(audioCodec);

            AVCodecParserContext* audioParser =
                ffmpeg.av_parser_init(AVCodecID.AV_CODEC_ID_ADPCM_ADX);

            AVFrame* audioFrame =
                ffmpeg.av_frame_alloc();

            if (audioCtx == null || audioParser == null || audioFrame == null)
            {
                if (audioParser != null)
                    ffmpeg.av_parser_close(audioParser);

                if (audioCtx != null)
                    ffmpeg.avcodec_free_context(&audioCtx);

                if (audioFrame != null)
                    ffmpeg.av_frame_free(&audioFrame);

                return;
            }

            if (ffmpeg.avcodec_open2(audioCtx, audioCodec, null) < 0)
            {
                ffmpeg.av_parser_close(audioParser);
                ffmpeg.avcodec_free_context(&audioCtx);
                ffmpeg.av_frame_free(&audioFrame);
                return;
            }

            const int targetSampleRate = 48000;
            const int targetChannels = 2;
            const int targetBitsPerSample = 16;
            const int bytesPerStereoFrame = targetChannels * sizeof(short); // 4 bytes

            AVPacket* audioPkt = ffmpeg.av_packet_alloc();
            byte[] audioBuffer = new byte[65536];
            bool parseSucceed = true;
            SwrContext* audioSwrContext = null;

            // ⏱️ HIGH-PRECISION FLOW THROTTLE
            System.Diagnostics.Stopwatch playbackClock = new System.Diagnostics.Stopwatch();
            double totalDecodedAudioMs = 0;
            bool clockStarted = false;

            try
            {
                while (parseSucceed && !token.IsCancellationRequested)
                {
                    // ⏳ BACKPRESSURE THROTTLE:
                    // If the loop has decoded more than 300ms ahead of real-world time, 
                    // force the thread to sleep until the real-world timeline catches up.
                    if (clockStarted && totalDecodedAudioMs > (playbackClock.Elapsed.TotalMilliseconds + 300))
                    {
                        System.Threading.Thread.Sleep(10);
                        continue;
                    }

                    int audioRead =
                        audioData.Read(
                            audioBuffer,
                            0,
                            audioBuffer.Length);

                    if (audioRead <= 0)
                        break;

                    fixed (byte* audioPtr = audioBuffer)
                    {
                        byte* data = audioPtr;
                        int remainingAudioSize = audioRead;

                        while (
                            remainingAudioSize > 0 &&
                            !token.IsCancellationRequested)
                        {
                            byte* outAudioData = null;
                            int outAudioSize = 0;

                            int ret = ffmpeg.av_parser_parse2(
                                audioParser,
                                audioCtx,
                                &outAudioData,
                                &outAudioSize,
                                data,
                                remainingAudioSize,
                                ffmpeg.AV_NOPTS_VALUE,
                                ffmpeg.AV_NOPTS_VALUE,
                                0);

                            if (ret < 0)
                            {
                                parseSucceed = false;
                                break;
                            }

                            data += ret;
                            remainingAudioSize -= ret;

                            audioPkt->data = outAudioData;
                            audioPkt->size = outAudioSize;

                            if (audioPkt->size == 0)
                                continue;

                            if (ffmpeg.avcodec_send_packet(
                                audioCtx,
                                audioPkt) < 0)
                            {
                                continue;
                            }

                            while (
                                !token.IsCancellationRequested &&
                                ffmpeg.avcodec_receive_frame(
                                    audioCtx,
                                    audioFrame) == 0)
                            {
                                int samples = audioFrame->nb_samples;

                                if (samples <= 0)
                                    continue;

                                if (audioSwrContext == null)
                                {
                                    SwrContext* localSwr =
                                        ffmpeg.swr_alloc();

                                    AVChannelLayout targetLayout;

                                    ffmpeg.av_channel_layout_default(
                                        &targetLayout,
                                        targetChannels);

                                    AVChannelLayout srcLayout =
                                        audioFrame->ch_layout;

                                    int swrResult =
                                        ffmpeg.swr_alloc_set_opts2(&localSwr, &targetLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, targetSampleRate, &srcLayout, (AVSampleFormat)audioFrame->format, audioFrame->sample_rate, 0, null);

                                    if (swrResult < 0 ||
                                        localSwr == null)
                                    {
                                        if (localSwr != null)
                                            ffmpeg.swr_free(&localSwr);

                                        parseSucceed = false;
                                        break;
                                    }

                                    if (ffmpeg.swr_init(localSwr) < 0)
                                    {
                                        ffmpeg.swr_free(&localSwr);
                                        parseSucceed = false;
                                        break;
                                    }

                                    audioSwrContext = localSwr;
                                }

                                int outSamples =
                                    (int)ffmpeg.av_rescale_rnd(
                                        samples,
                                        targetSampleRate,
                                        audioFrame->sample_rate,
                                        AVRounding.AV_ROUND_UP);

                                if (outSamples <= 0)
                                    continue;

                                byte[] pcmData =
                                    new byte[
                                        outSamples *
                                        targetChannels *
                                        sizeof(short)
                                    ];

                                int convertedSamples;

                                fixed (byte* pcmPtr = pcmData)
                                {
                                    byte*[] outPlanes = { pcmPtr, null, null, null };

                                    fixed (byte** outPlanesPtr = outPlanes)
                                    {
                                        convertedSamples =
                                            ffmpeg.swr_convert(audioSwrContext, outPlanesPtr, outSamples, (byte**)audioFrame->extended_data, samples);
                                    }
                                }

                                if (convertedSamples <= 0)
                                    continue;

                                int pcmByteCount =
                                    convertedSamples *
                                    targetChannels *
                                    sizeof(short);

                                if (pcmByteCount != pcmData.Length)
                                {
                                    Array.Resize(
                                        ref pcmData,
                                        pcmByteCount);
                                }

                                // ⏱️ TIMELINE MATH:
                                // Convert the number of processed samples into exact audio timeline milliseconds.
                                double sampleDurationMs = ((double)convertedSamples / targetSampleRate) * 1000.0;
                                totalDecodedAudioMs += sampleDurationMs;

                                // Start tracking real-world time the moment the first sample block completes
                                if (!clockStarted)
                                {
                                    playbackClock.Start();
                                    clockStarted = true;
                                }

                                /*
                                 * OUTPUT DRIVEN BY SELF-PACED REAL-TIME CLOCK
                                 */
                                AudioPcmDataDecoded?.Invoke(
                                    pcmData,
                                    targetSampleRate,
                                    targetChannels,
                                    targetBitsPerSample);
                            }
                        }
                    }
                }
            }
            finally
            {
                playbackClock.Stop();

                ffmpeg.av_packet_free(&audioPkt);
                if (audioParser != null) ffmpeg.av_parser_close(audioParser);
                if (audioCtx != null) ffmpeg.avcodec_free_context(&audioCtx);
                if (audioFrame != null) ffmpeg.av_frame_free(&audioFrame);
                if (audioSwrContext != null) ffmpeg.swr_free(&audioSwrContext);
            }
        }







        //private static Vector2[] DecodeAdxToPcmFrames(byte[] inputData, int dataSize)
        //{
        //    // CriWare ADX blocks typically process in 18-byte sequential frame groupings 
        //    // containing historical layout prediction scale bytes followed by nibble blocks
        //    int totalFrames = dataSize / 18;
        //    if (totalFrames <= 0) return Array.Empty<Vector2>();
        //
        //    // Each 18-byte ADX packet segment yields exactly 32 stereo audio output samples
        //    var pcmFrames = new Vector2[totalFrames * 32];
        //    int frameIndex = 0;
        //
        //    // Standard ADX historical sound optimization filter weights
        //    double coef1 = 1.8310546875;
        //    double coef2 = -0.83544921875;
        //
        //    for (int b = 0; b < totalFrames; b++)
        //    {
        //        int offset = b * 18;
        //
        //        // Extract the scale factor properties for the current audio block range
        //        short scale = (short)((inputData[offset] << 8) | inputData[offset + 1]);
        //
        //        for (int i = 0; i < 16; i++)
        //        {
        //            byte sampleByte = inputData[offset + 2 + i];
        //
        //            // Slice raw payload byte segments into independent 4-bit sound channel nibbles
        //            int nibbleL = (sampleByte >> 4) & 0x0F;
        //            int nibbleR = sampleByte & 0x0F;
        //
        //            // Convert to signed integer properties scale values
        //            if (nibbleL >= 8) nibbleL -= 16;
        //            if (nibbleR >= 8) nibbleR -= 16;
        //
        //            // Execute prediction calculations for Left channel signals
        //            double sampleL = (nibbleL * scale) + (coef1 * _adxPrevSampleL) + (coef2 * _adxPrevSampleL);
        //            _adxPrevSampleL = (int)Math.Clamp(sampleL, short.MinValue, short.MaxValue);
        //
        //            // Execute prediction calculations for Right channel signals
        //            double sampleR = (nibbleR * scale) + (coef1 * _adxPrevSampleR) + (coef2 * _adxPrevSampleR);
        //            _adxPrevSampleR = (int)Math.Clamp(sampleR, short.MinValue, short.MaxValue);
        //
        //            // Output the normalized sound coordinates into your Godot balance vectors
        //            pcmFrames[frameIndex++] = new Vector2(
        //                _adxPrevSampleL / 32768.0f,
        //                _adxPrevSampleR / 32768.0f
        //            );
        //        }
        //    }
        //
        //    return pcmFrames;
        //}



        // 1. Declare this delegate at your namespace level so any host program can listen to it
        public delegate void VideoFrameDecodedHandler(    byte[] rgbData,    int width,    int height);

        // 2. Add a field variable inside your standalone class to store the listener callback

        public event VideoFrameDecodedHandler? VideoFrameDecoded;

        // 3. This is your completely universal, Godot-free decode method layout:
        private unsafe bool decode(AVCodecContext* pCodecContext, AVFrame* frame, AVPacket* pkt)
        {
            int ret = ffmpeg.avcodec_send_packet(pCodecContext, pkt);
            if (ret < 0) return false;

            while (ret >= 0)
            {
                ret = ffmpeg.avcodec_receive_frame(pCodecContext, frame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) return true;
                else if (ret < 0) return false;

                int w = frame->width;
                int h = frame->height;

                // Initialize unmanaged scaling on the very first frame
                if (_swsContext == null)
                {
                    _rgbFrame = ffmpeg.av_frame_alloc();
                    int numBytes = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_RGB24, w, h, 1);
                    _rgbBuffer = new byte[numBytes];
                    _swsContext = ffmpeg.sws_getContext(w, h, (AVPixelFormat)frame->format, w, h, AVPixelFormat.AV_PIX_FMT_RGB24, (int)SwsFlags.SWS_BILINEAR, null, null, null);
                }

                // Convert unmanaged YUV frames to raw RGB24 bytes natively
                var dstData = new byte_ptrArray4();
                var dstLinesize = new int_array4();

                fixed (byte* rgbBufferPtr = _rgbBuffer)
                {
                    ffmpeg.av_image_fill_arrays(ref dstData, ref dstLinesize, rgbBufferPtr, AVPixelFormat.AV_PIX_FMT_RGB24, w, h, 1);
                    ffmpeg.sws_scale(_swsContext, frame->data, frame->linesize, 0, h, dstData, dstLinesize);

                    // BARE MINIMUM DELIVERABLE: Fire the raw pointer address directly out of the DLL!
                    // No threading delays, no memory copying, and no game engine frameworks required.
                    VideoFrameDecoded?.Invoke(_rgbBuffer, w, h);
                }
            }

            return true;
        }



        public (byte[], byte[])? KeySplitter(ulong? key)
        {
            if (key == null) return null;
            byte[] keyArray = new byte[8];
            BitConverter.GetBytes(key.Value).CopyTo(keyArray, 0);
            byte[] key1 = keyArray[..4];
            byte[] key2 = keyArray[4..];
            return (key1, key2);
        }

        public ulong? EncryptionKey(string videoFilename)
        {
            //ulong key1 = EncryptionKeyInFilename(videoFilename);
            //(ulong, bool)? blk = EncryptionKeyInBLK(videoFilename);
            ulong key1 = 0x207DFFFF;
            ulong blk = 0x00B8F21B;
            if (blk == null) return null;
            ulong key2 = blk;
            //audioEnc = blk.Value.Item2;

            ulong finalKey = 0x100000000000000;
            if ((key1 + key2 & 0xFFFFFFFFFFFFFF) != 0) finalKey = key1 + key2 & 0xFFFFFFFFFFFFFF;
            return finalKey;
        }

        public bool DemuxASync(string filenameArg, byte[] key1Arg, byte[] key2Arg, ref MemoryStream VidMS, ref MemoryStream ADXMS)
        {
            if (!File.Exists(filenameArg)) throw new FileNotFoundException($"File {filenameArg} doesn't exist...");
            string filename = Path.GetFileName(filenameArg);
            byte[] key1, key2;
            if (key1Arg.Length == 0 && key2Arg.Length == 0)
            {
                Console.WriteLine($"Finding encryption key for {filename}...");
                (byte[], byte[])? split = KeySplitter(EncryptionKey(filename));
                if (split == null) return false;
                key1 = split.Value.Item1;
                key2 = split.Value.Item2;
            }
            else
            {
                key1 = key1Arg;
                key2 = key2Arg;
            }
            key1 = key1.Reverse().ToArray();
            key2 = key2.Reverse().ToArray();

            USM file = new(filenameArg, key1, key2);
            //check if file is usm
            //byte[] check = File.ReadAllBytes(filenameArg)[0..4];
            //if (Encoding.ASCII.GetString(check).ToString() != "CRID")
            //    return false;

            // Fix: Replaced new FileStream with File.OpenRead to avoid SafeFileHandle signature mismatch issues
            using (FileStream fs = File.OpenRead(filenameArg))
            {
                byte[] check = new byte[4];
                if (fs.Read(check, 0, 4) != 4 || Encoding.ASCII.GetString(check) != "CRID")
                {
                    return false;
                }
            }


            file.DemuxAsync(true, true, ref VidMS, ref ADXMS);
            if (file.done)
            {
                smallerthanmb = true;
            }
            return true;
        }

        public bool DirectDemuxASync(string filenameArg, byte[] key1Arg, byte[] key2Arg, Stream output,CancellationToken cts)
        {
            if (!File.Exists(filenameArg)) throw new FileNotFoundException($"File {filenameArg} doesn't exist...");
            string filename = Path.GetFileName(filenameArg);
            byte[] key1, key2;
            if (key1Arg.Length == 0 && key2Arg.Length == 0)
            {
                Console.WriteLine($"Finding encryption key for {filename}...");
                (byte[], byte[])? split = KeySplitter(EncryptionKey(filename));
                if (split == null) return false;
                key1 = split.Value.Item1;
                key2 = split.Value.Item2;
            }
            else
            {
                key1 = key1Arg;
                key2 = key2Arg;
            }
            key1 = key1.Reverse().ToArray();
            key2 = key2.Reverse().ToArray();

            USM file = new(filenameArg, key1, key2);

            // Fix: Replaced new FileStream with File.OpenRead to avoid SafeFileHandle signature mismatch issues
            using (FileStream fs = File.OpenRead(filenameArg))
            {
                byte[] check = new byte[4];
                if (fs.Read(check, 0, 4) != 4 || Encoding.ASCII.GetString(check) != "CRID")
                {
                    return false;
                }
            }


            file.DemuxOnly(output,cts);
            if (file.done)
            {
                smallerthanmb = true;
            }
            return true;
        }



        //adding it here for now




        //end of code
    }
}
