using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;
using PluginBuilder.Util;
using PluginBuilder.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class DisposableEmailDomainsTests
{
    [Theory]
    [InlineData("someone@mailinator.com")]
    [InlineData("someone@MAILINATOR.COM")]
    [InlineData("someone@inbox.mailinator.com")]
    [InlineData("someone@mailinator.com.")]
    [InlineData("someone@yopmail.fr")]
    [InlineData("someone@sharklasers.com")]
    [InlineData("someone@10minute.email")]
    [InlineData("someone@TEMPMAIL.EMAIL")]
    [InlineData("someone@inbox.tempmail.email")]
    [InlineData("someone@trashmail.org")]
    [InlineData("someone@flashbox.5july.org")]
    [InlineData("someone@FLASHBOX.5JULY.ORG")]
    [InlineData("someone@inbox.flashbox.5july.org")]
    // Catch-all services that hand out made-up subdomains, as in a scripted sign-up burst.
    [InlineData("someone@2uh.kashre.com")]
    [InlineData("someone@c2.lucktaro.com")]
    // Mail software converts these to mailinator.com (IDNA), so they reach the same inbox.
    [InlineData("someone@mailinator\u3002com")]
    [InlineData("someone@mailinator\uFF0Ecom")]
    [InlineData("someone@mailinator\uFF61com")]
    [InlineData("someone@mailinator.com\u3002")]
    // Not valid IDNA ("xn--" alone), so the conversion fails; the dots must still separate the labels.
    [InlineData("someone@xn--\u3002mailinator.com")]
    [InlineData("someone@\uFF4D\uFF41\uFF49\uFF4C\uFF49\uFF4E\uFF41\uFF54\uFF4F\uFF52.com")]
    public void AListedDomainOrOneOfItsSubdomainsIsDisposable(string email) =>
        Assert.True(DisposableEmailDomains.IsDisposable(email));

    [Fact]
    public void ADisposableAddressAtTheMaximumLengthIsStillRefused()
    {
        const string suffix = "@mailinator.com";
        var email = new string('a', 256 - suffix.Length) + suffix;
        Assert.True(DisposableEmailDomains.IsDisposable(email));
    }

    [Theory]
    [InlineData("someone@gmail.com")]
    [InlineData("someone@proton.me")]
    [InlineData("someone@mymailinator.com")]
    [InlineData("someone@mailinator.com.example.org")]
    [InlineData("someone@nottempmail.email")]
    [InlineData("someone@tempmail.email.example.org")]
    [InlineData("someone@notflashbox.5july.org")]
    [InlineData("someone@flashbox.5july.org.example.org")]
    [InlineData("someone@b\u00FCcher.example")]
    [InlineData("someone@mailinator..com")]
    [InlineData("mailinator.com@example.org")]
    [InlineData("someone@")]
    [InlineData("mailinator.com")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsNot(string? email) =>
        Assert.False(DisposableEmailDomains.IsDisposable(email));

    [Fact]
    public void EveryEntryIsABareDomain()
    {
        // "@mailinator.com" or "https://mailinator.com" would never match, so a typo there would silently block nothing.
        var hostname = new Regex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$");
        Assert.All(DisposableEmailDomains.Domains, domain => Assert.Matches(hostname, domain));
        Assert.Empty(DisposableEmailDomains.Domains.GroupBy(d => d).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    [Fact]
    public void TheListMatchesThePinnedSnapshotWithLocalCorrections()
    {
        var canonical = Encoding.UTF8.GetBytes(string.Join('\n', DisposableEmailDomains.Domains) + "\n");
        Assert.Equal("96eed897524f3850f9f7d67e1833abf1fbe59d9294e860791d5974906adab360",
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    [Theory]
    [InlineData("mailinator.com")]
    [InlineData("tempmail.email")]
    [InlineData("trashmail.org")]
    [InlineData("flashbox.5july.org")]
    [InlineData("inbox.flashbox.5july.org")]
    public void RegistrationRefusesADisposableAddress(string domain)
    {
        var model = new RegisterViewModel { Email = "someone@" + domain, Password = "123456", ConfirmPassword = "123456" };
        var errors = new List<ValidationResult>();

        Assert.False(Validator.TryValidateObject(model, new ValidationContext(model), errors, validateAllProperties: true));
        var error = Assert.Single(errors);
        Assert.Equal("Please provide a valid email address.", error.ErrorMessage);
        Assert.Equal([nameof(RegisterViewModel.Email)], error.MemberNames);

        model.Email = "someone@example.org";
        Assert.True(Validator.TryValidateObject(model, new ValidationContext(model), errors, validateAllProperties: true));
    }

    [Theory]
    [InlineData("21cn.com")]
    [InlineData("5july.org")]
    [InlineData("atomauto.vn")]
    [InlineData("dhm.ro")]
    [InlineData("digitalsanctuary.com")]
    [InlineData("hola.org")]
    [InlineData("jsrsolutions.com")]
    [InlineData("lukecarriere.com")]
    [InlineData("neomailbox.com")]
    [InlineData("privacy.net")]
    [InlineData("stanfordujjain.com")]
    public void RegistrationAllowsLegitimateDomainsAndTheirSubdomains(string domain)
    {
        foreach (var email in new[] { "someone@" + domain, "someone@inbox." + domain })
        {
            var model = new RegisterViewModel { Email = email, Password = "123456", ConfirmPassword = "123456" };
            var errors = new List<ValidationResult>();

            Assert.False(DisposableEmailDomains.IsDisposable(email));
            Assert.True(Validator.TryValidateObject(model, new ValidationContext(model), errors, validateAllProperties: true));
            Assert.Empty(errors);
        }
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    [InlineData(16384, false)]
    public void RegistrationValidatesEmailLength(int length, bool expected)
    {
        const string suffix = "@example.org";
        var model = new RegisterViewModel
        {
            Email = new string('a', length - suffix.Length) + suffix,
            Password = "123456",
            ConfirmPassword = "123456"
        };
        var errors = new List<ValidationResult>();

        Assert.Equal(expected, Validator.TryValidateObject(model, new ValidationContext(model), errors, validateAllProperties: true));
        if (!expected)
        {
            var error = Assert.Single(errors);
            Assert.Equal("Please provide a valid email address.", error.ErrorMessage);
            Assert.Equal([nameof(RegisterViewModel.Email)], error.MemberNames);
        }
    }

    [Theory]
    [InlineData("example.org")]
    [InlineData("mailinator.com")]
    public void OversizedDomainsDoNotAllocatePerSuffix(string domain)
    {
        var email = "someone@" + string.Concat(Enumerable.Repeat("a.", 8192)) + domain;
        DisposableEmailDomains.IsDisposable("someone@example.org");
        var before = GC.GetAllocatedBytesForCurrentThread();

        DisposableEmailDomains.IsDisposable(email);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 64 * 1024);
    }
}

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class DisposableEmailRegistrationTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    [Fact]
    public async Task DisposableAndOversizedAddressesCannotRegisterAndANormalOneCan()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        // Registration needs only the database: drop the other hosted services (storage, builds), as the admin API tests do.
        tester.ConfigureServices = services =>
        {
            foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType != typeof(DatabaseStartupHostedService)).ToArray())
                services.Remove(service);
        };
        await tester.Start();
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() })
        {
            BaseAddress = new Uri(tester.WebApp.Urls.First(), UriKind.Absolute)
        };
        var page = await (await client.GetAsync("/register")).Content.ReadAsStringAsync();

        var rejectedEmails = new[]
        {
            $"disposable-{Guid.NewGuid():N}@Mailinator.com",
            $"disposable-{Guid.NewGuid():N}@mailinator\u3002com",
            $"disposable-{Guid.NewGuid():N}@tempmail.email",
            $"disposable-{Guid.NewGuid():N}@trashmail.org",
            $"disposable-{Guid.NewGuid():N}@flashbox.5july.org",
            $"disposable-{Guid.NewGuid():N}@inbox.flashbox.5july.org",
            new string('a', 245) + "@example.org", // 257 characters: just above the database's limit.
            "someone@" + string.Concat(Enumerable.Repeat("a.", 8192)) + "example.org"
        };
        foreach (var email in rejectedEmails)
        {
            using (var response = await client.PostAsync("/register", Form(email, AntiforgeryToken(page))))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains("Please provide a valid email address.", await response.Content.ReadAsStringAsync());
            }
            Assert.False(await UserExists(conn, email));
        }

        foreach (var domain in new[] { "example.org", "5july.org" })
        {
            // A successful registration signs in the client, so refresh the user-bound antiforgery token.
            page = await (await client.GetAsync("/register")).Content.ReadAsStringAsync();
            var normal = $"normal-{Guid.NewGuid():N}@{domain}";
            using (var response = await client.PostAsync("/register", Form(normal, AntiforgeryToken(page))))
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.True(await UserExists(conn, normal));
        }
    }

    private static FormUrlEncodedContent Form(string email, string antiforgeryToken) =>
        new(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = "123456",
            ["ConfirmPassword"] = "123456",
            ["__RequestVerificationToken"] = antiforgeryToken
        });

    private static Task<bool> UserExists(Npgsql.NpgsqlConnection conn, string email) =>
        conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM \"AspNetUsers\" WHERE lower(\"Email\") = lower(@email))", new { email });

    private static string AntiforgeryToken(string html)
    {
        var input = Regex.Match(html, "<input\\b(?=[^>]*\\bname=\"__RequestVerificationToken\")[^>]*>", RegexOptions.IgnoreCase);
        Assert.True(input.Success, "HTML did not contain an antiforgery token input.");
        var value = Regex.Match(input.Value, "\\bvalue=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        Assert.True(value.Success, "Antiforgery token input did not contain a value.");
        return WebUtility.HtmlDecode(value.Groups[1].Value);
    }
}
