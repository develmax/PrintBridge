using System.Buffers.Binary;
using System.Text;

namespace PrintBridge;

internal static class IppMessage
{
    private const int MaxAttributesBytes = 256 * 1024;
    private static readonly HashSet<string> EndpointAttributes = new(StringComparer.Ordinal)
    {
        "printer-uri", "printer-uri-supported", "job-printer-uri", "job-uri",
        "notify-printer-uri", "notify-job-uri"
    };

    public static async Task<(byte[] Attributes, int OriginalLength)> RewriteAsync(
        Stream source, Func<Uri, Uri?> map, CancellationToken token)
    {
        using var output = new MemoryStream();
        byte[] header = new byte[8];
        await source.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (header[0] is < 1 or > 2)
            throw new InvalidDataException("Invalid IPP message version.");
        output.Write(header);

        string? name = null;
        var tag = new byte[1];
        int read = header.Length;
        while (true)
        {
            await source.ReadExactlyAsync(tag, token).ConfigureAwait(false);
            if (++read > MaxAttributesBytes)
                throw new InvalidDataException("IPP attributes exceed the size limit.");
            output.WriteByte(tag[0]);
            if (tag[0] == 3)
                return (output.ToArray(), read);
            if (tag[0] < 0x10)
            {
                name = null;
                continue;
            }

            // Extended value tags carry a four-byte identifier before the attribute name.
            if (tag[0] == 0x7f)
            {
                var identifier = new byte[4];
                await source.ReadExactlyAsync(identifier, token).ConfigureAwait(false);
                output.Write(identifier);
                read += identifier.Length;
            }

            byte[] attributeName = await ReadFieldAsync(source, token).ConfigureAwait(false);
            byte[] value = await ReadFieldAsync(source, token).ConfigureAwait(false);
            read += attributeName.Length + value.Length + 4;
            if (read > MaxAttributesBytes)
                throw new InvalidDataException("IPP attributes exceed the size limit.");
            if (attributeName.Length != 0)
                name = Encoding.ASCII.GetString(attributeName);
            if (tag[0] == 0x45 && name != null && EndpointAttributes.Contains(name) &&
                Uri.TryCreate(Encoding.UTF8.GetString(value), UriKind.Absolute, out var uri) &&
                uri.Scheme is "ipp" or "ipps" && map(uri) is { } rewritten)
            {
                value = Encoding.UTF8.GetBytes(rewritten.AbsoluteUri);
            }
            WriteField(output, attributeName);
            WriteField(output, value);
            if (output.Length > MaxAttributesBytes)
                throw new InvalidDataException("Rewritten IPP attributes exceed the size limit.");
        }
    }

    private static async Task<byte[]> ReadFieldAsync(Stream source, CancellationToken token)
    {
        byte[] length = new byte[2];
        await source.ReadExactlyAsync(length, token).ConfigureAwait(false);
        byte[] data = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
        await source.ReadExactlyAsync(data, token).ConfigureAwait(false);
        return data;
    }

    private static void WriteField(Stream output, byte[] value)
    {
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)value.Length));
        output.Write(length);
        output.Write(value);
    }
}
