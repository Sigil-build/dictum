using Dictum.App.Shell;

namespace Dictum.App.Tests.Shell;

public class SelfTestTests
{
    [Fact]
    public void TheChecksThatNeedNoPlatformPass()
    {
        var output = new StringWriter();

        var code = SelfTest.Run(output, avalonia: false);

        Assert.True(code == 0, output.ToString());
        Assert.Contains("ok   strings", output.ToString());
        Assert.Contains("ok   svg icons: 11 files", output.ToString());
        Assert.Contains("ok   overlay model", output.ToString());
    }
}
