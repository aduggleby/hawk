using Hawk.Web.Services.Monitoring;

namespace Hawk.Tests;

public class AllowedStatusCodesParserTests
{
    [Fact]
    public void TryParse_ValidList_Succeeds()
    {
        var ok = AllowedStatusCodesParser.TryParse("404, 429;503", out var codes, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Contains(404, codes);
        Assert.Contains(429, codes);
        Assert.Contains(503, codes);
    }

    [Fact]
    public void TryParse_InvalidToken_Fails()
    {
        var ok = AllowedStatusCodesParser.TryParse("404,foo", out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void IsSuccessStatusCode_Default2xxAndAdditional()
    {
        Assert.True(AllowedStatusCodesParser.IsSuccessStatusCode(System.Net.HttpStatusCode.OK, null));
        Assert.False(AllowedStatusCodesParser.IsSuccessStatusCode(System.Net.HttpStatusCode.NotFound, null));
        Assert.True(AllowedStatusCodesParser.IsSuccessStatusCode(System.Net.HttpStatusCode.NotFound, "404,429"));
    }
}
