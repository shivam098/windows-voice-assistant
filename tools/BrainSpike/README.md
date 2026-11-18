# BrainSpike

Measures local language models as Jarvis's planner: 25 requests with screen summaries, checked automatically (the
spikes' tables in `docs/design/ai-brain.md` ran the first 20; the 21st is lab 3's real screen; 22–25, opening
Instagram with Chrome in front, came from the 19 Nov report). Plans are checked after `BrainPlan.Tidy`, as the app runs them.
It uses the app's own prompt, grammar and plan reader (`BrainPrompt`, `BrainPlan` in `src/VoiceAssistant.Core`), so a
change to the prompt is measured here before it ships. Not part of the app build.

Two engines:

```powershell
# Foundry Local (downloads models into its cache; set BRAINSPIKE_CACHE to put the cache on another drive)
dotnet run -- list                          # catalog: every model variant, device, size, tool support
dotnet run -- cached                        # variants already downloaded
dotnet run -- bench qwen3-4b-generic-gpu:2  # download if needed, load, run the cases (-v prints every plan)

# llama.cpp (what the app ships): start llama-server with a GGUF model first, then point the bench at it
llama-server.exe -m model.gguf --port 8080 -c 4096 -np 1 --jinja -ngl 99   # Adreno OpenCL build; drop -ngl for CPU
dotnet run -- llama http://127.0.0.1:8080 my-label -v --no-think            # --no-think turns off Gemma 4 / Qwen3 thinking
```

Aion Instruct Preview (Windows' on-device NPU model) has its own runner: `tools/AionBench`.

Results: `docs/design/ai-brain.md`.
