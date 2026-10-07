using Windows.Storage.Streams;

namespace Domestique.Ble;

internal static class BufferExtensions
{
    public static byte[] ToBytes(this IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(bytes);
        return bytes;
    }
}