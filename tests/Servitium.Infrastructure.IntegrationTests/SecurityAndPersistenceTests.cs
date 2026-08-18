using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Servitium.Application.Abstractions;
using Servitium.Domain.Common;
using Servitium.Infrastructure.Networking;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Security;

using Xunit;

namespace Servitium.Infrastructure.IntegrationTests;

public sealed class SecurityAndPersistenceTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-120")]
    public void Argon2id_使用固定参数且拒绝错误密码()
    {
        Argon2idPasswordHasher hasher = new();
        string encoded = hasher.Hash("correct-horse-battery-staple");

        Assert.StartsWith("argon2id$v=19$m=65536,t=3,p=2$", encoded, StringComparison.Ordinal);
        Assert.True(hasher.Verify("correct-horse-battery-staple", encoded));
        Assert.False(hasher.Verify("incorrect-password", encoded));
        Assert.False(hasher.Verify("correct-horse-battery-staple", "invalid"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-004")]
    public void Totp_兼容Rfc6238的Sha1时间步()
    {
        TotpService service = new(0);
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(59);

        Assert.True(service.Verify("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", "287082", timestamp));
        Assert.False(service.Verify("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", "287083", timestamp));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1130")]
    public void Totp_漂移窗口可在运行时立即更新()
    {
        TotpOptions options = new() { PermittedDriftSteps = 0 };
        TotpService service = new(options);
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(59);

        Assert.False(service.Verify("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", "755224", timestamp));
        options.PermittedDriftSteps = 1;
        Assert.True(service.Verify("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", "755224", timestamp));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-003")]
    public void Dpapi_计算机范围保护后可还原且不保留明文()
    {
        Assert.True(OperatingSystem.IsWindows());
        DpapiSecretProtector protector = new();
        byte[] plaintext = [1, 2, 3, 4, 5, 6, 7, 8];

        byte[] protectedData = protector.Protect(plaintext);

        Assert.NotEqual(plaintext, protectedData);
        Assert.Equal(plaintext, protector.Unprotect(protectedData));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("192.168.1.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-020")]
    public void 出站地址策略_拒绝私网和保留地址(string address)
    {
        Assert.True(OutboundAddressPolicy.IsProhibited(IPAddress.Parse(address)));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-021")]
    public void 出站地址策略_接受普通公网地址()
    {
        Assert.False(OutboundAddressPolicy.IsProhibited(IPAddress.Parse("8.8.8.8")));
        Assert.False(OutboundAddressPolicy.IsProhibited(IPAddress.Parse("2606:4700:4700::1111")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-022")]
    public async Task 出站请求_未进入私有源白名单时在Dns后拒绝禁止地址()
    {
        FixedResolver resolver = new(IPAddress.Loopback);
        RestrictedOutboundHttpClient client = new(
            resolver,
            new RestrictedHttpOptions(new HashSet<string>(StringComparer.OrdinalIgnoreCase), 1024, TimeSpan.FromSeconds(1), 0));

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() =>
            client.SendAsync(new OutboundHttpRequest(new Uri("https://example.com/file"), "GET"), CancellationToken.None)).ConfigureAwait(true);

        Assert.Equal("url-not-allowed", exception.Code);
        Assert.True(resolver.WasCalled);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "ARC-032")]
    public void 审计实体_任何更新或删除均被上下文拒绝()
    {
        DbContextOptions<ServitiumDbContext> options = new DbContextOptionsBuilder<ServitiumDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using ServitiumDbContext context = new(options);
        AuditLogRow row = new();
        _ = context.Attach(row);
        context.Entry(row).Property("Sequence").CurrentValue = 1L;
        context.Entry(row).State = EntityState.Modified;

        InvalidOperationException update = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
        Assert.Contains("append-only", update.Message, StringComparison.Ordinal);

        context.Entry(row).State = EntityState.Deleted;
        InvalidOperationException delete = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
        Assert.Contains("append-only", delete.Message, StringComparison.Ordinal);
    }

    private sealed class FixedResolver(IPAddress address) : IHostAddressResolver
    {
        internal bool WasCalled { get; private set; }

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult<IReadOnlyList<IPAddress>>([address]);
        }
    }
}
