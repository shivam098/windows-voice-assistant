using System.Security.Cryptography;

namespace VoiceAssistant.App;

/// <summary>
/// Downloads missing speech models on first use (portable zips ship without them), verifying SHA-256
/// before use. Hashes are the Hugging Face LFS object ids (or of our own release asset); a mismatched download is deleted.
/// </summary>
internal static class ModelDownloader
{
    private static readonly Dictionary<string, (string Url, string Sha256)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ggml-small.en-q8_0.bin"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en-q8_0.bin", "67a179f608ea6114bd3fdb9060e762b588a3fb3bd00c4387971be4d177958067"),
        ["ggml-base.en-q8_0.bin"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en-q8_0.bin", "a4d4a0768075e13cfd7e19df3ae2dbc4a68d37d36a7dad45e8410c9a34f8c87e"),
        ["ggml-base.en.bin"] = ("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin", "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002"),
        ["ggml-silero-v6.2.0.bin"] = ("https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin", "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987"),

        // Oriserve/Whisper-Hindi2Hinglish-Swift (Apache-2.0), converted with tools/ModelConvert and quantized to q8_0.
        ["ggml-hinglish-swift-q8_0.bin"] = ("https://github.com/shivam098/windows-voice-assistant/releases/download/models-hinglish-swift-1/ggml-hinglish-swift-q8_0.bin", "0cf6e7077971d9aa43963b04bf764a47600e873f7e0f3f0f9742323560171245"),

        // Oriserve/Whisper-Hindi2Hinglish-Apex (Apache-2.0; fine-tuned from Whisper large-v3-turbo), same recipe.
        ["ggml-hinglish-apex-q8_0.bin"] = ("https://github.com/shivam098/windows-voice-assistant/releases/download/models-hinglish-apex-1/ggml-hinglish-apex-q8_0.bin", "0b4324d2c1ad64f20883ee7fcd5d2bb0a8466287dc70d74bc47066200c28c719"),
    };

    public static bool CanDownload(string file) => Known.ContainsKey(file);

    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Downloads <paramref name="file"/> into <paramref name="directory"/>; reports whole-percent progress.</summary>
    public static async Task DownloadAsync(string file, string directory, Action<int> progress, CancellationToken cancellationToken)
    {
        var (url, expected) = Known[file];
        Directory.CreateDirectory(directory);
        var lastPercent = -1;
        await DownloadVerifiedAsync(url, expected, Path.Combine(directory, file), (received, total) =>
        {
            var percent = total > 0 ? (int)(received * 100 / total) : 0;
            if (percent != lastPercent)
            {
                lastPercent = percent;
                progress(percent);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="target"/> through “target.partial”, resuming a partial file
    /// left by an interrupted download, and moves it into place only if its SHA-256 matches (otherwise it is deleted).
    /// Progress is (bytes so far, total bytes).
    /// </summary>
    public static async Task DownloadVerifiedAsync(string url, string sha256, string target, Action<long, long> progress, CancellationToken cancellationToken)
    {
        var partial = target + ".partial";
        var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        // Big files take many minutes on a slow connection: no overall timeout, but a minute without any data gives up
        // (the next attempt resumes).
        try
        {
            await ReceiveAsync(url, partial, have, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The download stopped receiving data.");
        }

        string actual;
        await using (var stream = File.OpenRead(partial))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        }

        if (actual != sha256)
        {
            File.Delete(partial);
            throw new InvalidDataException($"Downloaded {Path.GetFileName(target)} failed verification and was deleted.");
        }

        File.Move(partial, target, overwrite: true);
    }

    private static async Task ReceiveAsync(string url, string partial, long have, Action<long, long> progress, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        }

        stall.CancelAfter(StallTimeout);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && have > 0)
        {
            // The partial file is already complete: it is verified next.
            progress(have, have);
            return;
        }

        response.EnsureSuccessStatusCode();
        if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            // The server ignored the range: start over.
            have = 0;
        }

        var total = have + (response.Content.Headers.ContentLength ?? 0);
        await using var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
        await using var destination = new FileStream(partial, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var buffer = new byte[1 << 16];
        var received = have;
        var lastReport = 0L;
        int read;
        while ((read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
        {
            stall.CancelAfter(StallTimeout);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            if (received - lastReport >= 1 << 20 || received == total)
            {
                lastReport = received;
                progress(received, total);
            }
        }
    }
}
