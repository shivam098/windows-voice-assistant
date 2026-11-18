using System.IO.Compression;
using System.Runtime.InteropServices;

namespace VoiceAssistant.App.Brain;

/// <summary>
/// The AI brain's files: llama.cpp's server for this processor (ARM64: the Adreno GPU build, which also runs on the CPU;
/// x64: the Vulkan build, which falls back to the CPU) and the Qwen3 4B Instruct model. Downloaded once on request from
/// their official releases, each verified by SHA-256 (docs/THIRD-PARTY-NOTICES.md). Paths in settings override the
/// downloaded ones; files there are never downloaded over or deleted.
/// </summary>
internal sealed class BrainFiles
{
    public const string LlamaBuild = "b12483";
    public const string ModelName = "Qwen3 4B";

    private const string ModelFile = "Qwen3-4B-Instruct-2507-Q4_0.gguf";

    // Q4_0 is the format llama.cpp's Adreno GPU code is tuned for (docs/design/ai-brain.md, spike 2).
    private static readonly Download Model = new(
        "https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q4_0.gguf",
        "e0ba675d86ab277c61701c6793659b2ae801d95e3be791464c321e6fbf613be2",
        2_375_773_280);

    private static readonly Download Arm64Server = new(
        $"https://github.com/ggml-org/llama.cpp/releases/download/{LlamaBuild}/llama-{LlamaBuild}-bin-win-opencl-adreno-arm64.zip",
        "9e9561f2bc49aca709e753837613937bff8b56f723cc98b4462e2d7b80e11cf8",
        14_796_009);

    private static readonly Download X64Server = new(
        $"https://github.com/ggml-org/llama.cpp/releases/download/{LlamaBuild}/llama-{LlamaBuild}-bin-win-vulkan-x64.zip",
        "7859e96ba91201a41e2769d4456fe86e8968ba93c6794eeb4fe49b7537a17628",
        38_502_442);

    private readonly bool _serverChosen;
    private readonly bool _modelChosen;

    public BrainFiles(string? folder, string? serverPath, string? modelPath)
    {
        Folder = string.IsNullOrWhiteSpace(folder) ? Path.Combine(AppPaths.DataDir, "brain") : Environment.ExpandEnvironmentVariables(folder.Trim());
        _serverChosen = !string.IsNullOrWhiteSpace(serverPath);
        _modelChosen = !string.IsNullOrWhiteSpace(modelPath);
        Server = _serverChosen ? Environment.ExpandEnvironmentVariables(serverPath!.Trim()) : Path.Combine(Folder, $"llama.cpp-{LlamaBuild}", "llama-server.exe");
        ModelPath = _modelChosen ? Environment.ExpandEnvironmentVariables(modelPath!.Trim()) : Path.Combine(Folder, ModelFile);
    }

    private sealed record Download(string Url, string Sha256, long Size);

    public string Folder { get; }

    public string Server { get; }

    public string ModelPath { get; }

    public bool Installed => File.Exists(Server) && File.Exists(ModelPath);

    /// <summary>Bytes a download would fetch now (what's missing at the default paths).</summary>
    public long MissingBytes =>
        (!_serverChosen && !File.Exists(Server) ? ServerDownload.Size : 0) + (!_modelChosen && !File.Exists(ModelPath) ? Model.Size : 0);

    private static Download ServerDownload => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? Arm64Server : X64Server;

    /// <summary>Downloads (resuming) and verifies what's missing. Progress is (bytes so far, bytes in all).</summary>
    public async Task DownloadAsync(Action<long, long> progress, CancellationToken cancellationToken)
    {
        if ((_serverChosen && !File.Exists(Server)) || (_modelChosen && !File.Exists(ModelPath)))
        {
            throw new FileNotFoundException("A file chosen in settings.json (brainServerPath or brainModelPath) doesn't exist.");
        }

        Directory.CreateDirectory(Folder);
        var total = MissingBytes;
        var done = 0L;
        if (!File.Exists(Server))
        {
            var server = ServerDownload;
            var zip = Path.Combine(Folder, $"llama-{LlamaBuild}.zip");
            await ModelDownloader.DownloadVerifiedAsync(server.Url, server.Sha256, zip, (received, _) => progress(done + received, total), cancellationToken).ConfigureAwait(false);

            // Only this app's own folder inside the brain folder is ever replaced.
            var target = Path.GetDirectoryName(Server)!;
            var unpacking = target + ".unpacking";
            if (Directory.Exists(unpacking))
            {
                Directory.Delete(unpacking, recursive: true);
            }

            ZipFile.ExtractToDirectory(zip, unpacking);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(unpacking, target);
            File.Delete(zip);
            done += server.Size;
        }

        if (!File.Exists(ModelPath))
        {
            await ModelDownloader.DownloadVerifiedAsync(Model.Url, Model.Sha256, ModelPath, (received, _) => progress(done + received, total), cancellationToken).ConfigureAwait(false);
        }
    }
}
