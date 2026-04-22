using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PluginBuilder.APIModels;
using PluginBuilder.Services;
using Xunit;

namespace PluginBuilder.Tests;

public class BtcMapsServiceTests
{
    private static BtcMapsService MakeService() =>
        new BtcMapsService(
            configuration: new ConfigurationBuilder().Build(),
            logger: NullLogger<BtcMapsService>.Instance);

    [Fact]
    public void Validate_RequiresAtLeastOneAction_NotEnforcedHere()
    {
        // The controller enforces (submitToDirectory || tagOnOsm). The service
        // validator focuses on field-level validity, so an all-false request
        // with only core fields should still pass Validate cleanly.
        var svc = MakeService();
        var req = new BtcMapsSubmitRequest
        {
            Name = "Good Shop",
            Url = "https://goodshop.example",
            Description = "A very good shop."
        };
        Assert.Empty(svc.Validate(req));
    }

    [Fact]
    public void Validate_RejectsMissingName()
    {
        var svc = MakeService();
        var req = new BtcMapsSubmitRequest
        {
            Url = "https://shop.example",
            Description = "desc"
        };
        Assert.Contains(svc.Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Name));
    }

    [Fact]
    public void Validate_RejectsNonHttpsUrl()
    {
        var svc = MakeService();
        var req = new BtcMapsSubmitRequest
        {
            Name = "Shop",
            Url = "http://plain.example",
            Description = "desc"
        };
        Assert.Contains(svc.Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Url));
    }

    [Fact]
    public void Validate_RejectsOverlongDescription()
    {
        var svc = MakeService();
        var req = new BtcMapsSubmitRequest
        {
            Name = "Shop",
            Url = "https://shop.example",
            Description = new string('x', 1001)
        };
        Assert.Contains(svc.Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Description));
    }

    [Theory]
    [InlineData("merchants", "books", true)]
    [InlineData("merchants", "not-a-subtype", false)]
    [InlineData("apps", "not-a-subtype", true)]
    public void Validate_ChecksMerchantSubType(string type, string subType, bool expectValid)
    {
        var svc = MakeService();
        var req = new BtcMapsSubmitRequest
        {
            Name = "Shop",
            Url = "https://shop.example",
            Description = "desc",
            Type = type,
            SubType = subType,
            SubmitToDirectory = true
        };
        var errors = svc.Validate(req);
        if (expectValid)
            Assert.DoesNotContain(errors, e => e.Path == nameof(BtcMapsSubmitRequest.SubType));
        else
            Assert.Contains(errors, e => e.Path == nameof(BtcMapsSubmitRequest.SubType));
    }

    [Fact]
    public void Validate_RejectsUnknownType_OnDirectorySubmit()
    {
        var svc = MakeService();
        var req = new BtcMapsSubmitRequest
        {
            Name = "Shop",
            Url = "https://shop.example",
            Description = "desc",
            Type = "unicorns",
            SubmitToDirectory = true
        };
        Assert.Contains(svc.Validate(req), e => e.Path == nameof(BtcMapsSubmitRequest.Type));
    }

    [Fact]
    public void Validate_SkipsDirectoryFieldsWhenNotSubmitting()
    {
        var svc = MakeService();
        var req = new BtcMapsSubmitRequest
        {
            Name = "Shop",
            Url = "https://shop.example",
            Description = "desc",
            Type = "unicorns",
            SubmitToDirectory = false
        };
        Assert.Empty(svc.Validate(req));
    }

    [Theory]
    [InlineData("GLOBAL", true)]
    [InlineData("US", true)]
    [InlineData("us", false)]
    [InlineData("USA", false)]
    public void Validate_ChecksCountryOnDirectorySubmit(string country, bool expectValid)
    {
        var svc = MakeService();
        var req = new BtcMapsSubmitRequest
        {
            Name = "Shop",
            Url = "https://shop.example",
            Description = "desc",
            Type = "merchants",
            Country = country,
            SubmitToDirectory = true
        };
        var errors = svc.Validate(req);
        if (expectValid)
            Assert.DoesNotContain(errors, e => e.Path == nameof(BtcMapsSubmitRequest.Country));
        else
            Assert.Contains(errors, e => e.Path == nameof(BtcMapsSubmitRequest.Country));
    }

    [Theory]
    [InlineData("http://example.onion", false)]
    [InlineData("https://abc.example", false)]
    [InlineData("http://abc.onion", true)]
    [InlineData("https://abc.onion", true)]
    public void Validate_ChecksOnionUrl(string onion, bool expectValid)
    {
        var svc = MakeService();
        var req = new BtcMapsSubmitRequest
        {
            Name = "Shop",
            Url = "https://shop.example",
            Description = "desc",
            Type = "merchants",
            OnionUrl = onion,
            SubmitToDirectory = true
        };
        var errors = svc.Validate(req);
        if (expectValid)
            Assert.DoesNotContain(errors, e => e.Path == nameof(BtcMapsSubmitRequest.OnionUrl));
        else
            Assert.Contains(errors, e => e.Path == nameof(BtcMapsSubmitRequest.OnionUrl));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(123L, "node", true)]
    [InlineData(123L, "Node", true)]
    [InlineData(123L, "relation", true)]
    [InlineData(123L, "line", false)]
    [InlineData(-1L, "node", false)]
    public void Validate_ChecksOsmTagFields(long? nodeId, string? nodeType, bool expectValid)
    {
        var svc = MakeService();
        var req = new BtcMapsSubmitRequest
        {
            Name = "Shop",
            Url = "https://shop.example",
            Description = "desc",
            OsmNodeId = nodeId,
            OsmNodeType = nodeType,
            TagOnOsm = true
        };
        var errors = svc.Validate(req)
            .Where(e => e.Path is nameof(BtcMapsSubmitRequest.OsmNodeId) or nameof(BtcMapsSubmitRequest.OsmNodeType))
            .ToList();
        if (expectValid)
            Assert.Empty(errors);
        else
            Assert.NotEmpty(errors);
    }

    [Theory]
    [InlineData("https://shop.example/", "https://shop.example/", true)]
    [InlineData("https://shop.example", "https://shop.example/", true)]
    [InlineData("https://Shop.Example/", "https://shop.example", true)]
    [InlineData("https://shop.example/a", "https://shop.example/b", false)]
    public void NormalizeUrl_IgnoresTrailingSlashAndCase(string a, string b, bool equal)
    {
        Assert.Equal(equal, BtcMapsService.NormalizeUrl(a) == BtcMapsService.NormalizeUrl(b));
    }

    [Theory]
    [InlineData("9 Bravos", "9-bravos")]
    [InlineData("Altair Technology", "altair-technology")]
    [InlineData("!!!", "merchant")]
    [InlineData("  leading and trailing  ", "leading-and-trailing")]
    public void Slugify_ProducesUrlSafeSlug(string input, string expected)
    {
        Assert.Equal(expected, BtcMapsService.Slugify(input));
    }
}
