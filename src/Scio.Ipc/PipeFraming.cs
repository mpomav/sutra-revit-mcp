using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Scio.Ipc
{
    /// <summary>
    /// Length-prefixed (4-byte little-endian) UTF-8 JSON framing over bidirectional streams.
    /// </summary>
    public static class PipeFraming
    {
        public const int MaxFrameBytes = 64 * 1024 * 1024; // 64 MB

        public static async Task WriteMessageAsync<T>(Stream stream, T payload, CancellationToken ct)
        {
            string json = JsonConvert.SerializeObject(payload, Formatting.None);
            byte[] body = Encoding.UTF8.GetBytes(json);
            if (body.Length > MaxFrameBytes)
                throw new InvalidOperationException($"IPC payload exceeds {MaxFrameBytes} bytes.");

            byte[] header = BitConverter.GetBytes(body.Length);
            if (!BitConverter.IsLittleEndian) Array.Reverse(header);

            await stream.WriteAsync(header, 0, 4, ct).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public static async Task<T?> ReadMessageAsync<T>(Stream stream, CancellationToken ct) where T : class
        {
            byte[] header = new byte[4];
            bool gotHeader = await ReadExactAsync(stream, header, 4, ct).ConfigureAwait(false);
            if (!gotHeader) return null;

            if (!BitConverter.IsLittleEndian) Array.Reverse(header);
            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > MaxFrameBytes)
                throw new InvalidDataException($"Invalid IPC frame length: {length}.");

            byte[] body = new byte[length];
            bool gotBody = await ReadExactAsync(stream, body, length, ct).ConfigureAwait(false);
            if (!gotBody)
                throw new EndOfStreamException("Unexpected EOF while reading IPC frame body.");

            string json = Encoding.UTF8.GetString(body);
            return JsonConvert.DeserializeObject<T>(json);
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    if (offset == 0) return false;
                    throw new EndOfStreamException("Connection closed mid-frame.");
                }
                offset += read;
            }
            return true;
        }
    }
}
