using System.IO;

namespace SennheiserTray
{
    public readonly record struct GaiaPacketHeader(
        ushort PayloadLength,
        ushort VendorId,
        ushort CommandId);

    public static class GaiaPacketCodec
    {
        public const int HeaderLength = 8;

        public static byte[] BuildPacket(ushort vendorId, ushort commandId, ReadOnlySpan<byte> payload)
        {
            if (payload.Length > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(payload),
                    "GAIA payloads cannot exceed 65535 bytes.");
            }

            var packet = new byte[HeaderLength + payload.Length];
            packet[0] = 0xFF;
            packet[1] = 0x03;
            packet[2] = (byte)(payload.Length >> 8);
            packet[3] = (byte)payload.Length;
            packet[4] = (byte)(vendorId >> 8);
            packet[5] = (byte)vendorId;
            packet[6] = (byte)(commandId >> 8);
            packet[7] = (byte)commandId;
            payload.CopyTo(packet.AsSpan(HeaderLength));
            return packet;
        }

        public static GaiaPacketHeader ParseHeader(ReadOnlySpan<byte> header)
        {
            if (header.Length < HeaderLength)
            {
                throw new ArgumentException("A GAIA frame header must contain 8 bytes.", nameof(header));
            }

            if (header[0] != 0xFF || header[1] != 0x03)
            {
                throw new InvalidDataException("Received an invalid GAIA frame header.");
            }

            return new GaiaPacketHeader(
                (ushort)((header[2] << 8) | header[3]),
                (ushort)((header[4] << 8) | header[5]),
                (ushort)((header[6] << 8) | header[7]));
        }
    }
}
