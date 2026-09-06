using System.Buffers.Binary;
using System.Text.Json;
using PriceSentinel3000.Application.Automation;

namespace PriceSentinel3000.Infrastructure.Automation;

internal static class AutomationPipeFrame
{
    internal const int MaximumBytes = 1024 * 1024;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumBytes)
        {
            throw new InvalidDataException($"Automation messages must contain 1 to {MaximumBytes} bytes.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, AutomationProtocol.JsonOptions)
            ?? throw new JsonException("The automation message must not be null.");
    }

    internal static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, AutomationProtocol.JsonOptions);
        if (payload.Length > MaximumBytes)
        {
            throw new InvalidDataException($"Automation messages must not exceed {MaximumBytes} bytes.");
        }

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
