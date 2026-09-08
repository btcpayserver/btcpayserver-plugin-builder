#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $ConfigPath = (Join-Path $PSScriptRoot '../.agents/connection.json'),
    [string] $ApiPath = 'admin/me',
    [ValidateSet('GET', 'POST', 'PUT', 'DELETE')] [string] $Method = 'GET',
    [string] $BodyFile,
    [string] $OutFile,
    [switch] $AllowInsecureLocalhost
)
$ErrorActionPreference = 'Stop'

# Keep credentials in memory, never in command arguments or diagnostic output.
try { $config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json }
catch { throw 'Unable to read or parse the local connection file.' }
$baseUri = $null
if (-not [Uri]::TryCreate($config.baseUrl, [UriKind]::Absolute, [ref] $baseUri) -or
    $baseUri.UserInfo -or $baseUri.Query -or $baseUri.Fragment) {
    throw 'Configure an absolute baseUrl without embedded credentials, query or fragment.'
}
$localHttp = $AllowInsecureLocalhost -and $baseUri.Scheme -eq 'http' -and $baseUri.Host -in @('127.0.0.1', '::1', 'localhost')
if ($baseUri.Scheme -ne 'https' -and -not $localHttp) { throw 'HTTPS is required.' }
$apiBase = [Uri]::new($baseUri.AbsoluteUri.TrimEnd('/') + '/api/v1/')
$uri = [Uri]::new($apiBase, $ApiPath)
if ($uri.Scheme -ne $apiBase.Scheme -or $uri.Authority -ne $apiBase.Authority -or
    -not $uri.AbsolutePath.StartsWith($apiBase.AbsolutePath + 'admin/', [StringComparison]::Ordinal) -or $uri.Fragment -or $uri.UserInfo) {
    throw 'ApiPath must target an admin API endpoint on the configured server.'
}
if ($Method -eq 'POST' -and ($uri.AbsolutePath.EndsWith('/access-tokens') -or $uri.AbsolutePath.EndsWith('/events/subscriptions')) -and -not $OutFile) {
    throw 'Use OutFile for responses that may contain a newly issued secret.'
}
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$handler.UseCookies = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(30)
$request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $uri)
try {
    if ($config.token) {
        $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $config.token)
    } elseif ($config.email -and $config.password) {
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($config.email + ':' + $config.password))
        $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Basic', $encoded)
    } else {
        throw 'Configure a token or email and password locally.'
    }
    if ($BodyFile) {
        $request.Content = [System.Net.Http.StringContent]::new((Get-Content -LiteralPath $BodyFile -Raw), [Text.Encoding]::UTF8, 'application/json')
    }
    try { $response = $client.SendAsync($request).GetAwaiter().GetResult() }
    catch { throw 'API connection failed. Check the server URL, TLS and network access.' }
    try {
        if (-not $response.IsSuccessStatusCode) {
            throw ('Admin API returned HTTP ' + [int] $response.StatusCode + '. Check authentication, account access and request parameters.')
        }
        $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($OutFile) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutFile), $content) }
        else { Write-Output $content }
    } finally { $response.Dispose() }
} finally {
    $request.Dispose()
    $client.Dispose()
}
