using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.IO;

namespace AIHelper.Helpers;

/// <summary>Downloads only policy-approved update artifacts and retains them only after hash verification.</summary>
internal sealed class UpdateDownloader
{
    private const int MaximumRedirects = 5;
    private readonly HttpClient _client;

    public UpdateDownloader(HttpClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<UpdateOperationResult> DownloadVerifiedAsync(UpdateManifest manifest, CancellationToken cancellationToken = default)
    {
        UpdateOperationResult validation = UpdateManifestValidator.Validate(manifest);
        if (validation.Status != UpdateOperationStatus.UpdateReady) return validation;
        Uri current = new(manifest.DownloadUrl!, UriKind.Absolute);
        string extension = Path.GetExtension(current.AbsolutePath).ToLowerInvariant();
        string directory = Path.Combine(Path.GetTempPath(), "AIHelper", "updates");
        string stagedPath = Path.Combine(directory, $"update-{manifest.Version}{extension}");
        try
        {
            Directory.CreateDirectory(directory);
            DeleteStagedArtifact(stagedPath);
            for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
            {
                using HttpRequestMessage request = new(HttpMethod.Get, current);
                using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (IsRedirect(response.StatusCode))
                {
                    if (redirect == MaximumRedirects || response.Headers.Location is null)
                        return new(UpdateOperationStatus.UnsafeUrl, "Update download redirect is invalid or exceeds the permitted limit.");
                    Uri next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
                    if (!UpdateSecurityPolicy.IsAllowedDownloadUri(next))
                        return new(UpdateOperationStatus.UnsafeUrl, "Update download redirect targets an unapproved URL.");
                    current = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    return new(UpdateOperationStatus.DownloadFailed, $"Update download failed with HTTP {(int)response.StatusCode}.");

                await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (FileStream destination = new(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                {
                    await source.CopyToAsync(destination, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                    destination.Flush(flushToDisk: true);
                }
                byte[] actual;
                await using (FileStream stream = new(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    actual = await SHA256.HashDataAsync(stream, cancellationToken);
                if (!UpdateManifestValidator.HashMatches(manifest.Sha256!, actual))
                {
                    DeleteStagedArtifact(stagedPath);
                    return new(UpdateOperationStatus.HashMismatch, "Downloaded update failed SHA-256 verification.");
                }
                return new(UpdateOperationStatus.UpdateReady, "Verified update artifact is ready.", manifest, stagedPath);
            }
            return new(UpdateOperationStatus.UnsafeUrl, "Update download redirect limit exceeded.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            DeleteStagedArtifact(stagedPath);
            return new(UpdateOperationStatus.DownloadFailed, $"Update download failed: {ex.Message}");
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    private static void DeleteStagedArtifact(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* Integrity failures never execute the artifact; cleanup is best effort. */ }
    }
}
