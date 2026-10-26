using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace ValDoctor.Core.Network;

public enum DnsRecordType : ushort
{
    A = 1,
    Cname = 5,
    Aaaa = 28,
}

public sealed record DnsResponse(int ResponseCode, IReadOnlyList<IPAddress> Addresses, IReadOnlyList<string> CnameChain, bool Truncated)
{
    public const int NoError = 0;
    public const int NameError = 3;
}

public sealed class DnsFormatException : Exception
{
    public DnsFormatException()
    {
    }

    public DnsFormatException(string message)
        : base(message)
    {
    }

    public DnsFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Minimal, defensive DNS message encoder/decoder for DNS-over-HTTPS (RFC 1035, RFC 8484).</summary>
public static class DnsWireFormat
{
    private const int MaxMessageSize = 65535;
    private const int MaxCnameHops = 10;

    /// <summary>Builds a recursive query with ID 0, as recommended for DoH caching (RFC 8484 §4.1).</summary>
    public static byte[] BuildQuery(string name, DnsRecordType type)
    {
        var labels = SplitName(name);
        var size = 12 + labels.Sum(l => l.Length + 1) + 1 + 4;
        var buffer = new byte[size];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0), 0);       // ID
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), 0x0100);  // RD
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4), 1);       // QDCOUNT
        var offset = 12;
        foreach (var label in labels)
        {
            buffer[offset++] = (byte)label.Length;
            label.CopyTo(buffer.AsSpan(offset));
            offset += label.Length;
        }

        buffer[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 2), 1); // IN
        return buffer;
    }

    /// <summary>
    /// Parses a response and returns the addresses of <paramref name="queryName"/>, following CNAMEs
    /// inside the answer section. Only records owned by the query name or its CNAME chain are accepted.
    /// </summary>
    public static DnsResponse ParseResponse(ReadOnlySpan<byte> message, string queryName, DnsRecordType type)
    {
        if (message.Length < 12 || message.Length > MaxMessageSize)
        {
            throw new DnsFormatException("DNS message has an invalid size.");
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        if ((flags & 0x8000) == 0)
        {
            throw new DnsFormatException("DNS message is not a response.");
        }

        var truncated = (flags & 0x0200) != 0;
        var rcode = flags & 0x000F;
        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var anCount = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);

        var offset = 12;
        for (var i = 0; i < qdCount; i++)
        {
            ReadName(message, ref offset);
            Ensure(message, offset, 4);
            offset += 4;
        }

        var cnames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var addresses = new List<(string Owner, IPAddress Address)>();
        for (var i = 0; i < anCount; i++)
        {
            var owner = ReadName(message, ref offset);
            Ensure(message, offset, 10);
            var rrType = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
            var rrClass = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 2)..]);
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 8)..]);
            offset += 10;
            Ensure(message, offset, rdLength);
            var rdataOffset = offset;
            offset += rdLength;

            if (rrClass != 1)
            {
                continue;
            }

            if (rrType == (ushort)DnsRecordType.A && type == DnsRecordType.A && rdLength == 4)
            {
                addresses.Add((owner, new IPAddress(message.Slice(rdataOffset, 4))));
            }
            else if (rrType == (ushort)DnsRecordType.Aaaa && type == DnsRecordType.Aaaa && rdLength == 16)
            {
                addresses.Add((owner, new IPAddress(message.Slice(rdataOffset, 16))));
            }
            else if (rrType == (ushort)DnsRecordType.Cname)
            {
                var cnameOffset = rdataOffset;
                var target = ReadName(message, ref cnameOffset);
                cnames.TryAdd(owner, target);
            }
        }

        var chain = new List<string> { Normalize(queryName) };
        while (chain.Count <= MaxCnameHops && cnames.TryGetValue(chain[^1], out var next) &&
               !chain.Contains(next, StringComparer.OrdinalIgnoreCase))
        {
            chain.Add(next);
        }

        var owners = new HashSet<string>(chain, StringComparer.OrdinalIgnoreCase);
        var result = addresses.Where(a => owners.Contains(a.Owner)).Select(a => a.Address).Distinct().ToList();
        return new DnsResponse(rcode, result, chain.Skip(1).ToList(), truncated);
    }

    private static List<byte[]> SplitName(string name)
    {
        var normalized = Normalize(name);
        if (normalized.Length is 0 or > 253)
        {
            throw new ArgumentException("Invalid DNS name.", nameof(name));
        }

        var labels = new List<byte[]>();
        foreach (var label in normalized.Split('.'))
        {
            if (label.Length is 0 or > 63 || !label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                throw new ArgumentException("Invalid DNS label.", nameof(name));
            }

            labels.Add(Encoding.ASCII.GetBytes(label));
        }

        return labels;
    }

    private static string Normalize(string name) => name.Trim().TrimEnd('.').ToLowerInvariant();

    private static string ReadName(ReadOnlySpan<byte> message, ref int offset)
    {
        var sb = new StringBuilder();
        var position = offset;
        var jumped = false;
        var jumps = 0;
        var length = 0;

        while (true)
        {
            Ensure(message, position, 1);
            var len = message[position];
            if (len == 0)
            {
                position++;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                Ensure(message, position, 2);
                var pointer = BinaryPrimitives.ReadUInt16BigEndian(message[position..]) & 0x3FFF;
                if (++jumps > 32 || pointer >= message.Length)
                {
                    throw new DnsFormatException("DNS name compression loop or invalid pointer.");
                }

                if (!jumped)
                {
                    offset = position + 2;
                    jumped = true;
                }

                position = pointer;
                continue;
            }

            if ((len & 0xC0) != 0)
            {
                throw new DnsFormatException("Unsupported DNS label type.");
            }

            Ensure(message, position + 1, len);
            length += len + 1;
            if (length > 255)
            {
                throw new DnsFormatException("DNS name is too long.");
            }

            if (sb.Length > 0)
            {
                sb.Append('.');
            }

            foreach (var b in message.Slice(position + 1, len))
            {
                sb.Append(b is >= 0x21 and < 0x7F ? (char)b : '?');
            }

            position += len + 1;
        }

        if (!jumped)
        {
            offset = position;
        }

        return sb.ToString().ToLowerInvariant();
    }

    private static void Ensure(ReadOnlySpan<byte> message, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > message.Length - count)
        {
            throw new DnsFormatException("DNS message is truncated.");
        }
    }
}
