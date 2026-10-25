using System.Text;
using Ledon.Protocols.Sip;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>SIP 消息与 SDP 解析。</summary>
public class SipTests
{
    private const string Invite = """
        INVITE sip:bob@example.com SIP/2.0
        Via: SIP/2.0/UDP pc.example.com;branch=z9hG4bK776asdhds
        Max-Forwards: 70
        From: Alice <sip:alice@example.com>;tag=1928301774
        To: Bob <sip:bob@example.com>
        Call-ID: a84b4c76e66710@pc.example.com
        CSeq: 314159 INVITE
        Contact: <sip:alice@pc.example.com>
        Content-Type: application/sdp
        Content-Length: 142

        v=0
        o=alice 2890844526 2890844526 IN IP4 pc.example.com
        s=-
        c=IN IP4 pc.example.com
        t=0 0
        m=audio 49170 RTP/AVP 0 101
        a=rtpmap:0 PCMU/8000
        a=rtpmap:101 telephone-event/8000
        a=sendrecv
        """;

    [Fact]
    public void Parses_request_line_and_method()
    {
        var message = SipParser.Parse(Invite);

        Assert.Equal(SipMessageType.Request, message.Type);
        Assert.Equal("INVITE", message.Method);
        Assert.Equal("sip:bob@example.com", message.RequestUri);
        Assert.Equal("SIP/2.0", message.Version);
    }

    [Fact]
    public void Parses_key_headers_case_insensitively()
    {
        var message = SipParser.Parse(Invite);

        Assert.Equal("a84b4c76e66710@pc.example.com", message.CallId);
        Assert.Equal("314159 INVITE", message.CSeq);
        Assert.Contains("alice@example.com", message.From!);
        Assert.Contains("bob@example.com", message.To!);
        Assert.Contains("z9hG4bK776asdhds", message.Via!);
    }

    [Fact]
    public void Header_lookup_is_case_insensitive()
    {
        var message = SipParser.Parse(Invite);

        Assert.Equal(message.Header("call-id"), message.Header("Call-ID"));
        Assert.NotNull(message.Header("CONTENT-TYPE"));
    }

    [Fact]
    public void Parses_sdp_body()
    {
        var message = SipParser.Parse(Invite);
        var sdp = SdpParser.Parse(message.Body);

        Assert.NotNull(sdp);
        Assert.Equal("-", sdp!.SessionName);
        Assert.Equal("IN IP4 pc.example.com", sdp.Connection);
        var audio = Assert.Single(sdp.Media);
        Assert.Equal("audio", audio.Media);
        Assert.Equal(49170, audio.Port);
        Assert.Equal("RTP/AVP", audio.Protocol);
        Assert.Equal(["0", "101"], audio.Formats);
        Assert.Contains("rtpmap:0 PCMU/8000", audio.Attributes);   // 属性值不含 "a=" 前缀
    }

    [Fact]
    public void Serialization_roundtrips_with_crlf()
    {
        var message = SipParser.Parse(Invite);
        var bytes = message.ToBytes();
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("\r\n", text);
        Assert.DoesNotContain("\n\n", text.Replace("\r\n", ""));

        var reparsed = SipParser.Parse(text);
        Assert.Equal("INVITE", reparsed.Method);
        Assert.Equal(message.CallId, reparsed.CallId);
        Assert.Equal(message.Body?.Trim(), reparsed.Body?.Trim());
    }

    [Fact]
    public void Parses_response_status_line()
    {
        const string ok = """
            SIP/2.0 200 OK
            Via: SIP/2.0/UDP pc.example.com;branch=z9hG4bK776asdhds
            From: Alice <sip:alice@example.com>;tag=1928301774
            To: Bob <sip:bob@example.com>;tag=a73kszlfl
            Call-ID: a84b4c76e66710@pc.example.com
            CSeq: 314159 INVITE
            Content-Length: 0

            """;

        var message = SipParser.Parse(ok);

        Assert.Equal(SipMessageType.Response, message.Type);
        Assert.Equal(200, message.StatusCode);
        Assert.Equal("OK", message.ReasonPhrase);
        Assert.Null(message.Method);
    }

    [Fact]
    public void Merges_multi_valued_headers()
    {
        const string withMultipleVia = """
            OPTIONS sip:bob@example.com SIP/2.0
            Via: SIP/2.0/UDP first.example.com
            Via: SIP/2.0/UDP second.example.com
            Call-ID: x@y
            CSeq: 1 OPTIONS

            """;

        var message = SipParser.Parse(withMultipleVia);

        Assert.Contains("first.example.com", message.Via!);
        Assert.Contains("second.example.com", message.Via!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage-line-without-colons")]
    public void Rejects_malformed_messages(string text)
    {
        Assert.Throws<SipParseException>(() => SipParser.Parse(text));
    }

    [Fact]
    public void Rejects_malformed_header_line()
    {
        const string bad = """
            INVITE sip:bob@example.com SIP/2.0
            this-is-not-a-header
            Call-ID: x@y

            """;

        Assert.Throws<SipParseException>(() => SipParser.Parse(bad));
    }

    [Fact]
    public async Task Responder_answers_options_with_200_ok()
    {
        var responder = new SipResponder(new LedonSipOptions { AutoAnswerOptions = true });
        var request = SipParser.Parse("""
            OPTIONS sip:bob@example.com SIP/2.0
            Via: SIP/2.0/UDP pc.example.com;branch=z9hG4bK1
            From: Alice <sip:alice@example.com>;tag=1
            To: Bob <sip:bob@example.com>
            Call-ID: abc@pc
            CSeq: 1 OPTIONS

            """);

        var response = await responder.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(200, response!.StatusCode);
        Assert.Equal("OK", response.ReasonPhrase);
        Assert.Equal("abc@pc", response.CallId);
        Assert.Equal("1 OPTIONS", response.CSeq);
        Assert.Equal("Ledon/1.0", response.Header("User-Agent"));
    }

    [Fact]
    public async Task Responder_stays_silent_for_invite_by_default()
    {
        var responder = new SipResponder(new LedonSipOptions());

        var response = await responder.HandleAsync(SipParser.Parse(Invite), CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task Responder_ignores_responses()
    {
        var responder = new SipResponder(new LedonSipOptions { AutoAnswerOptions = true });
        var response = await responder.HandleAsync(
            SipParser.CreateResponse(200, "OK", new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.Null(response);
    }
}
