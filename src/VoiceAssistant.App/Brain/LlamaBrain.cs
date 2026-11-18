using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using VoiceAssistant.App.Platform;
using VoiceAssistant.Core;

namespace VoiceAssistant.App.Brain;

/// <summary>
/// The AI brain on this PC: llama.cpp's llama-server running the model, started on demand on 127.0.0.1 with a random
/// port and key, ended with the app (it runs in a job object) or after a while unused. Replies are constrained to the
/// plan grammar and the shared instructions stay in the server's prompt cache. Requests and replies are never saved;
/// the log gets timings and token counts only.
/// </summary>
internal sealed class LlamaBrain : IBrainModel, IDisposable
{
    private const int ContextSize = 4096;
    private const string WarmUpScreen = "Windows (front first):\n1. \"Untitled - Notepad\" (Notepad), in front";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    private readonly BrainFiles _files;
    private readonly TimeSpan _keepLoaded;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Timer _idleTimer;
    private Process? _server;
    private Task? _starting;
    private Uri? _address;
    private string _key = string.Empty;
    private nint _job;
    private long _lastUsed = Environment.TickCount64;
    private volatile BrainAvailability _availability;

    public LlamaBrain(BrainFiles files, TimeSpan keepLoaded)
    {
        _files = files;
        _keepLoaded = keepLoaded;
        _availability = files.Installed ? BrainAvailability.Sleeping : BrainAvailability.NotInstalled;
        _idleTimer = new Timer(_ => UnloadIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public BrainAvailability Availability => _availability;

    public BrainFiles Files => _files;

    /// <summary>Raised on a worker thread when <see cref="Availability"/> changes.</summary>
    public event Action? AvailabilityChanged;

    /// <summary>Loads the model and fills its prompt cache in the background, so the first request is quick.</summary>
    public void WarmUp()
    {
        if (!_files.Installed || _shutdown.IsCancellationRequested)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await CompleteAsync(BrainPrompt.Messages(WarmUpScreen, "hello"), _shutdown.Token).ConfigureAwait(false);
                Log.Info("brain: warmed up");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Warn($"brain: warm-up failed: {ex.GetType().Name}");
            }
        });
    }

    /// <summary>After a download: notices the files.</summary>
    public void Refresh()
    {
        if (_availability == BrainAvailability.NotInstalled && _files.Installed)
        {
            Set(BrainAvailability.Sleeping);
        }
    }

    public async Task<string> CompleteAsync(IReadOnlyList<ChatTurn> messages, CancellationToken cancellationToken)
    {
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The model loads in the background: a cancelled request doesn't stop it, so the next one is quick.
            await EnsureStarted().WaitAsync(cancellationToken).ConfigureAwait(false);
            return await SendAsync(messages, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _lastUsed, Environment.TickCount64);
            _requests.Release();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _idleTimer.Dispose();
        StopServer();
        if (_job != 0)
        {
            Win32.CloseHandle(_job);
            _job = 0;
        }

        _http.Dispose();
    }

    private Task EnsureStarted()
    {
        lock (_gate)
        {
            if (_starting is { IsCompleted: false })
            {
                return _starting;
            }

            if (_server is { HasExited: false } && _availability == BrainAvailability.Ready)
            {
                return Task.CompletedTask;
            }

            _starting = Task.Run(StartServerAsync);
            return _starting;
        }
    }

    private async Task StartServerAsync()
    {
        StopServer();
        if (!_files.Installed)
        {
            Set(BrainAvailability.NotInstalled);
            throw new FileNotFoundException("The AI brain's files aren't on this PC.");
        }

        Set(BrainAvailability.Loading);
        var clock = Stopwatch.StartNew();
        try
        {
            var port = FreePort();
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var start = new ProcessStartInfo(_files.Server)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_files.Server)!,
            };
            foreach (var argument in new[]
            {
                "-m", _files.ModelPath, "--host", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture),
                "-c", ContextSize.ToString(CultureInfo.InvariantCulture), "-np", "1", "-ngl", "99", "--jinja", "--no-webui",
            })
            {
                start.ArgumentList.Add(argument);
            }

            // The key keeps other programs (and web pages) off the server; the environment hides it from command lines.
            start.Environment["LLAMA_API_KEY"] = key;
            var server = Process.Start(start) ?? throw new InvalidOperationException("llama-server didn't start.");

            // Its output is drained so it never blocks, and not kept: it could echo part of a request.
            server.OutputDataReceived += (_, _) => { };
            server.ErrorDataReceived += (_, _) => { };
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
            TieToThisApp(server);
            var address = new Uri($"http://127.0.0.1:{port}/");
            lock (_gate)
            {
                _server = server;
                _address = address;
                _key = key;
            }

            while (true)
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                if (server.HasExited)
                {
                    throw new InvalidOperationException($"llama-server exited with code {server.ExitCode}.");
                }

                try
                {
                    using var health = await _http.GetAsync(new Uri(address, "health"), _shutdown.Token).ConfigureAwait(false);
                    if (health.IsSuccessStatusCode)
                    {
                        break;
                    }
                }
                catch (HttpRequestException)
                {
                    // Not listening yet.
                }

                if (clock.Elapsed > StartTimeout)
                {
                    throw new TimeoutException("llama-server didn't get ready in time.");
                }

                await Task.Delay(250, _shutdown.Token).ConfigureAwait(false);
            }

            Set(BrainAvailability.Ready);
            Log.Info($"brain: {BrainFiles.ModelName} ready in {clock.ElapsedMilliseconds} ms (llama.cpp {BrainFiles.LlamaBuild}, {RuntimeInformation.ProcessArchitecture})");
        }
        catch (OperationCanceledException)
        {
            StopServer();
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"brain: couldn't start: {ex.GetType().Name} 0x{ex.HResult:X8}{(ex is InvalidOperationException or TimeoutException ? " " + ex.Message : string.Empty)}");
            StopServer();
            Set(BrainAvailability.Failed);
            throw;
        }
    }

    private async Task<string> SendAsync(IReadOnlyList<ChatTurn> messages, CancellationToken cancellationToken)
    {
        Uri address;
        string key;
        Process? server;
        lock (_gate)
        {
            address = _address ?? throw new InvalidOperationException("The AI brain isn't running.");
            key = _key;
            server = _server;
        }

        var body = new JsonObject
        {
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["temperature"] = 0,
            ["max_tokens"] = 300,
            ["cache_prompt"] = true,
            ["grammar"] = BrainPrompt.Grammar.Replace("\r", string.Empty, StringComparison.Ordinal),

            // Qwen3 and Gemma 4 templates think aloud before answering unless this is off.
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(address, "v1/chat/completions")) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (server is { HasExited: true })
        {
            Log.Error($"brain: server stopped (code {server.ExitCode})");
            Set(BrainAvailability.Failed);
            throw;
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"brain: server answered {(int)response.StatusCode}");
                throw new HttpRequestException($"llama-server answered {(int)response.StatusCode}.");
            }

            var json = JsonNode.Parse(text);
            if (json?["timings"] is { } t)
            {
                Log.Info($"brain: prompt {Num(t["prompt_n"]):F0} tokens ({Num(t["cache_n"]):F0} cached) in {Num(t["prompt_ms"]):F0} ms, reply {Num(t["predicted_n"]):F0} tokens in {Num(t["predicted_ms"]):F0} ms");
            }

            return json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? string.Empty;
        }
    }

    private static double Num(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;

    private void UnloadIfIdle()
    {
        if (_keepLoaded <= TimeSpan.Zero || _availability != BrainAvailability.Ready ||
            Environment.TickCount64 - Volatile.Read(ref _lastUsed) < _keepLoaded.TotalMilliseconds || !_requests.Wait(0))
        {
            return;
        }

        try
        {
            StopServer();
            Set(BrainAvailability.Sleeping);
            Log.Info($"brain: unloaded after {_keepLoaded.TotalMinutes:F0} minutes unused");
        }
        finally
        {
            _requests.Release();
        }
    }

    private void StopServer()
    {
        Process? server;
        lock (_gate)
        {
            server = _server;
            _server = null;
            _address = null;
        }

        if (server is null)
        {
            return;
        }

        try
        {
            if (!server.HasExited)
            {
                server.Kill();
                server.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        server.Dispose();
    }

    /// <summary>A job object kills the server when this app exits, even if it crashes.</summary>
    private void TieToThisApp(Process server)
    {
        if (_job == 0)
        {
            _job = Win32.CreateJobObject(0, 0);
            var info = new Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = Win32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (_job != 0 && !Win32.SetInformationJobObject(_job, Win32.JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                Win32.CloseHandle(_job);
                _job = 0;
            }
        }

        if (_job == 0 || !Win32.AssignProcessToJobObject(_job, server.Handle))
        {
            Log.Warn("brain: couldn't tie the server to the app; it is stopped on exit instead");
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private void Set(BrainAvailability availability)
    {
        if (_availability != availability)
        {
            _availability = availability;
            AvailabilityChanged?.Invoke();
        }
    }
}
