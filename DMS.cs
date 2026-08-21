using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace USMHandler
{
    public class DynamicMemoryStream : Stream
    {
        private readonly MemoryStream _memoryStream;
        private long _position = 0;

        public DynamicMemoryStream(MemoryStream memoryStream)
        {
            _memoryStream = memoryStream;
        }

        public override long Length => _memoryStream.Length;

        public override long Position
        {
            get => _position;
            set
            {
                lock (_memoryStream)
                {
                    _position = value;
                    _memoryStream.Position = value;
                }
            }
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;  // Prevent writing inside VLC

        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = 0;

            while (bytesRead < count)
            {
                lock (_memoryStream)  // Ensure thread safety
                {
                    if (_position < _memoryStream.Length)
                    {
                        _memoryStream.Position = _position;
                        int chunkSize = (int)Math.Min(count - bytesRead, _memoryStream.Length - _position);
                        int bytesReadThisTime = _memoryStream.Read(buffer, offset + bytesRead, chunkSize);
                        bytesRead += bytesReadThisTime;
                        _position += bytesReadThisTime;
                    }
                    else
                    {
                        break; // Stop if there’s no more data yet
                    }
                }
            }

            return bytesRead;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (_memoryStream)
            {
                long newPosition;

                switch (origin)
                {
                    case SeekOrigin.Begin:
                        newPosition = offset;
                        break;

                    case SeekOrigin.Current:
                        newPosition = _position + offset;
                        break;

                    case SeekOrigin.End:
                        newPosition = _memoryStream.Length + offset;
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(origin));
                }

                newPosition = Math.Clamp(
                    newPosition,
                    0,
                    _memoryStream.Length
                );

                _position = newPosition;
                _memoryStream.Position = newPosition;

                return _position;
            }
        }



        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }


}
