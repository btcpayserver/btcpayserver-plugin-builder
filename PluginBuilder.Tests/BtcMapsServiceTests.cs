using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PluginBuilder.APIModels;
using PluginBuilder.Services;
using Xunit;

namespace PluginBuilder.Tests;

public class BtcMapsServiceTests
{
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }

    private static BtcMapsService MakeService() =>
        new BtcMapsService(
            configuration: new ConfigurationBuilder().Build(),
            httpClientFactory: new StubHttpClientFactory(),
            logger: NullLogger<BtcMapsService>.Instance);

    private static BtcMapsSubmitRequest MakeValid() => new()
    {
        Name = "Good Shop",
        Url = "https://goodshop.example",
        Description = "A very good shop.",
        Type = "merchants"
    };

    [Fact]
    public void Validate_AcceptsMinimalDirectoryRequest()
    {
        Assert.Empty(MakeService().Validate(MakeValid()));
    }

    [Fact]
    public void Validate_RejectsMissingName()
    {
        var req = MakeValid();
        req.Name = null;
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Name));
    }

    [Fact]
    public void Validate_RejectsOverlongName()
    {
        var req = MakeValid();
        req.Name = new string('x', 201);
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Name));
    }

    [Fact]
    public void Validate_RejectsNonHttpsUrl()
    {
        var req = MakeValid();
        req.Url = "http://plain.example";
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Url));
    }

    [Fact]
    public void Validate_RejectsMissingDescription()
    {
        var req = MakeValid();
        req.Description = null;
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Description));
    }

    [Fact]
    public void Validate_RejectsOverlongDescription()
    {
        var req = MakeValid();
        req.Description = new string('x', 1001);
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Description));
    }

    [Fact]
    public void Validate_RejectsMissingType()
    {
        var req = MakeValid();
        req.Type = null;
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Type));
    }

    [Fact]
    public void Validate_RejectsInvalidType()
    {
        var req = MakeValid();
        req.Type = "shops";
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Type));
    }

    [Fact]
    public void Validate_RejectsInvalidMerchantSubType()
    {
        var req = MakeValid();
        req.SubType = "not-a-real-subtype";
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.SubType));
    }

    [Fact]
    public void Validate_AcceptsValidMerchantSubType()
    {
        var req = MakeValid();
        req.SubType = "books";
        Assert.Empty(MakeService().Validate(req));
    }

    [Fact]
    public void Validate_AcceptsIsoAlpha2Country()
    {
        var req = MakeValid();
        req.Country = "DE";
        Assert.Empty(MakeService().Validate(req));
    }

    [Fact]
    public void Validate_AcceptsGlobalCountry()
    {
        var req = MakeValid();
        req.Country = "GLOBAL";
        Assert.Empty(MakeService().Validate(req));
    }

    [Fact]
    public void Validate_RejectsLowerCaseCountry()
    {
        var req = MakeValid();
        req.Country = "de";
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Country));
    }

    [Fact]
    public void Validate_RejectsThreeLetterCountry()
    {
        var req = MakeValid();
        req.Country = "DEU";
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Country));
    }

    [Fact]
    public void Validate_RejectsNonAssignedTwoLetterCountry()
    {
        // ZZ is reserved / not assigned in ISO 3166-1, so the validator must
        // reject it even though it passes the length + casing check.
        var req = MakeValid();
        req.Country = "ZZ";
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Country));
    }

    [Fact]
    public void Validate_AcceptsOnionHttpsUrl()
    {
        var req = MakeValid();
        req.OnionUrl = "https://abc123.onion";
        Assert.Empty(MakeService().Validate(req));
    }

    [Fact]
    public void Validate_AcceptsOnionHttpUrl()
    {
        // Onion v3 addresses are commonly served over http (Tor provides the transport
        // encryption); the validator allows http on a .onion host explicitly.
        var req = MakeValid();
        req.OnionUrl = "http://abc123.onion";
        Assert.Empty(MakeService().Validate(req));
    }

    [Fact]
    public void Validate_RejectsNonOnionOnionUrl()
    {
        var req = MakeValid();
        req.OnionUrl = "https://example.com";
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.OnionUrl));
    }

    // BTC Map import-RPC fields are mandatory only when SubmitToBtcMap=true. The
    // default-false path preserves the directory-only callers untouched.

    private static BtcMapsSubmitRequest MakeValidBtcMap()
    {
        var req = MakeValid();
        req.SubmitToBtcMap = true;
        req.Lat = 51.5074;
        req.Lon = -0.1278;
        req.Category = "cafe";
        req.ExternalId = "store.example.com:abc123";
        return req;
    }

    [Fact]
    public void Validate_AcceptsValidBtcMapSubmission()
    {
        Assert.Empty(MakeService().Validate(MakeValidBtcMap()));
    }

    [Fact]
    public void Validate_DoesNotRequireBtcMapFieldsByDefault()
    {
        // Directory-only callers (the pre-existing shape) must not break: a
        // request with SubmitToBtcMap unset (default false) and no Lat / Lon /
        // Category / ExternalId is still valid.
        Assert.Empty(MakeService().Validate(MakeValid()));
    }

    [Fact]
    public void Validate_RejectsMissingLatWhenSubmitToBtcMap()
    {
        var req = MakeValidBtcMap();
        req.Lat = null;
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Lat));
    }

    [Fact]
    public void Validate_RejectsOutOfRangeLat()
    {
        var req = MakeValidBtcMap();
        req.Lat = 91.0;
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Lat));
    }

    [Fact]
    public void Validate_RejectsNaNLat()
    {
        var req = MakeValidBtcMap();
        req.Lat = double.NaN;
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Lat));
    }

    [Fact]
    public void Validate_RejectsMissingLonWhenSubmitToBtcMap()
    {
        var req = MakeValidBtcMap();
        req.Lon = null;
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Lon));
    }

    [Fact]
    public void Validate_RejectsOutOfRangeLon()
    {
        var req = MakeValidBtcMap();
        req.Lon = -180.5;
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Lon));
    }

    [Fact]
    public void Validate_RejectsMissingCategoryWhenSubmitToBtcMap()
    {
        var req = MakeValidBtcMap();
        req.Category = null;
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Category));
    }

    [Fact]
    public void Validate_RejectsUppercaseCategoryWhenSubmitToBtcMap()
    {
        // BTC Map docs: "Use a short, single-word (if possible), lowercase identifier."
        var req = MakeValidBtcMap();
        req.Category = "Cafe";
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Category));
    }

    [Fact]
    public void Validate_RejectsCategoryWithInvalidCharacters()
    {
        var req = MakeValidBtcMap();
        req.Category = "cafe!";
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Category));
    }

    [Fact]
    public void Validate_RejectsMissingExternalIdWhenSubmitToBtcMap()
    {
        var req = MakeValidBtcMap();
        req.ExternalId = null;
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.ExternalId));
    }

    [Fact]
    public void Validate_RejectsOverlongExternalId()
    {
        var req = MakeValidBtcMap();
        req.ExternalId = new string('x', 201);
        Assert.Contains(MakeService().Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.ExternalId));
    }

    [Fact]
    public void NormalizeUrl_LowercasesSchemeAndHostOnly()
    {
        // Scheme + host are case-insensitive (DNS + RFC); path + query are not, so
        // they must be preserved verbatim. Trailing slash is stripped only when the
        // path is non-root.
        Assert.Equal("https://example.com/", BtcMapsService.NormalizeUrl("HTTPS://Example.com/"));
        Assert.Equal("https://example.com/", BtcMapsService.NormalizeUrl("  https://example.com  "));
    }

    [Fact]
    public void NormalizeUrl_PreservesPathCase()
    {
        Assert.Equal("https://example.com/Foo/Bar",
            BtcMapsService.NormalizeUrl("HTTPS://Example.com/Foo/Bar/"));
    }

    [Fact]
    public void NormalizeUrl_PreservesQueryCase()
    {
        Assert.Equal("https://example.com/path?ID=ABC",
            BtcMapsService.NormalizeUrl("https://EXAMPLE.com/path?ID=ABC"));
    }

    [Fact]
    public void BuildBranchName_DeterministicForSameUrl()
    {
        var a = BtcMapsService.BuildBranchName("Good Shop", "https://example.com/foo");
        var b = BtcMapsService.BuildBranchName("Good Shop", "https://example.com/foo");
        Assert.Equal(a, b);
        Assert.StartsWith("btcmaps/good-shop-", a);
    }

    [Fact]
    public void BuildBranchName_DiffersForDifferentUrls()
    {
        var a = BtcMapsService.BuildBranchName("Good Shop", "https://example.com/foo");
        var b = BtcMapsService.BuildBranchName("Good Shop", "https://example.com/bar");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Slugify_ProducesUrlSafeSegment()
    {
        Assert.Equal("good-shop", BtcMapsService.Slugify("Good Shop!"));
        Assert.Equal("merchant", BtcMapsService.Slugify("!!!"));
    }

    [Fact]
    public void Slugify_CapsLengthAtFortyChars()
    {
        var input = new string('a', 80);
        var slug = BtcMapsService.Slugify(input);
        Assert.True(slug.Length <= 40);
    }
}
