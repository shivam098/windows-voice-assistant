# Platform porting guide

How each capability maps to platform APIs. Windows is implemented; the other columns are the plan and the known
limits. **Verify the starred (\*) items with a spike before committing to a design.**

## Architecture to keep

| Layer | Windows (reference) | Port |
|---|---|---|
| Core behaviour: parser, matcher, resolver, state machine, formatter, segmenter, live preview | C# `src/VoiceAssistant.Core` (no OS calls) | Re-implement in the platform language (Swift / Kotlin), **porting the unit tests with the same inputs and expected outputs**. Alternatively share the C# core via .NET on macOS/Android if the app shell allows it |
| Speech | whisper.cpp via Whisper.net; Silero via whisper.cpp | whisper.cpp directly (C API): Metal / Core ML on Apple, NDK + optional GPU on Android; same models and SHA-256s |
| Platform layer | `IDesktop` (`src/VoiceAssistant.Core/IDesktop.cs`) implemented with Win32 + UI Automation | Same operations with the platform's accessibility/input APIs |
| Shell | WPF tray app, pill window, marker overlay | Menu-bar app (macOS), foreground service + overlay (Android), app + keyboard (iOS) |

## Capability map

| Capability | Windows | macOS | Android | iOS |
|---|---|---|---|---|
| **Push-to-talk hotkey** | Low-level keyboard hook: hold Ctrl+Win | `CGEventTap` on `flagsChanged`: hold **Fn/Globe** (Input Monitoring permission) | Not global. Floating button or a keyboard (IME) mic key | Keyboard extension key (\*keyboards can't use the microphone: hand off to the container app to record) |
| **Always-listening wake word** | Microphone stream in the tray app | Same (menu-bar app) | Foreground service with microphone type and a persistent notification | Not allowed in the background; in-app only |
| **Insert text** | Clipboard paste (history/cloud excluded) or `SendInput` Unicode | `NSPasteboard` + Cmd+V via `CGEvent` (mark transient/concealed types); or `AXUIElementSetAttributeValue` on `kAXSelectedTextAttribute` | `ACTION_SET_TEXT` / paste on the focused node, or an IME that commits text | Keyboard extension `insertText` |
| **Password field check** | UIA `IsPassword` | `kAXSecureTextFieldSubrole`; `IsSecureEventInputEnabled()` | `AccessibilityNodeInfo.isPassword()` | Secure fields block custom keyboards automatically |
| **Elevated / protected targets** | Process elevation check | Secure input mode (above) | Not applicable | Not applicable |
| **Foreground window identity** | `GetForegroundWindow` + process id | `NSWorkspace.frontmostApplication` + `AXFocusedWindow` | `AccessibilityWindowInfo` of the active window | Not available |
| **Open / switch app** | `ShellExecute`, `SetForegroundWindow` | `NSWorkspace.openApplication`, `NSRunningApplication.activate` | Launch intent; switching via recents is limited | Not available (URL schemes only) |
| **List clickable elements** | UIA cached `FindAll` (buttons, links, tabs, list/tree/menu items, grid rows/cells such as WhatsApp Web's chat list; grid rows get a real click, since a programmatic select only highlights them) | `AXUIElement` tree (`AXButton`, `AXLink`, `AXRadioButton`, `AXRow`, …) | `AccessibilityNodeInfo` tree (`isClickable`) | Not available |
| **Click** | UIA Invoke/Select/Toggle/ExpandCollapse; `SendInput` mouse fallback | `AXPress` action; `CGEvent` mouse fallback | `performAction(ACTION_CLICK)`; `dispatchGesture` tap fallback | Not available |
| **Browser tabs** | UIA `TabItem` in the top row of the tab strip; Select | Chrome/Edge expose tabs to AX (\*); Safari via AppleScript `current tab` (Automation permission) | Chrome tabs aren't listed outside the tab switcher (\*): offer “next/previous tab” only | Not available |
| **Focus a text box** | UIA `SetFocus`, click fallback, verify focused element | `kAXFocusedAttribute = true`, verify `AXFocusedUIElement` | `ACTION_FOCUS` / `ACTION_CLICK`, verify `findFocus(FOCUS_INPUT)` | Not available |
| **Number badges / outline overlay** | Click-through topmost WPF window | Borderless `NSPanel`, `ignoresMouseEvents`, high window level | `TYPE_ACCESSIBILITY_OVERLAY` | Not available |
| **Status pill** | Topmost no-activate WPF window | Non-activating `NSPanel` | Accessibility overlay or bubble | In-app / keyboard UI |
| **Key shortcuts** (“copy”, “new tab”) | `SendInput` chords with Ctrl | `CGEvent` with Cmd | Global actions (back/home/recents) and node actions; most chords unavailable | Not available |
| **Clipboard hygiene** | `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory = 0`, `CanUploadToCloudClipboard = 0` | `org.nspasteboard.TransientType`, `ConcealedType` | Avoid the clipboard; use `ACTION_SET_TEXT` | Not needed (keyboard inserts directly) |

## Permissions to request (and explain in onboarding)

| Platform | Permissions |
|---|---|
| Windows | Microphone (Settings › Privacy › Microphone › desktop apps). No admin rights. |
| macOS | Microphone; Accessibility (control other apps, read UI); Input Monitoring (Fn hotkey); Automation per browser if AppleScript is used for Safari tabs |
| Android | Record audio; Accessibility service (with an honest, accessibility-focused description, as Play policy requires); foreground service (microphone); overlay if the bubble is used |
| iOS | Microphone (app); Full Access for the keyboard only if it must talk to the app |

## Feature availability by platform

| Feature | Windows | macOS | Android | iOS |
|---|---|---|---|---|
| Push-to-talk dictation into any app | ✅ | ✅ | ✅ (IME / button) | ✅ (keyboard, via app hand-off \*) |
| Live preview | ✅ | ✅ | ✅ | ✅ (in keyboard/app) |
| Hindi + English | ✅ | ✅ | ✅ | ✅ |
| Wake word + voice commands | ✅ | ✅ | ✅ (foreground service) | ⚠️ in-app only |
| Open / switch apps | ✅ | ✅ | ⚠️ open only | ❌ |
| Tabs by position/name | ✅ | ✅ (\* Chrome AX, Safari AppleScript) | ⚠️ next/previous only | ❌ |
| Click by name, numbers, confirmations | ✅ | ✅ | ✅ | ❌ |
| Start typing in a named box | ✅ | ✅ | ✅ | ❌ |

## Suggested order for macOS

1. Swift Package with the core logic + XCTest suites ported from `VoiceAssistant.Core.Tests` (parser, matcher, resolver,
   formatter, segmenter, controller with a fake desktop).
2. Menu-bar app: AVAudioEngine 16 kHz capture, whisper.cpp xcframework (Metal) with the same models, Silero VAD.
3. Fn push-to-talk + paste insertion + pill + live preview (the dictation-only app could ship here).
4. Wake word, core commands, `AXUIElement` desktop layer.
5. Navigation: AX scans, AXPress, tabs, overlay badges; run [acceptance-tests.md](acceptance-tests.md) N1–N9 with the
   same HTML pages in `tests/NavigationLab` opened in Chrome and Safari.
6. Build on GitHub's macOS runners; test on a real Mac (microphone, permissions and accessibility can't be emulated).
