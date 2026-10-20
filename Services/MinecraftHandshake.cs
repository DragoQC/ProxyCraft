using System.Net.Sockets;
using System.Text;

namespace ProxyCraft.Services;

// The original packet is preserved, including Forge metadata, for unchanged forwarding.
internal sealed record MinecraftHandshake(
    string Domain,
    int NextState,
    byte[] Packet)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<MinecraftHandshake> ReadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] packet = await ReadPacketAsync(stream, cancellationToken);
        int offset = 0;
        ReadVarInt(packet, ref offset);
        string domain = ReadDomain(packet.AsSpan(offset), out int nextState);
        return new MinecraftHandshake(domain, nextState, packet);
    }

    internal static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[3];
        int length = 0;
        int prefixLength = 0;
        do
        {
            if (prefixLength == prefix.Length)
            {
                throw new InvalidDataException("Invalid packet length.");
            }

            await stream.ReadExactlyAsync(prefix.AsMemory(prefixLength, 1), cancellationToken);
            length |= (prefix[prefixLength] & 0x7f) << (prefixLength * 7);
            prefixLength++;
        } while ((prefix[prefixLength - 1] & 0x80) != 0);

        if (length is < 1 or > 8192)
        {
            throw new InvalidDataException("Invalid packet length.");
        }

        byte[] packet = new byte[prefixLength + length];
        prefix.AsSpan(0, prefixLength).CopyTo(packet);
        await stream.ReadExactlyAsync(packet.AsMemory(prefixLength), cancellationToken);
        return packet;
    }

    private static string ReadDomain(ReadOnlySpan<byte> packet, out int nextState)
    {
        int offset = 0;
        if (ReadVarInt(packet, ref offset) != 0)
        {
            throw new InvalidDataException("Expected a handshake.");
        }

        ReadVarInt(packet, ref offset); // Protocol version, including -1 for server-list queries.
        int addressLength = ReadVarInt(packet, ref offset);
        if (addressLength is < 1 or > 1020 || addressLength > packet.Length - offset)
        {
            throw new InvalidDataException("Invalid server address.");
        }

        string address = Utf8.GetString(packet.Slice(offset, addressLength));
        if (address.Length > 255)
        {
            throw new InvalidDataException("Server address is too long.");
        }

        offset += addressLength;
        if (packet.Length - offset < 3)
        {
            throw new InvalidDataException("Incomplete handshake.");
        }

        offset += 2; // Unsigned server port.
        nextState = ReadVarInt(packet, ref offset);
        if (nextState is < 1 or > 3 || offset != packet.Length)
        {
            throw new InvalidDataException("Invalid handshake state.");
        }

        // Forge may append NUL-separated metadata. Keep it in the forwarded packet, not in the lookup key.
        int metadata = address.IndexOf('\0');
        return (metadata >= 0 ? address[..metadata] : address).TrimEnd('.').ToLowerInvariant();
    }

    internal static int ReadVarInt(ReadOnlySpan<byte> packet, ref int offset)
    {
        int value = 0;
        for (int index = 0; index < 5; index++)
        {
            if (offset >= packet.Length)
            {
                throw new InvalidDataException("Incomplete VarInt.");
            }

            byte current = packet[offset++];
            if (index == 4 && (current & 0xf0) != 0)
            {
                throw new InvalidDataException("Invalid VarInt.");
            }

            value |= (current & 0x7f) << (index * 7);
            if ((current & 0x80) == 0)
            {
                return value;
            }
        }
        throw new InvalidDataException("VarInt is too long.");
    }
}
