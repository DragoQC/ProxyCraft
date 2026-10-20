using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ProxyCraft.Security;

internal static class TotpCodes
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string CreateSecret()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(20);
        char[] encoded = new char[32];
        int buffer = 0;
        int bits = 0;
        int index = 0;

        foreach (byte value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                encoded[index++] = Alphabet[(buffer >> bits) & 31];
            }
        }

        return new string(encoded);
    }

    public static long? Match(string secret, string code, DateTimeOffset now, long lastUsedStep)
    {
        if (code.Length != 6 || !code.All((char value) => value is >= '0' and <= '9'))
        {
            return null;
        }

        byte[] key = Decode(secret);
        long currentStep = now.ToUnixTimeSeconds() / 30;
        foreach (int offset in new int[] { 0, -1, 1 })
        {
            long step = currentStep + offset;
            string expected = CreateCode(key, step);
            if (step > lastUsedStep && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(code)))
            {
                return step;
            }
        }

        return null;
    }

    private static byte[] Decode(string secret)
    {
        byte[] decoded = new byte[secret.Length * 5 / 8];
        int buffer = 0;
        int bits = 0;
        int index = 0;

        foreach (char value in secret)
        {
            int digit = Alphabet.IndexOf(value);
            if (digit < 0)
            {
                throw new CryptographicException("Invalid authenticator secret.");
            }

            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                decoded[index++] = (byte)(buffer >> bits);
            }
        }

        return decoded;
    }

    private static string CreateCode(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        byte[] digest = HMACSHA1.HashData(key, counter);
        int offset = digest[^1] & 15;
        int value = BinaryPrimitives.ReadInt32BigEndian(digest.AsSpan(offset, 4)) & int.MaxValue;
        return (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }
}
