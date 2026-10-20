using System.Net.Sockets;
using System.Text;

namespace ProxyCraft.Services;

internal sealed record MinecraftLoginStart(
    string PlayerName,
    byte[] Packet)
{
    public static async Task<MinecraftLoginStart> ReadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] packet = await MinecraftHandshake.ReadPacketAsync(stream, cancellationToken);
        int offset = 0;
        MinecraftHandshake.ReadVarInt(packet, ref offset); // Packet length prefix.
        if (MinecraftHandshake.ReadVarInt(packet, ref offset) != 0)
        {
            throw new InvalidDataException("Expected a login start packet.");
        }

        int nameLength = MinecraftHandshake.ReadVarInt(packet, ref offset);
        if (nameLength is < 1 or > 16 || nameLength > packet.Length - offset)
        {
            throw new InvalidDataException("Invalid player name length.");
        }

        for (int index = offset; index < offset + nameLength; index++)
        {
            byte character = packet[index];
            if (character is not (>= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'_'))
            {
                throw new InvalidDataException("Invalid player name.");
            }
        }
        // UUID/profile fields vary by version. Preserve the entire packet instead of rewriting them.
        return new MinecraftLoginStart(Encoding.ASCII.GetString(packet, offset, nameLength), packet);
    }
}
