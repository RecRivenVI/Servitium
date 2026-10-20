using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Domain.Primitives;
using Servitium.Server.Security;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class SvgSanitizerTests
{
    [Fact]
    [Trait("Category", "Security")]
    [Trait("TestId", "UNT-430")]
    public async Task Svg消毒_接受纯矢量并拒绝脚本事件与外部引用()
    {
        byte[] safe = await SanitizeAsync("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><path d=\"M0 0L10 10\" fill=\"none\"/></svg>").ConfigureAwait(true);
        Assert.Contains("<path", Encoding.UTF8.GetString(safe), System.StringComparison.Ordinal);

        await AssertRejectedAsync("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>").ConfigureAwait(true);
        await AssertRejectedAsync("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>").ConfigureAwait(true);
        await AssertRejectedAsync("<svg xmlns=\"http://www.w3.org/2000/svg\"><use href=\"https://example.invalid/a.svg#x\"/></svg>").ConfigureAwait(true);
        await AssertRejectedAsync("<!DOCTYPE svg [<!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\">]><svg xmlns=\"http://www.w3.org/2000/svg\"><title>&xxe;</title></svg>").ConfigureAwait(true);
    }

    private static async Task<byte[]> SanitizeAsync(string value)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(value));
        return await SvgSanitizer.SanitizeAsync(stream, CancellationToken.None).ConfigureAwait(true);
    }

    private static async Task AssertRejectedAsync(string value)
    {
        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => SanitizeAsync(value)).ConfigureAwait(true);
        Assert.Equal("validation-failed", exception.Code);
    }
}
