# BrainSpike

Measures local language models as Jarvis's planner: 20 requests with screen summaries, checked automatically.
Not part of the app build.

Two engines:

```powershell
# Foundry Local (downloads models into its cache; set BRAINSPIKE_CACHE to put the cache on another drive)
dotnet run -- list                          # catalog: every model variant, device, size, tool support
dotnet run -- cached                        # variants already downloaded
dotnet run -- bench qwen3-4b-generic-gpu:2  # download if needed, load, run the 20 cases (-v prints every plan)

# llama.cpp: start llama-server with a GGUF model first, then point the bench at it
llama-server.exe -m model.gguf --port 8080 -c 4096 --jinja -ngl 99   # Adreno OpenCL build; drop -ngl for CPU
dotnet run -- llama http://127.0.0.1:8080 my-label -v --no-think      # --no-think turns off Gemma 4 / Qwen3 thinking
```

Aion Instruct Preview (Windows' on-device NPU model) has its own runner: `tools/AionBench`.

Results: `docs/design/ai-brain.md`.
