using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace EzGetBmcIp
{
    internal static class ProcessOutputDecoder
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static async Task<byte[]> ReadAllBytesAsync(Stream stream)
        {
            using (var buffer = new MemoryStream())
            {
                await stream.CopyToAsync(buffer);
                return buffer.ToArray();
            }
        }

        public static string Decode(byte[] bytes, Encoding fallbackEncoding)
        {
            if (bytes.Length == 0)
                return string.Empty;

            var offset = bytes.Length >= 3
                && bytes[0] == 0xEF
                && bytes[1] == 0xBB
                && bytes[2] == 0xBF
                    ? 3
                    : 0;

            try
            {
                return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
            }
            catch (DecoderFallbackException)
            {
                return fallbackEncoding.GetString(bytes, offset, bytes.Length - offset);
            }
        }
    }
}
