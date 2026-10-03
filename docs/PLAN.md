# FFXIV JP/EN Chat Translator — Planning Notes

Dalamud plugin for English speakers on Japanese data centers. Two-pane chat
window (original left, translation right) plus an English input box that
produces a Japanese translation, shows a breakdown for confirmation, and sends
the Japanese on a second Enter.

Research date: 2026-10-03.

---

## 1. Toolchain (current state of Dalamud)

| Item | Value |
|---|---|
| Dalamud release | v15 / API level 15 (2026-04-29) |
| .NET | 10.0 (`net10.0-windows`) |
| Project SDK | `<Project Sdk="Dalamud.NET.Sdk/15.0.0">` — supplies Dalamud, FFXIVClientStructs, Lumina, ImGui bindings, DalamudPackager |
| ImGui bindings | `Dalamud.Bindings.ImGui` (ImGui.NET is gone since v13); `ImRaii` in `Dalamud.Interface.Utility.Raii` |
| Dev Dalamud path | `%AppData%\XIVLauncher\addon\Hooks\dev\` (override with `DALAMUD_HOME`) |
| Dev load | `/xlsettings` → Experimental → Dev Plugin Locations → `/xlplugins` → Dev Tools |
| Distribution | custom repo `repo.json` (array of manifests with `DownloadLinkInstall` → `latest.zip`) |

Minimal csproj:

```xml
<Project Sdk="Dalamud.NET.Sdk/15.0.0">
  <PropertyGroup>
    <Version>0.1.0.0</Version>
    <Name>JpEnChat</Name>
    <Author>colliexiv</Author>
    <Punchline>Two-pane JP/EN chat translation for English players on JP servers.</Punchline>
    <Description>...</Description>
    <RepoUrl>https://github.com/colliexiv/ffxivjpentranslate</RepoUrl>
    <Tags>chat;translation;japanese</Tags>
  </PropertyGroup>
</Project>
```

Only NuGet dependency beyond the SDK: none required. `System.Text.Json` and
`HttpClient` ship with .NET.

Official-repo note: Dalamud's publishing rules reject "entirely AI-generated
submissions" and anything that sends automatically. A user-confirmed send of a
sanitized message is what ChatTwo does and is accepted. Plan on a custom repo
first; decide on official submission later.

---

## 2. What ChatTranslated does and why it feels slow

Source: kelvin124124/ChatTranslated master @ 73c8e8f (v3.6.1.3), ~2,900 lines
of C#. Key facts:

- **Hook**: `IChatGui.ChatMessage` (API 15 `IHandleableChatMessage`). Each
  message becomes an `async void` fire-and-forget task. No queue, no ordering.
- **Pipeline per line**: phrase filter → language detection (default is an
  *online* round-trip via Yandex/Google/Bing) → translation (DeepL unofficial
  endpoint → DeepL API → MS → Google fallback chain, or LLM) → print.
  Detection alone adds a full HTTP request before translation starts. The
  20 s HTTP timeout and fallback chain can stack to tens of seconds.
- **Output**: prints a second `[CT] Sender: translation` line into the game
  chat log via `IChatGui.Print` (Echo channel by default), seconds after the
  original. That is the "already pushed past" problem: the translated line is
  detached from the original and lands wherever the log is by then. Its own
  window is a single read-only `InputTextMultiline` log with
  `orig || translated` on one line.
- **Cache**: in-memory LRU of 120 entries keyed by original text only,
  persisted to JSON.
- **Outgoing**: it does **not** send chat. The main window translates typed
  text and offers a Copy button; the user pastes manually.
- **Bloat to drop**: GTranslate (Google/Bing/Yandex scrapers), Lingua
  language-detection models (plus an MSBuild task that strips them), DeepL
  endpoints, the author's proxy with embedded CI secret, 8 resx
  localizations, phrase JSON, glyph-folding for the game font, setup wizard,
  IPC, PF context menu.

---

## 3. Target architecture

```
IChatGui.ChatMessage ──► Ingest (framework thread, <1 ms)
   │  copy LogKind, sender, payloads → ChatLine{Id, Ts, Kind, Sender, Orig, Lang}
   │  push to window immediately (translation column shows "…")
   ▼
Gate (client-side, no network)
   ├─ script check: has kana/kanji? → JA, else skip (or EN→JA mode off)
   ├─ normalize → exact cache hit? → fill translation column, done
   ├─ per-sender debounce (~250–400 ms): more lines from same sender arriving
   │   within the window are merged into one batch (macro burst)
   └─ emits TranslationJob{lines[]}
   ▼
Translator (Task.Run, bounded concurrency, per-job CancellationToken)
   OpenRouter chat completion, streaming, fast model
   ▼
IFramework.RunOnFrameworkThread → update ChatLine.Translation, write cache
```

### 3.1 Incoming hook (API 15)

```csharp
ChatGui.ChatMessage += OnChatMessage;   // or ChatMessageUnhandled like ChatTwo

void OnChatMessage(IHandleableChatMessage m) {
    if (m.IsHandled) return;
    if (!Config.Channels.Contains(m.LogKind)) return;
    var sender = m.Sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
    var line = new ChatLine {
        Kind = m.LogKind, Timestamp = m.Timestamp,
        SenderName = sender?.PlayerName ?? m.Sender.TextValue,
        SenderWorld = sender?.World.Value.Name ?? PlayerState.CurrentWorld.Value.Name,
        Original = ExtractText(m.Message),   // TextPayload + AutoTranslatePayload.Text, links as placeholders
    };
    Log.Add(line);           // visible instantly with empty right column
    Gate.Enqueue(line);
}
```

Channels to cover: Say 10, Shout 11, Yell 30, Party 14, Alliance 15,
FreeCompany 24, Ls1–8 16–23, CrossLinkShell1 37, CrossLinkShell2–8 101–107,
TellIncoming 13, TellOutgoing 12, NoviceNetwork 27, PvPTeam 36, CrossParty 32,
CustomEmote 28. Own messages: skip when sender matches
`IPlayerState.CharacterName`, except keep TellOutgoing visible.

Chat history on load: Dalamud has no API, but FFXIVClientStructs
`RaptureLogModule.GetLogMessageDetail(index, …)` can be iterated on the
framework thread to backfill the last N lines. Nice-to-have for v2.

### 3.2 Gate: do it client-side, not with a second LLM

The three decisions in the original idea (cache hit, standalone vs. wait,
macro grouping) are all cheaper and faster to make locally. A second LLM call
costs a full round-trip (150–300 ms minimum) before the real translation even
starts, which is the exact latency the plugin is trying to remove.

1. **Cache hit**: normalize (NFKC, trim, collapse whitespace, strip trailing
   `www`/`ｗ`/`！`/`。` runs, lowercase for EN) and look up an exact key.
   Dictionary + LRU, persisted to `ConfigDirectory/cache.json`. Key must
   include direction (`ja→en` / `en→ja`). Hit rate on macros, greetings,
   `おつ`, `よろしく`, raid callouts is very high.
2. **Standalone vs. wait**: per-sender debounce. First line from a sender
   starts a 250–400 ms timer. Lines arriving during the timer join the batch.
   Timer fires → send the batch. A single normal message pays at most the
   debounce once; a macro's lines arrive within a few frames so they always
   land in one batch. Make the delay configurable.
3. **Macro grouping**: falls out of the debounce. Additionally, a batch is
   sent to the model as numbered lines and the model returns numbered lines,
   so context is shared but each line is still mapped back to its own row.
   Cache each line individually afterward.

Optional later: a tiny structured-output call (`gpt-5-nano` minimal
reasoning, `max_tokens≈10`) only for ambiguous cases. Not in v1.

Language detection: no library needed. Count Hiragana (3040–309F), Katakana
(30A0–30FF), CJK (4E00–9FFF). Any present → JA. Else if Latin letters → EN.
That is enough for a JA↔EN tool.

### 3.3 Translator

- One static `HttpClient` with `HTTP-Referer` and `X-Title` headers.
- Bounded concurrency (SemaphoreSlim, 2–3 in flight). Jobs carry a
  `CancellationToken`; cancel if the window is closed or plugin unloads.
- **Streaming** (`stream: true`) so the right column fills token by token.
  First JP/EN token shows in roughly 200–400 ms on a flash-lite model.
- Request body:

```json
{
  "model": "google/gemini-3.8-flash",
  "models": ["google/gemini-3.8-flash", "google/gemini-3.5-flash-lite"],
  "reasoning": {"effort": "low", "exclude": true},
  "messages": [
    {"role":"system","content":"<static prompt + glossary>"},
    {"role":"user","content":"1: ...\n2: ...\n3: ..."}
  ],
  "temperature": 0.2, "max_tokens": 300, "stream": true,
  "provider": {"sort":"latency","allow_fallbacks":true,"data_collection":"deny"}
}
```

- System prompt: FFXIV context, keep names/job abbreviations/tells intact,
  output one line per input line with the same number, no commentary, no
  reasoning. Keep the prefix byte-identical so provider prompt caching
  engages once it exceeds ~1k tokens (add a glossary of FFXIV terms to get
  there usefully).
- **Gemini 3.8 Flash thinks by default (level `medium`) and that is the
  entire latency problem.** Artificial Analysis measures ~13 s time to first
  token at the default level; OpenRouter's own p50 with the best provider is
  ~0.76 s. `minimal` is rejected with a 400 on 3.7/3.8 Flash and thinking
  cannot be fully disabled, so `reasoning.effort: "low"` (OpenRouter maps it
  to Google's `thinking_level`) is the floor. `exclude: true` keeps thought
  tokens out of the stream. Expect roughly 0.5–1.0 s to first token; if that
  is still too slow in practice, fall back to `google/gemini-3.5-flash-lite`
  (~0.3–0.7 s, no thinking tax) and keep 3.8 Flash for the outgoing EN→JA
  flow where quality matters more than speed.
- Other latency knobs that stack with the above: `stream: true`, short
  `max_tokens`, `provider.sort: "latency"`, `:nitro` suffix for priority
  endpoints (billed at priority rate), and a system prompt that forbids
  commentary so output is just the lines.
- Error handling: 429 → backoff with `X-RateLimit-*`; 402 → surface
  "credits/key limit" in the window; timeout 8 s, no fallback chains to
  other services.

### 3.4 Model/cost shortlist (OpenRouter list prices, USD per 1M in/out)

| Model | TTFT (p50) | Price | Notes |
|---|---|---|---|
| google/gemini-3.8-flash | ~0.76 s at `effort: low`; ~13 s at default | 0.75 / 3.75 | **Chosen default.** User-verified JA quality. Must send low effort. |
| google/gemini-3.5-flash-lite | ~0.3–0.7 s | 0.30 / 2.50 | Fallback / swap-in if 3.8 feels slow |
| google/gemini-2.5-flash-lite | ~0.15–0.3 s | 0.10 / 0.40 | Cheapest, no thinking |
| openai/gpt-4.1-mini | ~0.4 s | 0.40 / 1.60 | Non-Google fallback |
| anthropic/claude-haiku-4.5 | ~0.2–0.6 s | 1.00 / 5.00 | Best quality, 10x flash-lite |

Cost at ~2,000 lines per evening, ~80 tokens in + ~40 out each (plus the
cached system prompt): about $0.30/evening on 3.8 Flash, under $0.05 on
2.5 flash-lite. Model is a config field, so this is tunable without a
rebuild. The plugin logs ms-to-first-token per request so the user can
compare models in their own conditions.

Two API keys: OpenRouter supports unlimited keys per account, each with its
own credit limit and reset period. If the gate stays client-side only one key
is needed. Keep the config field for a second key anyway so a classifier can
be added later without a schema change.

---

## 4. UI (decided)

Single `Window` from `Dalamud.Interface.Windowing`, flags `NoScrollbar`,
`SizeConstraints` with a sensible minimum. Decisions:

- Two-pane aligned table, original left, translation right, one row per
  message. No tabs in v1; a channel filter dropdown with checkboxes instead.
- Vanilla chat stays visible in v1. The user shrinks it to a strip. Hiding it
  is a v2 toggle.
- Input block is pinned to the bottom of the window. The breakdown panel
  expands *above* the input box (not a popup), so the log is pushed up and
  nothing is covered.
- Enter in the input = translate; Enter again = send; Esc = cancel/edit;
  Ctrl+Enter = send without re-translate after manual edits to the JA text.
- Sent messages appear in the log as their own row (left: EN draft, right:
  JA sent) with a distinct color.
- Right cell shows `…` until the first token, then streams; a failed request
  shows `⚠ retry` as a clickable text in the cell.
- Hover on a right cell shows a tooltip with the full original (useful for
  long macro batches that were wrapped).
- Font: Axis at a configurable size, default 14 px; one slider for both
  panes.

```
┌─ JP/EN Chat ───────────────────────────────────────────────┐
│ [Say][Party][FC][LS1]…  tabs or channel filter             │
│ ┌ original (left) ───────────┬ translation (right) ──────┐ │
│ │ 21:03 [P] Tanaka: よろしく   │ 21:03 nice to meet you    │ │
│ │ 21:03 [P] Suzuki: 1ボス行きます│ …                         │ │
│ └────────────────────────────┴───────────────────────────┘ │
│ EN> [ let's pull the first boss                 ] (Enter)  │
│ ┌ 1ボスいきましょう                                        │ │
│ │  1ボス = first boss · いきましょう = let's go (polite)      │ │
│ │  back: "Let's go to the first boss."    [Enter=send Esc] │ │
│ └──────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────┘
```

- **Rows stay aligned**: each `ChatLine` is one table row with both cells,
  so a slow translation never detaches from its original. The row appears
  on arrival; the right cell shows `…` then streams in. This is the core fix
  for "messages already pushed past".
- `ImGui.BeginTable` with two stretch columns inside
  `ImRaii.Child("log", new Vector2(0, -inputBlockHeight))`. Auto-scroll:
  `atBottom = GetScrollY() >= GetScrollMaxY()` before drawing; after drawing
  `if (atBottom && added) SetScrollHereY(1f)`.
- Text colors per `XivChatType` matching the game's defaults.
- Japanese glyphs: Dalamud's default font is the game AXIS font, which
  already includes Japanese. For larger text:
  `UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis18))`
  and `using (handle.Push())`.
- Input: `ImGui.InputText("##en", ref buf, 500, ImGuiInputTextFlags.EnterReturnsTrue)`.
  Dalamud's built-in IME handling covers any Japanese typed directly.
- Channel selector: dropdown or `/p ` style prefix typed into the box
  (parse leading `/p`, `/s`, `/fc`, `/l1`, `/t Name@World`). Default to the
  last used channel, same as the game.
- Keybind: `/jpchat` command to toggle/focus; optionally detect Enter while
  no ImGui item is active to focus the input. Hiding the vanilla ChatLog
  (ChatTwo sets `AtkUnitBase->IsVisible = false` on `ChatLog` and
  `ChatLogPanel_0..3` each frame) is a v2 option; v1 leaves vanilla chat
  alone and the user shrinks it.

### 4.1 Outgoing flow

1. User types English, presses Enter → state `Translating`. Request EN→JA
   with structured output:

```json
{"type":"json_schema","json_schema":{"name":"outgoing","strict":true,"schema":{
  "type":"object","properties":{
    "ja":{"type":"string"},
    "segments":{"type":"array","items":{"type":"object","properties":{
       "ja":{"type":"string"},"reading":{"type":"string"},"en":{"type":"string"}},
       "required":["ja","reading","en"],"additionalProperties":false}},
    "back":{"type":"string"},
    "register":{"type":"string","enum":["casual","polite"]}
  },"required":["ja","segments","back","register"],"additionalProperties":false}}}
```

   Use `provider.require_parameters: true` so only endpoints supporting
   `json_schema` are routed. Gemini flash-lite, GPT-4.1-mini, Haiku 4.5 all
   support it.
2. Breakdown panel shows `ja`, segment glosses with readings, and the
   back-translation. Politeness toggle (casual/polite) re-requests.
3. Enter again → send. Esc → back to editing. Allow editing the JA text in
   place before sending.
4. Send via FFXIVClientStructs on the framework thread:

```csharp
unsafe void Send(string text) {
    var bytes = Encoding.UTF8.GetBytes(text);
    if (bytes.Length == 0 || bytes.Length > 500) throw new ArgumentException("0 < len <= 500 bytes");
    var u = Utf8String.FromString(text);
    u->SanitizeString((AllowedEntities)0x27F);
    if (u->ToString() != text) { u->Dtor(true); throw new ArgumentException("invalid chars"); }
    UIModule.Instance()->ProcessChatBoxEntry(u);
    u->Dtor(true);
}
Framework.RunOnFrameworkThread(() => Send("/p " + ja));
```

   500 UTF-8 bytes ≈ 166 Japanese characters. Show a byte counter and block
   send when over. No sigscan needed; `ProcessChatBoxEntry` is a
   `[MemberFunction]` in current ClientStructs (this is what ECommons and
   ChatTwo use).
5. Echo the sent line into the local log (left: EN draft, right: JA sent) and
   add the pair to the cache in both directions.

---

## 5. Config and secrets

- `Configuration : IPluginConfiguration` saved with `SavePluginConfig`.
  Fields: enabled channels, debounce ms, model, fallback models, max
  concurrency, politeness default, font size, window layout, cache enabled,
  hide vanilla chat (v2).
- API keys: Dalamud config is plaintext JSON. Store keys with Windows DPAPI
  (`ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)`)
  base64-encoded; never log them. Config window takes the key via
  `ImGuiInputTextFlags.Password`.
- Logging through `IPluginLog`; verbose level logs request timing (ms to
  first token, ms total) so latency can be tuned in the field.

---

## 6. Threading rules

- Chat events arrive on the framework thread. Copy what is needed and
  return immediately.
- Network in `Task.Run`; results marshalled back with
  `IFramework.RunOnFrameworkThread`, or appended to a `ConcurrentQueue`
  drained at the top of `Draw`.
- ImGui calls only inside `UiBuilder.Draw`. ClientStructs calls
  (`ProcessChatBoxEntry`, `RaptureLogModule`, addons) only on the framework
  thread.

---

## 7. Milestones

| # | Scope | Output | State |
|---|---|---|---|
| M0 | Skeleton: SDK csproj, `/jpchat`, empty window, config, DPAPI key storage | loads in dev mode | done (Phase 1) |
| M1 | Ingest + two-pane log with `…` placeholder, script-based language detect | rows appear instantly | done (Phases 2A, 2B, 3) |
| M2 | OpenRouter streaming translation, bounded concurrency, timing logs | right column fills in <0.5 s typical | done (Phase 2A) |
| M3 | Normalized exact cache, per-sender debounce/batching | macros translated as a unit, repeats instant | done (Phase 2A) |
| M4 | Outgoing: EN input → structured JA + breakdown → confirm → `ProcessChatBoxEntry` | can chat in JP | done (Phases 2A, 2B, 3) |
| M5 | Polish: channel colors, tabs/filters, byte counter, politeness toggle, `repo.json` release | installable from custom repo | done (Phases 2B, 3) |
| P4 | Vanilla chat integration: translate English typed into the game's chat box in a popup; chat-bar button | | done (Phase 4, §9) |
| v2 | Hide vanilla chat, backfill history from `RaptureLogModule`, optional LLM gate for ambiguous bursts, glossary editor | | open |

"Done" means implemented, building with zero warnings and unit-tested; in-game verification is still pending (README,
"Known unverified").

### 7.1 Implementation notes (deviations from this plan)

- **Reasoning effort**: `Configuration.ReasoningEffort` defaults to `"low"`; an empty string omits the `reasoning`
  object entirely, for models without thinking.
- **`max_tokens`**: not a fixed 300. Incoming batches use `clamp(1024 + 3 × chars + 8 × lines, 1024, 4096)`, outgoing
  uses 4096, because Gemini 3.x reasoning tokens count against `max_tokens` and a tight cap truncated the answer.
  Billing is per generated token, so the headroom is free.
- **Debounce caps**: the per-sender timer restarts on every line, but a batch waits at most 3 × the debounce after its
  first line, and a batch of 10 lines is sent immediately, so a chatty sender cannot starve their own translations.
  `MaxConcurrency` is read once at load.
- **Enter while confirming**: Enter in the EN box sends when the English is unchanged since the translation (so
  "Enter, Enter" sends even if focus stayed in the EN box) and re-translates when it changed. Ctrl+Enter sends without
  re-translating.
- **Enter on an empty EN box** returns keyboard focus to the game, like the vanilla chat box.
- **`/jpchat test` removed**: the development subcommand that added sample rows is gone now that ingest provides real
  rows.
- **Outgoing requests go through the pipeline** (`TranslationPipeline.TranslateOutgoingAsync`), so unloading the
  plugin cancels one in flight.
- **Message flattening** (`Chat/SeStringText`): text payloads are kept (link names included), auto-translate phrases
  become `《phrase》` (placeholder `《auto-translate》` if unresolvable), player links become the name once, newlines
  become spaces, private-use glyphs U+E000–U+F8FF become spaces, whitespace is collapsed.
- **Own messages**: own = sender name equals `IPlayerState.CharacterName` and either the sender has no player link (the
  game sends your own name as plain text) or its world is your home world. `TellOutgoing` is always kept (sender is
  the target), except the echo of a tell the plugin itself just sent, which is dropped because the composer already
  added that row.
- **Send path**: `UIModule.ProcessChatBoxEntry(Utf8String*, nint a4 = 0, bool saveToHistory = false)`, as ChatTwo's
  normal send and ECommons do. `RaptureShellModule.ExecuteCommandInner` exists too but only runs commands (ChatTwo
  uses it for one special tell case). Before the game call, `ChatSendValidation` rejects empty text, more than
  500 UTF-8 bytes and control characters; then the text must survive `SanitizeString((AllowedEntities)0x27F)`
  unchanged.

---

## 8. Decisions log

- **Model**: `google/gemini-3.8-flash` with `reasoning.effort: "low"`,
  streaming, latency-sorted provider. Fallback `gemini-3.5-flash-lite`.
  Reason: user confirmed 3.8 Flash's JA quality; low thinking level is the
  only way to keep it fast, and it is still roughly 2x slower to first token
  than flash-lite. Config field, re-evaluate from the timing logs.
- **Hide vanilla chat in v1**: no. Window lives next to a shrunk vanilla log.
- **Gate**: client-side only (cache + per-sender debounce). No second LLM.
- **Keys**: one key. Second optional key field kept in config for later.
- **Direction on incoming**: only lines containing kana/kanji go to the
  translator. EN lines are shown as-is with an empty right cell.
- **Own messages**: skipped on ingest except TellOutgoing; sent messages are
  added to the log by the send path instead.

---

## 9. Phase 4: vanilla chat integration

Goal: the user keeps typing in the game's own chat box. Plain English is translated before it is sent, without opening
the plugin window. Version 0.2.0.0.

### 9.1 Hook

- `Chat/ChatSendHook` hooks `UIModule.ProcessChatBoxEntry` through `IGameInteropProvider.HookFromAddress`. Native
  signature per ClientStructs' `[MemberFunction]`: `void (UIModule* this, Utf8String* message, nint a4, bool
  saveToHistory)`. The managed delegate is `void (UIModule*, Utf8String*, nint, byte)`; `byte` for the bool so
  marshalling reads one byte. Address: `UIModule.Addresses.ProcessChatBoxEntry.Value` (ClientStructs resolves it from
  the signature `48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 48 8B F2 48 8B F9 45 84 C9` at startup).
- Detour (framework thread): read `message->ToString()`, decide with the pure `InterceptDecision.Decide(text,
  enabled, bypassPrefix, modifierHeld)`:
  1. disabled → pass; 2. bypass modifier held (`IKeyState`, Ctrl by default) → pass; 3. empty or control characters
  (item links, auto-translate payload bytes) → pass; 4. bypass prefix (`\`) at the start or right after a channel
  command → call the original with the prefix stripped (new `Utf8String`, disposed after); 5. a leading `/` that is
  not a chat-channel command (`OutgoingChannels.TrySplitChatCommand`; `/e` counts as not) → pass; 6. body empty, only
  links/numbers/symbols, or containing Japanese → pass; 7. else intercept.
- Intercept: the original is **not** called (the chat box has already cleared its input). `OnIntercept` (the popup)
  gets the prefix exactly as typed, the body, the raw line, and the channel (typed command, else
  `RaptureShellModule.ChatType`/`TellName`/`TellWorld`). The body is logged only at Debug.

Safety rules:

- **Bypass flag.** Every plugin send goes through `ChatSendHook.SendBypassingHook`, which sets a flag around
  `GameChatSender.Send`; the detour passes straight to the original while it is set. The plugin can never intercept
  its own line.
- **Fail open.** Any exception while deciding or handing over, a missing handler, or a handler that declines (the
  popup is mid-send) calls the original with the unchanged message. Exceptions are logged once per type. A bug can
  delay nothing and eat nothing.
- **Pass by default.** Only plainly English chat is held. Commands, emotes, echo, Japanese, links, numbers and payloads
  go through untouched.
- **No install, no feature.** A null address or an exception while hooking logs a warning; the rest of the plugin
  works. `Dispose` disables and disposes the hook, before the popup and the translation pipeline.

### 9.2 Popup

- `Ui/QuickTranslatePopup`: plain `ImGui.Begin` window (no title bar, auto-resize, no saved settings, no focus on
  appearing), not a Dalamud `Window`, so it is not in the window list and Esc is fully ours.
- The outgoing state machine was extracted from `OutgoingComposer` into `Ui/OutgoingSession` (states, generation
  counter, cancellation, send, events) and the breakdown panel into `Ui/OutgoingPanel`. The main window's composer and
  the popup each own one session + panel; the composer's behaviour is unchanged.
- Placement: right of the `ChatLog` addon, bottom-aligned (`X + width + 8·scale`, `Y + height − popupHeight`); above
  it if that leaves the main viewport; then `PopupOffsetX/Y`; clamped into the viewport. Addon coordinates are
  relative to the game window, so `MainViewport.Pos` is added.
- Keys: Enter/Ctrl+Enter in the JA box sends prefix + Japanese (the Japanese alone without a prefix, so the chat box's
  channel applies); Shift+Enter or "Send English" sends the raw line through the bypass; Esc cancels and puts the raw
  line back with `AddonChatLog.TextInput->SetText` (clipboard + a 2 s notice if that is impossible). Popup keys are
  read when the popup is focused with no active item, or when no ImGui text input is active.
- Sent lines are added to the log (`IsSentByPlugin`). `RecentSends` also matches an echo equal to the whole command,
  so a prefix-less send to a tell channel is not logged twice.

### 9.3 Chat-bar button

`Ui/ChatBarButton`: a borderless, background-less, auto-sized ImGui window with one icon button, positioned at the
right end of the last chat tab (`AddonChatLog.ChatTabs[TabCount−1]->OwnerNode` screen rect; the addon's top-left if no
tab is found) plus `ChatBarButtonOffsetX/Y`. Hidden when the addon is missing or hidden, the game UI is hidden, or
`ShowChatBarButton` is off. It toggles the main window.

### 9.4 Config

`InterceptVanillaChat` (true), `BypassPrefix` (`\`, 1–3 characters, not starting with `/`, normalized on load and
on edit), `BypassModifier` (Ctrl/Shift/Alt/None, default Ctrl), `PopupOffsetX/Y` (0), `ShowChatBarButton` (true),
`ChatBarButtonOffsetX/Y` (4, 0). Schema version stays 1 (additive fields). `/jpchat auto` toggles
`InterceptVanillaChat` and prints the state with `IChatGui.Print`, the plugin's only chat print.
