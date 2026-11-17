# BrainSpike

Measures local language models (Foundry Local) as Jarvis's planner: 20 requests with screen summaries, checked
automatically. Not part of the app build.

```powershell
dotnet run -- list                          # catalog: every model variant, device, size, tool support
dotnet run -- bench qwen3-4b-generic-gpu:2  # download if needed, load, run the 20 cases (-v prints every plan)
```

Results: `docs/design/ai-brain.md`.
