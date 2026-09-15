using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.IO;
using HandyControl.Controls;
using Serilog;

namespace AIHelper.Helpers;

public static class UpdateHelper
{
    private static readonly Uri UpdateManifestUri = new("https://www.ooppp.com/soft/aihelper.json", UriKind.Absolute);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task CheckUpdateAsync()
    {
        try
        {
            UpdateOperationResult result = await CheckForVerifiedUpdateAsync();
            if (result.Status == UpdateOperationStatus.NoUpdate) return;
            if (!result.IsReady)
            {
                Log.Warning("Update check rejected: {Category}", result.Status);
                return;
            }
            Application.Current?.Dispatcher.Invoke(() =>
            {
                string prompt = $"发现新版本：V{result.Manifest!.Version}\n\n【更新内容】\n{result.Manifest.Description}\n\n更新包已验证，是否打开？";
                if (HandyControl.Controls.MessageBox.Show(prompt, "🎉 发现新版本", MessageBoxButton.YesNo, MessageBoxImage.Asterisk) == MessageBoxResult.Yes)
                    OpenVerifiedLocalArtifact(result);
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warning(ex, "Update check failed."); }
    }

    internal static async Task<UpdateOperationResult> CheckForVerifiedUpdateAsync(CancellationToken cancellationToken = default, HttpMessageHandler? handler = null)
    {
        if (!UpdateSecurityPolicy.IsAllowedManifestUri(UpdateManifestUri))
            return new(UpdateOperationStatus.UnsafeUrl, "Configured update manifest URL is not approved HTTPS.");
        using HttpClientHandler? ownedHandler = handler is null ? new HttpClientHandler { AllowAutoRedirect = false } : null;
        using HttpClient client = handler is null ? new HttpClient(ownedHandler!) : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(10);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, UpdateManifestUri);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                return new(UpdateOperationStatus.UnsafeUrl, "Update manifest redirects are not permitted.");
            if (!response.IsSuccessStatusCode)
                return new(UpdateOperationStatus.DownloadFailed, $"Update manifest request failed with HTTP {(int)response.StatusCode}.");
            UpdateManifest? manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(await response.Content.ReadAsStreamAsync(cancellationToken), JsonOptions, cancellationToken);
            UpdateOperationResult validation = UpdateManifestValidator.Validate(manifest);
            if (validation.Status != UpdateOperationStatus.UpdateReady) return validation;
            if (!ApplicationVersionProvider.IsRemoteNewer(manifest!.Version!, ApplicationVersionProvider.Current))
                return new(UpdateOperationStatus.NoUpdate, "No newer verified update is available.", manifest);
            return await new UpdateDownloader(client).DownloadVerifiedAsync(manifest, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException) { return new(UpdateOperationStatus.InvalidManifest, "Update manifest JSON is invalid."); }
        catch (Exception ex) { return new(UpdateOperationStatus.UnexpectedError, $"Update check failed: {ex.Message}"); }
    }

    private static void OpenVerifiedLocalArtifact(UpdateOperationResult verifiedResult)
    {
        if (!verifiedResult.IsReady) throw new InvalidOperationException("Only a verified update artifact can be opened.");
        string path = verifiedResult.VerifiedArtifactPath!;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new InvalidOperationException("Verified update artifact is unavailable.");
        string extension = Path.GetExtension(path).ToLowerInvariant();
        Uri local = new(path);
        if (!string.Equals(local.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)
            || (extension is not ".exe" and not ".zip"))
            throw new InvalidOperationException("Verified update artifact type is not permitted.");
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true })?.Dispose();
    }
}
