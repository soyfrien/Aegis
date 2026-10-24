using System.Net;
using System.Text;
using Aegis.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aegis.Tests;

public class OneDriveFlowTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            var json = """{"device_code":"dc","user_code":"ABCD1234","verification_uri":"https://www.microsoft.com/link","verification_uri_complete":"https://www.microsoft.com/link?otc=ABCD1234","expires_in":900,"interval":5}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    [Fact]
    public void Default_tenant_is_consumers_for_personal_accounts()
    {
        Assert.Equal("consumers", new OneDriveOptions().TenantId);
    }

    [Fact]
    public async Task Device_flow_uses_consumers_authority_and_returns_complete_uri()
    {
        var opts = new AegisOptions { StateDir = Path.Combine(Path.GetTempPath(), "aegis-test-" + Guid.NewGuid().ToString("N")) };
        opts.OneDrive.ClientId = "00000000-0000-0000-0000-000000000000";
        var handler = new Handler();
        var svc = new OneDriveService(Options.Create(opts), new Factory(handler), new EphemeralDataProtectionProvider(), NullLogger<OneDriveService>.Instance);

        var info = await svc.StartDeviceFlowAsync(CancellationToken.None);

        Assert.Equal("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode", Assert.Single(handler.Urls));
        Assert.Equal("ABCD1234", info.UserCode);
        Assert.Equal("https://www.microsoft.com/link?otc=ABCD1234", info.VerificationUriComplete);
    }
}
