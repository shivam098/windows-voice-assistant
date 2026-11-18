# Design: an AI brain for Jarvis (general-purpose requests)

Status: proposed, 18 Nov 2026. Not built yet.

## The problem

On 18 Nov at 01:12, "Hey Jarvis, open the MrBeast video on YouTube" failed. YouTube was in Chrome on the left and
PowerShell was on the right. The log shows:

```
clickable scan: 0 of 0 in 6 ms
command OpenApp failed
unrecognized command   (×3, the rephrasings)
```

Jarvis understands a **fixed list of phrases** (`CommandParser`). "Open …" always means "start an app called …", so it
looked for an app named "the MrBeast video", found none, and stopped. The UI scan ran on the window in front, which
had nothing clickable. The rephrasings matched no known phrase. The fixed grammar is fast and safe, but anything
outside it fails, so Jarvis doesn't feel intelligent.

## The idea

Keep the fixed commands as the **fast path** (instant, offline, predictable). When a request doesn't match, hand it
to a **language model that plans steps using the actions Jarvis already has**, then carry out the plan step by step.

```
speech ─► Whisper ─► fixed grammar ──match──► run (as today)
                          │
                       no match
                          ▼
            Brain: request + screen summary + tool list
                          │  plan (tool calls)
                          ▼
            Executor: one step at a time, re-scan, check result
                          │
            pill shows the plan, Jarvis answers ("Playing it")
```

### What the model sees (and nothing more)

- The words of the request.
- A **screen summary** built from UI Automation, which Jarvis already scans in 26–70 ms. It includes the open windows
  (process name, title, position: left / right / maximized, which is in front) and, for the target window, its
  clickable items (name, role, rough position). It is capped at about 150 items.
- No screenshots, no audio, and nothing from password fields or elevated windows. Those are already refused.

### Tools (existing abilities, exposed as functions)

| Tool | Backed by |
|---|---|
| `focus_window(match)` | window switching, including "the one on the left" |
| `open_app(name)` | `OpenApp` |
| `click(name, near?)` | click by name, with the same matching as "click New on the left" |
| `type_text(text)` / `press(keys)` | dictation insertion, `KeyboardInput` |
| `browser_search(site?, query)` / `open_url(url)` | address bar commands |
| `scroll(direction)` | new: mouse wheel over the target |
| `read_screen()` | re-scan after each step |
| `ask_user(question, choices?)` | pill and speech, with the answer by voice |
| `say(text)` | spoken and shown reply |

For the failing example, the plan would be:
1. `focus_window("Chrome, left")`
2. `read_screen()`: a video tile named "… MrBeast …" is found.
3. `click("MrBeast")`

If the video isn't on screen: `browser_search("youtube", "MrBeast")`, then `click` on the first result. If several
match: `ask_user("Which one?", [titles])`.

### Safety rules (enforced in code, not by the model)

- At most 8 steps and 30 seconds per request. **Esc** or "stop" cancels at once.
- **Confirm first** before anything that sends, deletes, buys, closes unsaved work or types more than 200 characters.
  The pill asks; "yes" or Enter goes ahead.
- Never act in password fields, elevated windows, or the lock screen (the existing refusals stay).
- Each step is checked against a fresh scan. If the screen isn't what the plan expected, Jarvis stops and says so; it
  doesn't guess.
- The log records tool names and success or failure, never words or titles (as today).

## The brain: three options

| | On-device (Phi Silica) | Cloud (e.g. Azure OpenAI) | Hybrid (recommended) |
|---|---|---|---|
| Privacy | Words never leave the PC | Request text and on-screen item names go online | On-device unless you allow cloud for a request type |
| Cost | Free | Per request (cents) | Mostly free |
| Smarts | Good for short, concrete requests | Best for multi-step and vague requests | Both |
| Speed | NPU; check in the spike | Network round trip | Fast path first |
| Works offline | Yes | No | Yes (on-device part) |

- **Phi Silica** is Windows' built-in local model. It comes **pre-installed and runs on the NPU on Copilot+ PCs** (like
  this Snapdragon X laptop) through the Windows AI APIs in the Windows App SDK; on other Windows 11 PCs with a
  supported GPU it downloads on first use (several GB). Source: Microsoft Learn, "Get started with Phi Silica", updated
  Oct 2026.
- **Recommended: hybrid.** Phi Silica plans by default. If it can't produce a valid plan, or the request is long or
  vague, Jarvis offers cloud help ("Ask the online model? It sends your words and the names on screen"). A setting
  can make that always or never. This keeps "offline by default" true.

## Risks to check in a spike (about a day)

1. **Package identity.** The Windows AI APIs may need the app to have package identity. Voice Assistant is an
   unpackaged WPF app, so a sparse package or MSIX may be needed. Check this first.
2. **Plan quality** of Phi Silica on 20 real requests ("open the MrBeast video", "reply thanks to the last WhatsApp
   message", "find the PR from yesterday"). Measure valid-plan rate and time to the first action.
3. **Output format.** If tool calling isn't native, use a strict JSON plan schema with validation and one retry.

## Phases

1. **Spike** (above). Decide on-device vs hybrid with numbers.
2. **Brain v1:** the screen summary, tools, executor, confirmations and spoken replies, for single-window requests.
   Tests: a plan executor with fake tools in `VoiceAssistant.Core.Tests`; real-app labs (YouTube in Chrome, Notepad,
   WhatsApp Web) run only while the user is idle.
3. **v2:** multi-window, follow-ups ("no, the second one"), and remembering the last target ("play it", "pause").
4. Bring the same brain to ThumbFree's planned desktop apps.

## Spike results (18 Nov 2026, Snapdragon X Elite laptop, Foundry Local 1.2.4)

20 realistic requests with screen summaries (`tools/BrainSpike`), checked automatically. "Clean" means correct with no
needless steps. Times are the median per request, from the request to the full plan.

| Model (Foundry Local) | Runs on | Download | Correct | Clean | Median time |
|---|---|---|---|---|---|
| Qwen3 4B | GPU | 2.9 GB | **14/20** | **11/20** | 11.9 s |
| Qwen3 4B | CPU | 2.8 GB | worse (stopped early) | | 11–15 s |
| Qwen3.5 2B text | GPU | 1.4 GB | 8–10/20 | 8/20 | 6.4 s |
| Qwen2.5 7B | NPU | 7.0 GB | 7/20 (often right but broken JSON) | 6/20 | 5.5 s |
| Qwen2.5 1.5B | NPU | 1.1 GB | 2/20 | 2/20 | 1.6 s |
| Gemma 4 E2B | CPU | 6.0 GB | could not load: runtime too old (`per_layer_inputs`) | | |
| Phi Silica | NPU | built in | not tried: needs a Limited Access token; retired Jan 2027 | | |

Findings:
- **JSON constraint:** Foundry Local's C# SDK sends a typed JSON schema that the runtime ignores. Passing the schema
  through a Lark grammar (`start: %json {...}`) enforces it on CPU and GPU. The NPU (QNN) path ignores grammars
  entirely, so NPU output can be malformed.
- **Speed is the blocker.** No local model on this PC today is both good enough (at least 18/20) and fast enough
  (under 2–3 s). The NPU is fast but its models are weaker and unconstrained; the GPU/CPU 4B models are smarter but
  take about 12 s.
- **Next on-device candidates:** Aion Instruct (Windows' NPU model replacing Phi Silica: no token, preview package
  and an unpackaged WPF sample at aka.ms/tryaion, Insider rollout Dec 2026) and Gemma 4 E2B/E4B once Foundry Local's
  runtime supports them. Until then, the hybrid design's cloud path is what makes Jarvis feel smart.

## Spike 2 results (18 Nov 2026): llama.cpp and Aion Instruct

Same laptop, same 20 cases and checks.
- **llama.cpp** b12483 (`llama-server`, Windows ARM64). "GPU" means its OpenCL Adreno build with every layer on the GPU. Thinking is off unless noted.
- **Aion Instruct Preview** 1.1.0.2 (the framework package, NPU), through `tools/AionBench`.
- Medians are measured with the shared instructions and examples already in llama.cpp's prompt cache. Aion starts a new context for each request.
- "JSON schema" means llama.cpp's own grammar for the schema. "Compact grammar" is `Engines.CompactGrammar`: the same shape with no whitespace and the keys in a fixed order.

| Engine: model (download) | Runs on | Output constraint | Correct | Clean | Median | Slowest |
|---|---|---|---|---|---|---|
| llama.cpp: Qwen3 4B Instruct 2507, Q4_0 (2.4 GB) | GPU | JSON schema | **17/20** | **16/20** | **2.2 s** | 6.1 s |
| ″ | GPU | compact grammar | 17/20 | 16/20 | 2.4 s | 6.1 s |
| llama.cpp: Gemma 4 E4B QAT Q4_0 (5.2 GB) | GPU | JSON schema | **17/20** | **17/20** | 11.5 s | 27.1 s |
| ″ | GPU | compact grammar | 14/20 | 14/20 | 3.4 s | 5.7 s |
| llama.cpp: Gemma 4 E2B QAT Q4_0 (3.3 GB) | GPU | JSON schema | 15/20 | 15/20 | 2.4 s | 8.0 s |
| ″ | GPU | compact grammar | 15/20 | 15/20 | 2.2 s | 5.9 s |
| ″ | CPU | JSON schema | 15/20 | 15/20 | 4.4 s | 14.4 s |
| ″, thinking on (the default) | CPU | JSON schema | 14/20 | 14/20 | 7.8 s | 20.2 s |
| Aion Instruct Preview 1.1.0.2 (2.3 GB) | NPU | none (the API has none) | 7/20 | 7/20 | 2.4–2.7 s | 4.3 s |
| *Spike 1 best:* Foundry Local Qwen3 4B | GPU | Lark grammar | 14/20 | 11/20 | 11.9 s | |

Measurements:
- **Engine matters as much as model.** Qwen3 4B took 11.9 s through Foundry Local (WebGPU) and 2.2 s through llama.cpp
  (OpenCL), with 14 and 17 correct. The checkpoints differ: Foundry has the original Qwen3 4B, while llama.cpp ran the
  2507 Instruct update.
- **Gemma 4 thinks first by default.** It writes a reasoning section before the JSON. With thinking off
  (`chat_template_kwargs.enable_thinking=false`, the bench's `--no-think`), E2B on the CPU went from 7.8 s to 4.4 s and
  from 14 to 15 correct. The CPU runs shared the processor with downloads.
- **E4B pretty-prints under llama.cpp's JSON-schema grammar** (newlines and indentation), producing 90–300 output
  tokens per plan at about 12 tokens/s. With the compact grammar its median fell from 11.5 s to 3.4 s, but it got 14
  right instead of 17. The compact grammar didn't change Qwen's or E2B's scores.
- **Aion Instruct Preview:**
  - 5 of 20 replies were broken JSON, because the preview API can't constrain output.
  - Several valid plans were wrong decisions: it searched YouTube to mute a video, and clicked the tab instead of closing it.
  - Loading took 16–22 s; requests ran at 2.4–2.7 s on the NPU. Two runs gave identical scores.
- **Safety rule confirmed:** for "reply thanks to Rahul on WhatsApp", Qwen pressed Enter, sending the reply without
  being asked. The confirm-before-send rule must be enforced in code (see Safety rules).
- **Strict checks:** two failures are arguably acceptable:
  - E2B pressed Enter instead of clicking Search.
  - Qwen asked for clarification in `say` instead of using the `ask` tool.

  The checks are unchanged so that all models stay comparable.
- **No local model reached 18/20.** The closest at a usable speed is Qwen3 4B 2507 on llama.cpp's GPU build: 17/20 at
  2.2 s.

Open questions (not yet decided):
- Why does E4B lose 3 cases under the compact grammar? Possible reasons:
  - The fixed key order: when free, it wrote `say` before `steps`.
  - Losing the whitespace tokens changes what it generates.
  - Three borderline cases flipped.
- Which on-device engine and model Jarvis v1 uses. The licences of the model and llama.cpp need checking before shipping.
