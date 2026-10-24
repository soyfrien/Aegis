using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aegis.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aegis.Tests;

public class StatsParserTests
{
    [Fact]
    public void Parses_meminfo()
    {
        const string text = "MemTotal:        1000000 kB\nMemFree:          100000 kB\nMemAvailable:     400000 kB\nSwapTotal:       2000000 kB\nSwapFree:        1500000 kB\n";
        var m = SystemStatsService.ParseMeminfo(text);
        Assert.Equal(1_000_000L * 1024, m.TotalBytes);
        Assert.Equal(600_000L * 1024, m.UsedBytes);
        Assert.Equal(500_000L * 1024, m.SwapUsedBytes);
    }

    [Fact]
    public void Computes_cpu_percent_from_two_samples()
    {
        var a = SystemStatsService.ParseCpuStat("cpu  100 0 100 800 0 0 0 0 0 0\ncpu0 1 2 3 4\n");
        var b = SystemStatsService.ParseCpuStat("cpu  150 0 150 900 0 0 0 0 0 0\n");
        Assert.Equal(1000, a!.Total);
        // delta total 200, delta idle 100 => 50% busy
        Assert.Equal(50.0, SystemStatsService.CpuPercent(a, b));
        Assert.Null(SystemStatsService.CpuPercent(null, b));
        Assert.Null(SystemStatsService.CpuPercent(b, b));
    }

    [Fact]
    public void Parses_loadavg_uptime_and_os_release()
    {
        Assert.Equal([0.5, 0.25, 0.1], SystemStatsService.ParseLoadAvg("0.50 0.25 0.10 1/200 1234\n"));
        Assert.Equal(12345.67, SystemStatsService.ParseUptimeSeconds("12345.67 9999.00\n"));
        Assert.Equal("Ubuntu 22.04.5 LTS", SystemStatsService.ParseOsPrettyName("NAME=\"Ubuntu\"\nPRETTY_NAME=\"Ubuntu 22.04.5 LTS\"\n"));
    }

    [Fact]
    public void Parses_df_output()
    {
        var d = SystemStatsService.ParseDf("Mounted on        1B-blocks       Used      Avail\n/          30000000000 10000000000 20000000000\n");
        Assert.Equal("/", d!.Mount);
        Assert.Equal(30_000_000_000, d.TotalBytes);
        Assert.Equal(20_000_000_000, d.FreeBytes);
        Assert.Null(SystemStatsService.ParseDf("garbage"));
    }

    [Fact]
    public void Job_records_keep_only_the_host()
    {
        Assert.Equal("www.youtube.com", JobService.SafeHost("https://www.youtube.com/watch?v=abc&token=secret"));
        Assert.Equal("?", JobService.SafeHost("nonsense"));
    }
}

public class StatsEndpointTests : IClassFixture<ApiTests.Factory>
{
    private readonly ApiTests.Factory _factory;
    public StatsEndpointTests(ApiTests.Factory factory) => _factory = factory;

    [Fact]
    public async Task Stats_requires_login_and_returns_expected_shape()
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/stats")).StatusCode);

        (await c.PostAsJsonAsync("/api/login", new { passcode = "test-passcode-123" })).EnsureSuccessStatusCode();
        var r = await c.GetAsync("/api/stats");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.False(string.IsNullOrEmpty(root.GetProperty("hostname").GetString()));
        Assert.True(root.GetProperty("cpuCount").GetInt32() >= 1);
        Assert.True(root.GetProperty("app").GetProperty("uptimeSeconds").GetDouble() >= 0);
        Assert.Equal(0, root.GetProperty("downloads").GetProperty("total").GetInt32());
        Assert.True(root.GetProperty("versions").TryGetProperty(".NET", out _));
    }

    [Fact]
    public async Task Stats_page_is_served()
    {
        var r = await _factory.CreateClient().GetAsync("/stats");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Aegis stats", await r.Content.ReadAsStringAsync());
    }
}
