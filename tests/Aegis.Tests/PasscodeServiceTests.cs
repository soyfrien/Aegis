using Aegis.Web.Services;

namespace Aegis.Tests;

public class PasscodeServiceTests
{
    [Fact]
    public void Verifies_the_configured_passcode_only()
    {
        var svc = new PasscodeService("correct horse");
        Assert.True(svc.IsConfigured);
        Assert.True(svc.Verify("correct horse"));
        Assert.False(svc.Verify("correct horse "));
        Assert.False(svc.Verify("wrong"));
        Assert.False(svc.Verify(""));
        Assert.False(svc.Verify(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nobody_can_log_in_when_unconfigured(string? configured)
    {
        var svc = new PasscodeService(configured);
        Assert.False(svc.IsConfigured);
        Assert.False(svc.Verify(configured));
        Assert.False(svc.Verify("anything"));
    }
}
