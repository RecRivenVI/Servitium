using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;

using Servitium.Client;
using Servitium.Contracts.Identity;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class RealtimeClientTests
{
    [Fact]
    public async Task 普通账户登录和切换后不订阅全局且被拒绝的范围不会污染重连()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "servitium-realtime-" + Guid.NewGuid().ToString("N"));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using WebApplicationFactory<Program> baseFactory = new();
        using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(builder =>
        {
            _ = builder.UseSetting("Servitium:DataRoot", dataRoot);
            _ = builder.UseSetting("Servitium:LocalAuthenticationEnabled", "false");
        });
        factory.UseKestrel(0);
        using HttpClient readiness = factory.CreateClient();
        using ServitiumApiClient client = new(readiness.BaseAddress!);
        await client.Session.InitializeAsync(new("owner", "Owner", "correct-horse-battery-staple", "zh-Hans", null), timeout.Token).ConfigureAwait(true);
        _ = await client.Session.SignInAsync(new("owner", "correct-horse-battery-staple"), timeout.Token).ConfigureAwait(true);
        await client.Events.StartAsync(timeout.Token).ConfigureAwait(true);
        await client.Events.SubscribeAsync("global", timeout.Token).ConfigureAwait(true);
        _ = await client.CreateUserAsync(new("operator", "Operator", "operator-strong-password", "User"), timeout.Token).ConfigureAwait(true);
        _ = await client.GetSystemInfoAsync(DateTimeOffset.UtcNow, timeout.Token).ConfigureAwait(true);

        await client.Session.SignOutAsync(timeout.Token).ConfigureAwait(true);
        _ = await client.Session.SignInAsync(new("operator", "operator-strong-password"), timeout.Token).ConfigureAwait(true);
        await client.Events.StartAsync(timeout.Token).ConfigureAwait(true);
        await Assert.ThrowsAsync<HubException>(() => client.Events.SubscribeAsync("global", timeout.Token)).ConfigureAwait(true);
        await Assert.ThrowsAsync<HubException>(() => client.Events.SubscribeAsync("global", timeout.Token)).ConfigureAwait(true);
        await Assert.ThrowsAsync<HubException>(() => client.Events.SubscribeAsync("instance:" + Guid.NewGuid().ToString("D"), timeout.Token)).ConfigureAwait(true);
        await client.Events.StopAsync(timeout.Token).ConfigureAwait(true);
        await client.Events.StartAsync(timeout.Token).ConfigureAwait(true);
        ServitiumApiException forbidden = await Assert.ThrowsAsync<ServitiumApiException>(() => client.GetSettingsAsync(timeout.Token)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("User", client.Session.Principal!.User.Role);
        Assert.Empty(client.Session.Principal.GrantedInstanceIds);

        TotpSetupDto setup = await client.BeginTotpAsync(timeout.Token).ConfigureAwait(true);
        _ = await client.ConfirmTotpAsync(new(Code(setup.Secret)), timeout.Token).ConfigureAwait(true);
        await client.Events.StopAsync(timeout.Token).ConfigureAwait(true);
        Assert.True((await client.Session.SignInAsync(new("operator", "operator-strong-password"), timeout.Token).ConfigureAwait(true)).TotpRequired);
        _ = await client.Session.CompleteTotpAsync(Code(setup.Secret), timeout.Token).ConfigureAwait(true);
        await client.Events.StartAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal("User", client.Session.Principal!.User.Role);
        await Assert.ThrowsAsync<HubException>(() => client.Events.SubscribeAsync("global", timeout.Token)).ConfigureAwait(true);

        TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Events.AuthorizationChanged += (_, _) => changed.TrySetResult();
        using ServitiumApiClient administrator = new(readiness.BaseAddress!);
        _ = await administrator.Session.SignInAsync(new("owner", "correct-horse-battery-staple"), timeout.Token).ConfigureAwait(true);
        _ = await administrator.UpdateUserAsync(client.Session.Principal.User.Id, new("Updated", "User"), timeout.Token).ConfigureAwait(true);
        await changed.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        await client.Events.StopAsync(timeout.Token).ConfigureAwait(true);
    }

    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "The protocol regression supplies an RFC 6238 HMAC-SHA1 code to the actual TOTP endpoint.")]
    private static string Code(string secret)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        List<byte> key = [];
        int buffer = 0, bits = 0;
        foreach (char character in secret.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8) { bits -= 8; key.Add((byte)(buffer >> bits)); }
        }
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        byte[] hash = HMACSHA1.HashData(key.ToArray(), counter);
        int offset = hash[^1] & 15;
        int number = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & int.MaxValue;
        return (number % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }
}
