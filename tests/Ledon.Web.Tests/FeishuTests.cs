using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ledon.Notifications.Feishu;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>飞书：签名、解密、事件回调（URL 校验/Token/分发）。</summary>
public class FeishuTests
{
    [Fact]
    public void Webhook_signature_matches_documented_algorithm()
    {
        // 飞书文档算法：key = "{timestamp}\n{secret}"，对空消息做 HMAC-SHA256 后 Base64
        const long timestamp = 1700000000;
        const string secret = "test-secret";

        var sign = FeishuCrypto.SignWebhook(timestamp, secret);

        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes($"{timestamp}\n{secret}"));
        var expected = Convert.ToBase64String(hmac.ComputeHash([]));

        Assert.Equal(expected, sign);
    }

    [Fact]
    public void Webhook_signature_is_stable_and_key_dependent()
    {
        var first = FeishuCrypto.SignWebhook(1700000000, "s1");
        var second = FeishuCrypto.SignWebhook(1700000000, "s1");
        var other = FeishuCrypto.SignWebhook(1700000000, "s2");

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void Event_decrypt_roundtrip()
    {
        // 按飞书算法加密：key = SHA256(encryptKey)，AES-256-CBC，IV 随机并前置
        const string encryptKey = "test-encrypt-key";
        const string plain = "{\"schema\":\"2.0\",\"header\":{\"event_type\":\"im.message.receive_v1\"}}";

        var key = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(encryptKey));
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.Mode = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(Encoding.UTF8.GetBytes(plain), 0, plain.Length);
        var encrypt = Convert.ToBase64String(aes.IV.Concat(cipher).ToArray());

        var decrypted = FeishuCrypto.DecryptEvent(encryptKey, encrypt);

        Assert.Equal(plain, decrypted);
    }

    [Fact]
    public void Decrypt_rejects_malformed_payload()
    {
        Assert.Throws<FormatException>(() => FeishuCrypto.DecryptEvent("key", Convert.ToBase64String([1, 2, 3])));
    }

    [Fact]
    public async Task Url_verification_is_answered_with_challenge()
    {
        var app = BuildApp(o => o.VerificationToken = "tok");
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.PostAsJsonAsync("/feishu/events", new
        {
            type = "url_verification",
            challenge = "abc-123",
            token = "tok",
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("abc-123", body);

        await app.StopAsync();
    }

    [Fact]
    public async Task Url_verification_with_wrong_token_is_rejected()
    {
        var app = BuildApp(o => o.VerificationToken = "tok");
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.PostAsJsonAsync("/feishu/events", new
        {
            type = "url_verification",
            challenge = "abc",
            token = "WRONG",
        });

        Assert.Equal(StatusCodes.Status401Unauthorized, (int)response.StatusCode);

        await app.StopAsync();
    }

    [Fact]
    public async Task Event_is_dispatched_to_handlers()
    {
        var recorder = new FeishuRecorder();
        var app = BuildApp(o => o.VerificationToken = "tok", recorder);
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.PostAsJsonAsync("/feishu/events", new
        {
            schema = "2.0",
            header = new { event_type = "im.message.receive_v1", token = "tok" },
            @event = new { message = new { message_id = "m-1", message_type = "text" } },
        });

        // 飞书按 200 判定投递成功：处理器异常也不能让回调失败
        response.EnsureSuccessStatusCode();
        Assert.Equal("im.message.receive_v1", recorder.LastEventType);
        Assert.Equal("m-1", recorder.LastMessageId);

        await app.StopAsync();
    }

    [Fact]
    public async Task Handler_failure_does_not_fail_the_callback()
    {
        var app = BuildApp(o => o.VerificationToken = "tok", new ThrowingHandler());
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.PostAsJsonAsync("/feishu/events", new
        {
            header = new { event_type = "any", token = "tok" },
            @event = new { },
        });

        response.EnsureSuccessStatusCode();

        await app.StopAsync();
    }

    [Fact]
    public async Task Encrypted_callback_is_decrypted_before_dispatch()
    {
        const string encryptKey = "enc-key";
        const string plain = "{\"header\":{\"event_type\":\"im.message.receive_v1\",\"token\":\"tok\"},\"event\":{\"message\":{\"message_id\":\"m-9\"}}}";

        var key = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(encryptKey));
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.Mode = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(Encoding.UTF8.GetBytes(plain), 0, plain.Length);
        var encrypt = Convert.ToBase64String(aes.IV.Concat(cipher).ToArray());

        var recorder = new FeishuRecorder();
        var app = BuildApp(o => { o.EncryptKey = encryptKey; o.VerificationToken = "tok"; }, recorder);
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.PostAsJsonAsync("/feishu/events", new { encrypt });

        response.EnsureSuccessStatusCode();
        Assert.Equal("m-9", recorder.LastMessageId);

        await app.StopAsync();
    }

    [Fact]
    public async Task Message_event_deserializes_to_typed_shape()
    {
        var recorder = new FeishuRecorder();
        var app = BuildApp(o => o.VerificationToken = "tok", recorder);
        await app.StartAsync();

        var client = app.GetTestClient();
        await client.PostAsJsonAsync("/feishu/events", new
        {
            header = new { event_type = "im.message.receive_v1", token = "tok" },
                @event = new
            {
                sender = new { sender_id = new { open_id = "ou_1" } },
                message = new { message_id = "m-2", chat_id = "oc_1", message_type = "text" },
            },
        });

        Assert.Equal("ou_1", recorder.LastOpenId);
        Assert.Equal("oc_1", recorder.LastChatId);

        await app.StopAsync();
    }

    private static WebApplication BuildApp(Action<FeishuOptions> configure, params IFeishuEventHandler[] handlers)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();
        builder.Services.AddLedonFeishu(configure);

        foreach (var handler in handlers)
            builder.Services.AddSingleton(handler);

        var app = builder.Build();
        app.UseLedonWeb();

        return app;
    }

    private sealed class FeishuRecorder : IFeishuEventHandler
    {
        public string? LastEventType { get; private set; }
        public string? LastMessageId { get; private set; }
        public string? LastOpenId { get; private set; }
        public string? LastChatId { get; private set; }

        public Task HandleAsync(FeishuEvent @event, CancellationToken cancellationToken = default)
        {
            LastEventType = @event.EventType;

            if (@event.EventType == "im.message.receive_v1")
            {
                var message = @event.Event.Deserialize<FeishuMessageEvent>();
                LastMessageId = message?.Message?.MessageId;
                LastOpenId = message?.Sender?.SenderId?.OpenId;
                LastChatId = message?.Message?.ChatId;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : IFeishuEventHandler
    {
        public Task HandleAsync(FeishuEvent @event, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }
}
