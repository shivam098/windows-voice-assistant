using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;
using VoiceAssistant.Core;

namespace VoiceAssistant.App;

/// <summary>
/// Runs a script of transcripts through the real controller and Windows automation (no microphone),
/// so desktop flows can be verified end to end. Script lines:
///   listen                 start listening (standby / command mode)
///   say: &lt;transcript&gt;      as if Whisper heard it (confidence 0.9)
///   ptt: &lt;transcript&gt;      as if pushed-to-talk
///   wait &lt;ms&gt;
///   fg                     record the foreground process name
///   read                   record the focused control's type, text length and hash (not its text)
///   expect: &lt;text&gt;         PASS/FAIL: does the focused control contain the script's phrase (\n = line break)
///   insert Paste|Type      switch the insertion method
///   clipboard              record a fingerprint (formats, hash, length) of the clipboard, not its text
///   # comment
/// Results are written to the output file; it never contains user text, window titles, or clipboard contents.
/// </summary>
internal static class SelfTest
{
    public static async Task RunAsync(string scriptPath, string outputPath, Dispatcher ui)
    {
        var output = new StringBuilder();
        var settings = AppSettings.LoadOrCreate();
        using var host = new AssistantHost(settings, ui, persistMemory: false);
        host.Status += s => output.AppendLine($"   [{s.Mode}] {s.Kind}: {s.Message}");
        try
        {
            host.StartForSelfTest();
            foreach (var raw in await File.ReadAllLinesAsync(scriptPath).ConfigureAwait(false))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                output.AppendLine($"> {line}");
                if (line == "listen")
                {
                    await host.RunOnQueueAsync(() => { host.Controller.SetListening(true); return Task.CompletedTask; }).ConfigureAwait(false);
                }
                else if (line.StartsWith("say:", StringComparison.Ordinal))
                {
                    var text = line[4..].Trim();
                    await host.RunOnQueueAsync(() => host.Controller.HandleTranscriptAsync(text, 0.9f)).ConfigureAwait(false);
                }
                else if (line.StartsWith("ptt:", StringComparison.Ordinal))
                {
                    var text = line[4..].Trim();
                    await host.RunOnQueueAsync(() => { host.Controller.InsertDictation(text); return Task.CompletedTask; }).ConfigureAwait(false);
                }
                else if (line.StartsWith("wait ", StringComparison.Ordinal))
                {
                    await Task.Delay(int.Parse(line[5..])).ConfigureAwait(false);
                }
                else if (line.StartsWith("typing ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ');
                    Platform.KeyboardInput.TypingChunk = int.Parse(parts[1]);
                    Platform.KeyboardInput.TypingDelayMs = int.Parse(parts[2]);
                }
                else if (line.StartsWith("insert ", StringComparison.Ordinal))
                {
                    Platform.WindowsDesktop.InsertMethod = line[7..].Trim();
                }
                else if (line == "clipboard")
                {
                    output.AppendLine($"   clipboard fingerprint: {ClipboardFingerprint()}");
                }
                else if (line == "fg")
                {
                    // Process name only: window titles can contain the user's document or page names.
                    var fg = new Platform.WindowsDesktop().GetForegroundWindow();
                    output.AppendLine($"   foreground: {fg?.ProcessName}");
                }
                else if (line == "read")
                {
                    var text = ReadFocusedText();
                    output.AppendLine($"   focused: {text.Type} len={text.Value.Length} hash={Hash(text.Value)}");
                }
                else if (line.StartsWith("expect:", StringComparison.Ordinal))
                {
                    // Records only whether the script's own phrase is present, never the field's other contents.
                    var expected = line[7..].Trim().Replace("\\n", "\r", StringComparison.Ordinal);
                    var actual = ReadFocusedText().Value;
                    var pass = actual.Contains(expected, StringComparison.Ordinal);
                    output.AppendLine($"   {(pass ? "PASS" : "FAIL")}: focused text {(pass ? "contains" : "does not contain")} the expected phrase (field len={actual.Length})");
                }

                await Task.Delay(400).ConfigureAwait(false);
            }

            // Let queued UI status posts land.
            await Task.Delay(500).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            output.AppendLine($"!! {ex.GetType().Name}: {ex.Message}");
        }

        await File.WriteAllTextAsync(outputPath, output.ToString()).ConfigureAwait(false);
    }

    /// <summary>Formats + a hash of the text, so restoration can be verified without recording the user's clipboard.</summary>
    private static string ClipboardFingerprint()
    {
        var result = string.Empty;
        var t = new Thread(() =>
        {
            var data = System.Windows.Clipboard.GetDataObject();
            var formats = data is null ? "(empty)" : string.Join(",", data.GetFormats(false).Take(6));
            var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty;
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
            result = $"formats=[{formats}] textHash={hash} len={text.Length}";
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    private static string Hash(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    private static (string Type, string Value) ReadFocusedText()
    {
        try
        {
            var task = Task.Run(() =>
            {
                var e = AutomationElement.FocusedElement;
                var type = e.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal);
                if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var v))
                {
                    return (type, ((ValuePattern)v).Current.Value);
                }

                if (e.TryGetCurrentPattern(TextPattern.Pattern, out var t))
                {
                    return (type, ((TextPattern)t).DocumentRange.GetText(4000));
                }

                return (type, string.Empty);
            });
            return task.Wait(2000) ? task.Result : ("(timeout)", string.Empty);
        }
        catch (Exception ex)
        {
            return ($"(error {ex.GetType().Name})", string.Empty);
        }
    }
}
