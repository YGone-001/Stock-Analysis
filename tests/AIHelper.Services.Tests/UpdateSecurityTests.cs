using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.IO;
using System.Text;
using AIHelper.Helpers;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class UpdateSecurityTests
{
    private const string GoodHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void StableVersions_NormalizeAndCompareWithoutHardcodedUpdateVersion()
    {
        Assert.True(ApplicationVersionProvider.TryNormalizeStable("1.0.0", out string? first)); Assert.Equal("1.0.0", first);
        Assert.True(ApplicationVersionProvider.TryNormalizeStable("v2.0.0", out string? second)); Assert.Equal("2.0.0", second);
        Assert.True(ApplicationVersionProvider.TryNormalizeStable("2.0.0+abc123", out string? metadata)); Assert.Equal("2.0.0", metadata);
        Assert.False(ApplicationVersionProvider.TryNormalizeStable("", out _));
        Assert.False(ApplicationVersionProvider.TryNormalizeStable("abc", out _));
        Assert.False(ApplicationVersionProvider.TryNormalizeStable("2.0", out _));
        Assert.True(ApplicationVersionProvider.IsRemoteNewer("2.0.0", "1.0.0"));
        Assert.False(ApplicationVersionProvider.IsRemoteNewer("2.0.0", "2.0.0"));
        Assert.False(ApplicationVersionProvider.IsRemoteNewer("1.0.0", "2.0.0"));
    }

    [Fact]
    public void ManifestValidation_RequiresEverySecurityFieldAndApprovedUri()
    {
        UpdateManifest valid = Manifest();
        Assert.Equal(UpdateOperationStatus.UpdateReady, UpdateManifestValidator.Validate(valid).Status);
        Assert.Equal(UpdateOperationStatus.InvalidManifest, UpdateManifestValidator.Validate(valid with { Version = null }).Status);
        Assert.Equal(UpdateOperationStatus.InvalidManifest, UpdateManifestValidator.Validate(valid with { DownloadUrl = null }).Status);
        Assert.Equal(UpdateOperationStatus.UnsafeUrl, UpdateManifestValidator.Validate(valid with { DownloadUrl = "http://www.ooppp.com/AIHelper.exe" }).Status);
        Assert.Equal(UpdateOperationStatus.UnsafeUrl, UpdateManifestValidator.Validate(valid with { DownloadUrl = "file:///C:/AIHelper.exe" }).Status);
        Assert.Equal(UpdateOperationStatus.UnsafeUrl, UpdateManifestValidator.Validate(valid with { DownloadUrl = "https://example.com/AIHelper.exe" }).Status);
        Assert.Equal(UpdateOperationStatus.UnsupportedArtifact, UpdateManifestValidator.Validate(valid with { DownloadUrl = "https://www.ooppp.com/AIHelper.ps1" }).Status);
        Assert.Equal(UpdateOperationStatus.InvalidManifest, UpdateManifestValidator.Validate(valid with { Sha256 = null }).Status);
        Assert.Equal(UpdateOperationStatus.InvalidManifest, UpdateManifestValidator.Validate(valid with { Sha256 = "not-a-hash" }).Status);
        Assert.Equal(UpdateOperationStatus.InvalidManifest, UpdateManifestValidator.Validate(valid with { Version = "2.0.0-beta" }).Status);
    }

    [Fact]
    public async Task Downloader_HashMismatchDeletesStagedArtifact()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("untrusted payload");
        UpdateManifest manifest = Manifest("9.9.9", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("different"))));
        string path = StagedPath(manifest);
        TryDelete(path);
        using HttpClient client = new(new StaticHandler(_ => Response(HttpStatusCode.OK, bytes)));
        UpdateOperationResult result = await new UpdateDownloader(client).DownloadVerifiedAsync(manifest);
        Assert.Equal(UpdateOperationStatus.HashMismatch, result.Status);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Downloader_ValidHashStagesOnlyApprovedArtifact()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("verified payload");
        UpdateManifest manifest = Manifest("9.9.8", Convert.ToHexString(SHA256.HashData(bytes)));
        string path = StagedPath(manifest);
        TryDelete(path);
        try
        {
            using HttpClient client = new(new StaticHandler(_ => Response(HttpStatusCode.OK, bytes)));
            UpdateOperationResult result = await new UpdateDownloader(client).DownloadVerifiedAsync(manifest);
            Assert.True(result.IsReady); Assert.Equal(path, result.VerifiedArtifactPath); Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Downloader_RejectsRedirectToDisallowedHostAndPreservesCancellation()
    {
        UpdateManifest manifest = Manifest("9.9.7", GoodHash);
        using HttpClient redirectClient = new(new StaticHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/AIHelper.exe") } }));
        UpdateOperationResult redirect = await new UpdateDownloader(redirectClient).DownloadVerifiedAsync(manifest);
        Assert.Equal(UpdateOperationStatus.UnsafeUrl, redirect.Status);

        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        using HttpClient client = new(new StaticHandler(_ => Response(HttpStatusCode.OK, Array.Empty<byte>())));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpdateDownloader(client).DownloadVerifiedAsync(manifest, cancelled.Token));
    }

    [Fact]
    public async Task UpdateCheck_RejectsManifestRedirect()
    {
        using HttpMessageHandler handler = new StaticHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://www.ooppp.com/elsewhere.json") } });
        UpdateOperationResult result = await UpdateHelper.CheckForVerifiedUpdateAsync(handler: handler);
        Assert.Equal(UpdateOperationStatus.UnsafeUrl, result.Status);
    }

    private static UpdateManifest Manifest(string version = "2.0.0", string sha256 = GoodHash) => new() { SchemaVersion = 1, Version = version, DownloadUrl = "https://www.ooppp.com/AIHelper.exe", Sha256 = sha256, Description = "test" };
    private static HttpResponseMessage Response(HttpStatusCode status, byte[] bytes) => new(status) { Content = new ByteArrayContent(bytes) };
    private static string StagedPath(UpdateManifest manifest) => Path.Combine(Path.GetTempPath(), "AIHelper", "updates", $"update-{manifest.Version}.exe");
    private static void TryDelete(string path) { if (File.Exists(path)) File.Delete(path); }
    private sealed class StaticHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(reply(request));
        }
    }
}
