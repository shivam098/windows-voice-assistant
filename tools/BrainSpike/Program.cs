using System.Reflection;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length > 0 && args[0] == "api2")
{
    foreach (var t in typeof(FoundryLocalManager).Assembly.GetTypes().Where(t => t.Name.Contains("ResponseFormat") || t.Name.Contains("ToolChoice") || t.Name == "PropertyDefinition" || t.Name.Contains("JsonSchema")))
        Console.WriteLine(t.FullName + ": " + string.Join("; ", t.GetProperties().Select(p => p.PropertyType.Name + " " + p.Name)) + " | ctors " + string.Join(",", t.GetConstructors().Select(c => c.GetParameters().Length)));
    foreach (var t in typeof(Betalgo.Ranul.OpenAI.ObjectModels.RequestModels.ChatMessage).Assembly.GetTypes().Where(t => t.Name is "ResponseFormat" or "ToolDefinition" or "FunctionDefinition" or "PropertyDefinition" or "ToolCall" or "FunctionCall" or "JsonSchema"))
        Console.WriteLine(t.FullName + ": " + string.Join("; ", t.GetProperties().Select(p => p.PropertyType.Name + " " + p.Name)));
    return;
}
if (args.Length > 0 && args[0] == "api")
{
    var asm = typeof(FoundryLocalManager).Assembly;
    foreach (var n in new[] { "Microsoft.AI.Foundry.Local.OpenAIChatClient+ChatSettings", "Microsoft.AI.Foundry.Local.ModelInfo", "Microsoft.AI.Foundry.Local.IModel", "Microsoft.AI.Foundry.Local.Runtime", "Microsoft.AI.Foundry.Local.Configuration", "Microsoft.AI.Foundry.Local.ResponseFormatExtended", "Microsoft.AI.Foundry.Local.ToolChoice" })
    {
        var t = asm.GetType(n); Console.WriteLine($"== {n}");
        if (t == null) continue;
        foreach (var p in t.GetProperties()) Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
    }
    return;
}

if (args.Length > 2 && args[0] == "llama")
{
    // llama <server url> <label> [-v] [--no-think] [--schema]
    var (passed, median, max) = await Bench.RunAsync(Engines.LlamaServer(args[1], thinking: !args.Contains("--no-think"), schema: args.Contains("--schema")), args.Contains("-v"));
    Console.WriteLine($"== {args[2]}: {passed}/{Bench.Cases.Length} passed, median {median:F0} ms, slowest {max:F0} ms");
    return;
}

// Model cache (several GB per model). Default: %USERPROFILE%\.VoiceAssistantBrainSpike; BRAINSPIKE_CACHE moves it.
var cache = Environment.GetEnvironmentVariable("BRAINSPIKE_CACHE");
var config = string.IsNullOrEmpty(cache)
    ? new Configuration { AppName = "VoiceAssistantBrainSpike" }
    : new Configuration { AppName = "VoiceAssistantBrainSpike", AppDataDir = cache };
await FoundryLocalManager.CreateAsync(config, NullLogger.Instance);
var manager = FoundryLocalManager.Instance;
if (args.Length > 0 && args[0] == "eps") { var r = await manager.DownloadAndRegisterEpsAsync((ep, p) => Console.Write($"\r{ep} {p:F0}%   ")); Console.WriteLine(); Console.WriteLine($"ok={r.Success} status={r.Status} registered={string.Join(",", r.RegisteredEps ?? [])} failed={string.Join(",", r.FailedEps ?? [])}"); }
var catalog = await manager.GetCatalogAsync();
if (args.Length > 0 && args[0] == "cached")
{
    await manager.DownloadAndRegisterEpsAsync();
    foreach (var m in await catalog.ListModelsAsync())
        foreach (var v in m.Variants)
            if (await v.IsCachedAsync()) Console.WriteLine($"cached: {v.Id}");
    manager.Dispose();
    return;
}
if (args.Length > 1 && args[0] == "bench")
{
    await manager.DownloadAndRegisterEpsAsync();
    foreach (var id in args.Skip(1).Where(a => !a.StartsWith("-")))
    {
        var v = await catalog.GetModelVariantAsync(id) ?? throw new Exception("no variant " + id);
        if (!await v.IsCachedAsync())
        {
            var last = -1;
            await v.DownloadAsync(p => { if ((int)p / 10 != last) { last = (int)p / 10; Console.WriteLine($"  download {id} {p:F0}%"); } });
        }
        var load = System.Diagnostics.Stopwatch.StartNew();
        await v.LoadAsync();
        Console.WriteLine($"== {id}: loaded in {load.ElapsedMilliseconds} ms");
        var (passed, median, max) = await Bench.RunAsync(await Engines.FoundryAsync(v), args.Contains("-v"));
        Console.WriteLine($"== {id}: {passed}/{Bench.Cases.Length} passed, median {median:F0} ms, slowest {max:F0} ms");
        await v.UnloadAsync();
    }
    manager.Dispose();
    return;
}
foreach (var m in await catalog.ListModelsAsync())
{
    foreach (var v in m.Variants)
    {
        var info = v.GetType().GetProperty("Info")?.GetValue(v);
        string Get(string p) => info?.GetType().GetProperty(p)?.GetValue(info)?.ToString() ?? "";
        var rt = info?.GetType().GetProperty("Runtime")?.GetValue(info);
        string R(string p) => rt?.GetType().GetProperty(p)?.GetValue(rt)?.ToString() ?? "";
        Console.WriteLine($"{m.Alias,-28} {v.Id,-60} {R("DeviceType"),-4} {R("ExecutionProvider"),-28} {Get("FileSizeMb"),7} MB tools={Get("SupportsToolCalling")}");
    }
}
manager.Dispose();





