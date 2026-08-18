using System.Text;

using Servitium.Agent.Configurations;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class PreservingPropertiesDocumentTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-890")]
    public void Properties_未修改时字节级往返一致()
    {
        byte[] original = Encoding.UTF8.GetBytes("# comment\r\n\r\nmotd : 合成服务器\r\nescaped\\:key=value\r\ncontinued=one\\\r\n  two\r\n");

        var document = PreservingPropertiesDocument.Parse(original);

        Assert.Equal(original, document.Write());
        Assert.Equal("合成服务器", document.GetValue("motd"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-900")]
    public void Properties_修改值时保留注释顺序分隔符和行尾()
    {
        byte[] original = Encoding.UTF8.GetBytes("# keep\r\neula : false\r\nmotd=keep\r\n");
        var document = PreservingPropertiesDocument.Parse(original);

        document.SetValue("eula", "true");
        document.SetValue("online-mode", "true");

        Assert.Equal("# keep\r\neula : true\r\nmotd=keep\r\nonline-mode=true\r\n", Encoding.UTF8.GetString(document.Write()));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-900")]
    public void Properties_改写续行值时保留物理续行结构()
    {
        var document = PreservingPropertiesDocument.Parse(Encoding.UTF8.GetBytes("value=one\\\r\n  two\r\n"));

        document.SetValue("value", "replacement");

        Assert.Equal("value=replac\\\r\n  ement\r\n", Encoding.UTF8.GetString(document.Write()));
        Assert.Equal("replacement", document.GetValue("value"));
    }
}
