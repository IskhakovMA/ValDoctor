using System.Buffers.Binary;
using System.Net;
using System.Text;
using RiotFix.Core.Network;

namespace RiotFix.Core.Tests;

public class DnsWireFormatTests
{
    [Fact]
    public void Builds_rfc8484_query()
    {
        var q = DnsWireFormat.BuildQuery("auth.riotgames.com", DnsRecordType.A);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(q)); // ID 0
        Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(2))); // RD
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(4)));
        Assert.Equal(4, q[12]);
        Assert.Equal("auth", Encoding.ASCII.GetString(q, 13, 4));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(q.Length - 4)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad..name")]
    [InlineData("spa ce.com")]
    public void Rejects_invalid_names(string name)
    {
        Assert.Throws<ArgumentException>(() => DnsWireFormat.BuildQuery(name, DnsRecordType.A));
    }

    [Fact]
    public void Parses_cname_chain_with_compression()
    {
        var msg = new ResponseBuilder("auth.riotgames.com", DnsRecordType.A)
            .Cname("auth.riotgames.com", "auth.riotgames.com.cdn.cloudflare.net")
            .A("auth.riotgames.com.cdn.cloudflare.net", "104.16.206.131")
            .A("auth.riotgames.com.cdn.cloudflare.net", "104.18.157.37")
            .Build();

        var r = DnsWireFormat.ParseResponse(msg, "auth.riotgames.com", DnsRecordType.A);

        Assert.Equal(0, r.ResponseCode);
        Assert.Equal([IPAddress.Parse("104.16.206.131"), IPAddress.Parse("104.18.157.37")], r.Addresses);
        Assert.Equal(["auth.riotgames.com.cdn.cloudflare.net"], r.CnameChain);
    }

    [Fact]
    public void Ignores_records_for_unrelated_owners()
    {
        var msg = new ResponseBuilder("auth.riotgames.com", DnsRecordType.A)
            .A("evil.example", "6.6.6.6")
            .A("auth.riotgames.com", "104.16.206.131")
            .Build();

        var r = DnsWireFormat.ParseResponse(msg, "auth.riotgames.com", DnsRecordType.A);
        Assert.Equal([IPAddress.Parse("104.16.206.131")], r.Addresses);
    }

    [Fact]
    public void Parses_aaaa()
    {
        var msg = new ResponseBuilder("auth.riotgames.com", DnsRecordType.Aaaa)
            .Aaaa("auth.riotgames.com", "2606:4700::6810:ce83")
            .Build();
        var r = DnsWireFormat.ParseResponse(msg, "auth.riotgames.com", DnsRecordType.Aaaa);
        Assert.Equal([IPAddress.Parse("2606:4700::6810:ce83")], r.Addresses);
    }

    [Fact]
    public void Reports_nxdomain()
    {
        var msg = new ResponseBuilder("nope.riotgames.com", DnsRecordType.A, rcode: 3).Build();
        var r = DnsWireFormat.ParseResponse(msg, "nope.riotgames.com", DnsRecordType.A);
        Assert.Equal(DnsResponse.NameError, r.ResponseCode);
        Assert.Empty(r.Addresses);
    }

    [Fact]
    public void Rejects_queries_and_truncated_messages()
    {
        var query = DnsWireFormat.BuildQuery("auth.riotgames.com", DnsRecordType.A);
        Assert.Throws<DnsFormatException>(() => DnsWireFormat.ParseResponse(query, "auth.riotgames.com", DnsRecordType.A));

        var msg = new ResponseBuilder("auth.riotgames.com", DnsRecordType.A).A("auth.riotgames.com", "104.16.206.131").Build();
        Assert.Throws<DnsFormatException>(() => DnsWireFormat.ParseResponse(msg.AsSpan(0, msg.Length - 2), "auth.riotgames.com", DnsRecordType.A));
        Assert.Throws<DnsFormatException>(() => DnsWireFormat.ParseResponse(new byte[5], "x", DnsRecordType.A));
    }

    [Fact]
    public void Rejects_compression_pointer_loops()
    {
        // Header (response, 1 answer, 0 questions) + answer name = pointer to itself.
        byte[] msg = [0, 0, 0x81, 0x80, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 12];
        Assert.Throws<DnsFormatException>(() => DnsWireFormat.ParseResponse(msg, "a.b", DnsRecordType.A));
    }

    [Fact]
    public void Random_garbage_only_ever_throws_dns_format_exception()
    {
        var rng = new Random(12345);
        for (var i = 0; i < 2000; i++)
        {
            var buf = new byte[rng.Next(12, 200)];
            rng.NextBytes(buf);
            buf[2] |= 0x80; // mark as response so the parser goes deeper
            try
            {
                DnsWireFormat.ParseResponse(buf, "auth.riotgames.com", DnsRecordType.A);
            }
            catch (DnsFormatException)
            {
            }
        }
    }

    /// <summary>Builds DNS responses with name compression for the question name.</summary>
    private sealed class ResponseBuilder
    {
        private readonly List<byte> _answers = [];
        private readonly byte[] _question;
        private readonly int _rcode;
        private int _count;

        public ResponseBuilder(string qname, DnsRecordType type, int rcode = 0)
        {
            var q = DnsWireFormat.BuildQuery(qname, type);
            _question = q[12..];
            _rcode = rcode;
        }

        public ResponseBuilder A(string owner, string ip) => Record(owner, 1, IPAddress.Parse(ip).GetAddressBytes());

        public ResponseBuilder Aaaa(string owner, string ip) => Record(owner, 28, IPAddress.Parse(ip).GetAddressBytes());

        public ResponseBuilder Cname(string owner, string target) => Record(owner, 5, EncodeName(target));

        public byte[] Build()
        {
            var header = new byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)(0x8180 | _rcode));
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)_count);
            return [.. header, .. _question, .. _answers];
        }

        private ResponseBuilder Record(string owner, ushort type, byte[] rdata)
        {
            // Use a compression pointer to the question name when the owner matches it.
            var questionName = Encoding.ASCII.GetString(_question, 0, _question.Length - 4);
            var encodedOwner = EncodeName(owner);
            if (Encoding.ASCII.GetString(encodedOwner) == questionName)
            {
                _answers.AddRange([0xC0, 12]);
            }
            else
            {
                _answers.AddRange(encodedOwner);
            }

            var fixedPart = new byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart, type);
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt32BigEndian(fixedPart.AsSpan(4), 300);
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(8), (ushort)rdata.Length);
            _answers.AddRange(fixedPart);
            _answers.AddRange(rdata);
            _count++;
            return this;
        }

        private static byte[] EncodeName(string name)
        {
            var bytes = new List<byte>();
            foreach (var label in name.Split('.'))
            {
                bytes.Add((byte)label.Length);
                bytes.AddRange(Encoding.ASCII.GetBytes(label));
            }

            bytes.Add(0);
            return [.. bytes];
        }
    }
}
