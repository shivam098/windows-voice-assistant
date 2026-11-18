# Design: an AI brain for Jarvis (general-purpose requests)

Status: **Brain v1 built** (18 Nov 2026): on-device with llama.cpp and Qwen3 4B Instruct 2507, chosen from the
measurements below. Cloud fallback, spoken replies and follow-ups are next (see [Brain v1](#brain-v1-what-was-built)).

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

- At most 8 steps and 30 seconds per request. **Esc** cancels at once (v1; a spoken "stop" waits behind the running
  request).
- **Confirm first** before anything that sends, deletes, buys, closes unsaved work or types more than 200 characters.
  The pill asks; "yes" goes ahead (v1 doesn't take Enter, which would go to the app in front).
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

Decided (18 Nov 2026): Brain v1 runs **Qwen3 4B Instruct 2507 on llama.cpp** (the user's choice from the table above),
with the cloud fallback later. Licences checked: llama.cpp is MIT, Qwen3-4B-Instruct-2507 and unsloth's GGUF are
Apache-2.0 (docs/THIRD-PARTY-NOTICES.md); neither is bundled, Settings downloads them on request.

## Brain v1: what was built

```
"open the MrBeast video on YouTube"
   │ CommandParser: no command (or a single open/go to/click whose name matches nothing)
   ▼
InteractionController.AskBrainAsync  (Core, platform-independent, unit-tested)
   ├─ ScreenSummary: windows front to back + items of up to 2 windows (front, named in the request, next)
   ├─ BrainPrompt: the measured system prompt + 3 examples + "Screen: … Request: …"
   ├─ IBrainModel.CompleteAsync ──► LlamaBrain (App): llama-server on 127.0.0.1, random port + key, job object
   ├─ BrainPlan.Parse: lenient JSON, known tools only, ≤ 8 steps
   └─ run each step with the existing abilities; safety gates in code (BrainSafety)
```

- **Where it plugs in.** Unrecognised phrases go to the brain instead of “Not a command I know”. So does a single
  open / go to / click command that finds no app, tab or on-screen name (the MrBeast request parses as “open an app
  called *the MrBeast video on YouTube*”). With the brain on, a description of 3+ words needs a strong tab match, so it
  no longer just switches to the YouTube tab.
- **Prompt.** The spikes' prompt, now in `BrainPrompt` (Core) and shared by `tools/BrainSpike` and `tools/AionBench`,
  so the bench measures what ships. Re-measured through the app's prompt and plan reader (with the bench's missing
  line break before item lists fixed): Qwen3 4B 2507 on the Adreno GPU, **17/20 correct, 16 clean, median 2.5 s**
  (same three failures as before). One rule changed after lab 3: “Only press enter or click Send/Delete when the user
  asked to send or delete” made the model read “clear my watch history” as not asking to delete, and on the real
  screen it clicked the browser's bookmark star instead (replayed offline from the captured screen). The rule now says
  to click the item that deletes, clears or removes (the PC asks first), to press Enter or Send only when asked to
  send, and never to click anything unrelated. The bench gained that case (the real screen, private names replaced):
  **18/21 before and after the change, same three failures, median 2.5 s**; the replay of the real screen now clicks
  “Delete history”, which asks for a yes.
- **Screen summary.** The bench's layout. Real pages list hundreds of items, so the summary reads at most two windows
  (the front one, then the one the request names, else the next) and keeps 14 items each: items sharing words with
  the request, then text boxes and content-like names (titles, messages, files), in reading order. Generic words
  (“window”, “tab”) don't count as naming a window. Names are flattened to one line and stripped of double quotes so
  page text can't fake a new “Request:” line.
- **Model output.** llama.cpp's grammar sampler enforces the plan's exact shape (`BrainPrompt.Grammar`: no whitespace,
  fixed key order, ≤ 6 steps, ≤ 100 characters per field). Temperature 0; `-c 4096 -np 1` so the one slot keeps the
  shared prefix (instructions and examples, about 540 tokens) in its prompt cache.
- **Lifecycle.** Downloaded only from Settings (llama.cpp b12483: Adreno OpenCL build on ARM64, Vulkan on x64; the
  Q4_0 GGUF), each file verified by SHA-256, resuming after interruptions. Loaded in the background once the speech
  models are ready, unloaded after `brainKeepLoadedMinutes` (60) unused, reloaded on the next request. The server dies
  with the app (job object); its output is drained, never saved.
- **Safety, enforced in code** (`BrainSafety`, `InteractionController.Brain`; the full list in
  `specs/voice-commands.md`):
  - a “yes” before destructive or send-like buttons, files that start programs, Enter outside search boxes, closing
    and menu keys, and typing over 200 characters; the plan resumes after “yes”;
  - no typing or keys in terminals, the Run box, File Explorer, Start search or the Registry Editor; in browsers,
    typing only inside the page; no clipboard or menu keys; Win shortcuts other than arranging windows refused; only
    http(s) links to real host names;
  - ≤ 8 steps, 30 s of acting, 60 s to plan; Esc (the dictation key hook) cancels;
  - logs carry tool names, counts and timings only (tested).
- **Typing target.** Text the brain types binds that box like “start typing” does (without switching to typing), so
  “send it” and “scratch that” work on it next.
- **Hardened after review** (two rounds of independent code review of v1, all fixed with tests):
  - only speech addressed to Jarvis goes to the brain (the wake word, or the phrase right after “Hey Jarvis”), above
    the command confidence threshold, so conversation in the 30 s awake window isn't acted on;
  - address bars are told apart by structure, not by name: in a browser the brain types only where the focus is inside
    the page (a UI Automation Document), so the address bar is refused in any language and page fields such as “Email
    address” aren't; typing an address into a search box is refused (the new-tab page's box opens it);
  - File Explorer and the desktop get no typing or keys (its address bar starts programs, Shift+F10 then D deletes),
    and opening any file there needs a yes (known extensions are hidden); the Run box is an Explorer window in any
    language; Task Manager and PowerToys Run are covered too, as are terminals and consoles inside apps (VS Code's);
  - keys can't get round the typing checks: clipboard keys and menu keys (Alt, F10, Shift+F10, Alt+Space) are refused,
    Alt+letter asks, and a letter pressed into a box counts as typing;
  - Enter (with any modifier), Space and Alt+S are judged by what has the focus: after the plan typed a message there,
    any click or send key needs a yes (typing into a search box afterwards can't clear it); in any other box that isn't
    a search box (it may hold a draft) or on a focused Send/Delete button they ask too, and so does an unreadable focus;
  - 200 characters is a total per plan; program files (.exe, .ps1…) need a yes;
  - a page that opens without its browser in front stops the plan; `localhost.`, `.local.` and numeric hosts with a
    trailing dot (`0x7f.1.`) are refused; window titles never reach the log.

Measured live on this PC (18 Nov 2026, Snapdragon X Elite, self-test lab pages that look like YouTube opened in the
user's Chrome, the app's real UI Automation scans; five lab runs):
- Download: llama.cpp (14 MB) fetched, verified and unpacked; the model loads in 7–14 s. Resuming: a download stopped
  at 6.3 MB carried on from 6.3 MB when started again, and the SHA-256 matched.
- **“Hey Jarvis, open the MrBeast video on YouTube”** with PowerShell in front and the YouTube page in Chrome behind:
  Chrome came to the front and the video opened (the original bug), in every run. “Play the video about the twisted
  bridge” opened the Veritasium video. “Type dir in this window and press enter” in PowerShell: refused. “What's the
  weather in Bangalore?”: Google results in Edge.
- **“Clear my watch history”** on a page with a “Delete history” button: the first prompt read “clear” as not asking
  to delete; one run offered numbers for three matches, the next clicked Chrome's bookmark star (the bookmark was
  removed through Chrome afterwards). After the prompt change: “Click ‘Delete history’? Say ‘yes’…”, and “no”
  cancelled with nothing clicked.
- **Address bar**: the app's own focus check on Chrome's real address bar reads it as outside the page and an address
  bar, and the page's search box as inside the page, so typing there is refused (the refusal itself is unit-tested).
  In the runs, requests to type into the address bar put the text in the wrong field of the plan, so the app chose the
  page's only text box; the closing sentence (the model's own) still claimed “address bar”.
- **Time from “Thinking…” to the first action: 3.2–6.4 s, median 4.8 s** over 13 requests (the first request after
  the model loads is the slowest): screen read 50–150 ms; prompt 160–360 new tokens in 0.8–1.7 s with the instructions
  cached; plan 30–63 tokens in 2–4 s, about 16–19 tokens/s. The first version's screen summary (3 windows, 50 items,
  ~540 tokens) took 8.8 s, so it was cut to 2 windows and 28 items.
- llama-bench on this PC: the Adreno GPU and the CPU read prompts at the same speed (249 vs 254 tokens/s) and the CPU
  writes a little faster (24.5 vs 21.2 tokens/s). The GPU is kept so the CPU stays free for Whisper.
- **Chrome limitation (existing, not the brain's):** a *new* Chrome window exposes only the browser's own buttons to UI
  Automation, not the page, so the brain can't see its links (the first lab run failed this way). Pages in windows
  that were already open are fine. Tracked with the navigation lab.

Next:
1. **Spoken replies** ("Opening it"): needs the microphone muted while it speaks, or Jarvis hears itself.
2. **Cloud fallback** (the hybrid design above): offer it when the local plan fails or the request is long or vague.
3. **Follow-ups and re-planning:** remember the last request ("no, the second one"), and look at the screen again
   after a step opens a new page instead of planning everything from one look.
4. **x64:** the Vulkan download works in principle but hasn't been measured on an Intel or AMD PC.
