using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace USMHandler
{
    public class DirectPl
    {
        public static unsafe Stream UsmToVlcStream(Stream usmStream)
        {
            MemoryStream outputStream = new MemoryStream();

            GCHandle inputHandle = GCHandle.Alloc(usmStream);
            GCHandle outputHandle = default;

            AVFormatContext* input = null;
            AVFormatContext* output = null;
            AVPacket* packet = null;

            try
            {
                const int bufferSize = 32768;

                avio_alloc_context_read_packet readCallback = ReadInput;
                avio_alloc_context_write_packet writeCallback = WriteOutput;

                byte* inputBuffer = (byte*)ffmpeg.av_malloc((ulong)bufferSize);

                AVIOContext* inputIO = ffmpeg.avio_alloc_context(
                    inputBuffer,
                    bufferSize,
                    0,
                    (void*)GCHandle.ToIntPtr(inputHandle),
                    readCallback,
                    null,
                    null);

                if (inputIO == null)
                    throw new Exception("Failed to create input AVIO context.");

                input = ffmpeg.avformat_alloc_context();

                if (input == null)
                    throw new Exception("Failed to allocate input AVFormatContext.");

                input->pb = inputIO;
                input->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

                int ret = ffmpeg.avformat_open_input(
                    &input,
                    null,
                    null,
                    null);

                if (ret < 0)
                    throw new Exception($"Failed to open USM: {GetError(ret)}");

                ret = ffmpeg.avformat_find_stream_info(
                    input,
                    null);

                if (ret < 0)
                    throw new Exception($"Failed to find stream information: {GetError(ret)}");

                ret = ffmpeg.avformat_alloc_output_context2(
                    &output,
                    null,
                    "mpegts",
                    null);

                if (ret < 0 || output == null)
                    throw new Exception($"Failed to create MPEG-TS output: {GetError(ret)}");

                int[] streamMap = new int[input->nb_streams];

                Array.Fill(streamMap, -1);

                for (uint i = 0; i < input->nb_streams; i++)
                {
                    AVStream* inputStream = input->streams[i];

                    if (inputStream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO &&
                        inputStream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
                    {
                        continue;
                    }

                    AVStream* newOutputStream =
                        ffmpeg.avformat_new_stream(output, null);

                    if (newOutputStream == null)
                        throw new Exception("Failed to create output stream.");

                    ret = ffmpeg.avcodec_parameters_copy(
                        newOutputStream->codecpar,
                        inputStream->codecpar);

                    if (ret < 0)
                        throw new Exception(
                            $"Failed to copy codec parameters: {GetError(ret)}");

                    newOutputStream->time_base =
                        inputStream->time_base;

                    streamMap[i] =
                        newOutputStream->index;
                }

                outputHandle = GCHandle.Alloc(outputStream);

                byte* outputBuffer =
                    (byte*)ffmpeg.av_malloc((ulong)bufferSize);

                AVIOContext* outputIO =
                    ffmpeg.avio_alloc_context(
                        outputBuffer,
                        bufferSize,
                        1,
                        (void*)GCHandle.ToIntPtr(outputHandle),
                        null,
                        writeCallback,
                        null);

                if (outputIO == null)
                    throw new Exception("Failed to create output AVIO context.");

                output->pb = outputIO;
                output->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

                ret = ffmpeg.avformat_write_header(
                    output,
                    null);

                if (ret < 0)
                    throw new Exception(
                        $"Failed to write MPEG-TS header: {GetError(ret)}");

                packet = ffmpeg.av_packet_alloc();

                if (packet == null)
                    throw new Exception("Failed to allocate AVPacket.");

                while ((ret = ffmpeg.av_read_frame(
                    input,
                    packet)) >= 0)
                {
                    int outputIndex =
                        streamMap[packet->stream_index];

                    if (outputIndex >= 0)
                    {
                        AVStream* inputStream =
                            input->streams[packet->stream_index];

                        AVStream* mappedOutputStream =
                            output->streams[outputIndex];

                        ffmpeg.av_packet_rescale_ts(
                            packet,
                            inputStream->time_base,
                            mappedOutputStream->time_base);

                        packet->stream_index =
                            outputIndex;

                        ret = ffmpeg.av_interleaved_write_frame(
                            output,
                            packet);

                        if (ret < 0)
                            throw new Exception(
                                $"Failed to write packet: {GetError(ret)}");
                    }

                    ffmpeg.av_packet_unref(packet);
                }

                ret = ffmpeg.av_write_trailer(output);

                if (ret < 0)
                    throw new Exception(
                        $"Failed to write MPEG-TS trailer: {GetError(ret)}");

                outputStream.Position = 0;

                return outputStream;
            }
            finally
            {
                if (packet != null)
                    ffmpeg.av_packet_free(&packet);

                if (input != null)
                    ffmpeg.avformat_close_input(&input);

                if (output != null)
                {
                    if (output->pb != null)
                        ffmpeg.avio_context_free(&output->pb);

                    ffmpeg.avformat_free_context(output);
                }

                if (inputHandle.IsAllocated)
                    inputHandle.Free();

                if (outputHandle.IsAllocated)
                    outputHandle.Free();
            }
        }

        private static unsafe int ReadInput(
            void* opaque,
            byte* buffer,
            int bufferSize)
        {
            Stream input =
                (Stream)GCHandle.FromIntPtr(
                    (IntPtr)opaque).Target!;

            try
            {
                int bytesRead = input.Read(
                    new Span<byte>(buffer, bufferSize));

                return bytesRead > 0
                    ? bytesRead
                    : ffmpeg.AVERROR_EOF;
            }
            catch
            {
                return ffmpeg.AVERROR_EOF;
            }
        }

        private static unsafe int WriteOutput(
            void* opaque,
            byte* buffer,
            int size)
        {
            Stream output =
                (Stream)GCHandle.FromIntPtr(
                    (IntPtr)opaque).Target!;

            try
            {
                output.Write(
                    new ReadOnlySpan<byte>(buffer, size));

                return size;
            }
            catch
            {
                return -1;
            }
        }

        private static unsafe string GetError(int error)
        {
            byte[] buffer = new byte[1024];

            fixed (byte* ptr = buffer)
            {
                ffmpeg.av_strerror(
                    error,
                    ptr,
                    (ulong)buffer.Length);
            }

            return System.Text.Encoding.UTF8
                .GetString(buffer)
                .TrimEnd('\0');
        }


    }
}
