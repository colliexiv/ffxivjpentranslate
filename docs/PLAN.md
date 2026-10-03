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
  "model": "google/gemini-2.5-flash-lite",
  "models": ["google/gemini-2.5-flash-lite", "openai/gpt-4.1-mini"],
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
- Gemini 3.x flash models reason by default; if used, send
  `reasoning: {effort: "none"}` or TTFT balloons.
- Error handling: 429 → backoff with `X-RateLimit-*`; 402 → surface
  "credits/key limit" in the window; timeout 8 s, no fallback chains to
  other services.

### 3.4 Model/cost shortlist (OpenRouter list prices, USD per 1M in/out)

| Model | TTFT (p50) | Price | Notes |
|---|---|---|---|
| google/gemini-2.5-flash-lite | ~0.15–0.3 s | 0.10 / 0.40 | Recommended default |
| google/gemini-3.1-flash-lite | similar | 0.25 / 1.50 | Better JA, disable reasoning |
| openai/gpt-4.1-mini | ~0.4 s | 0.40 / 1.60 | Non-reasoning fallback |
| anthropic/claude-haiku-4.5 | ~0.2–0.6 s | 1.00 / 5.00 | Best quality, 10x cost |
| openai/gpt-5-nano | fast at `effort: minimal` | 0.05 / 0.40 | Classifier role if ever needed |

A busy evening of chat (~2,000 lines, ~60 tokens each round-trip) is a few
cents on flash-lite.

Two API keys: OpenRouter supports unlimited keys per account, each with its
own credit limit and reset period. If the gate stays client-side only one key
is needed. Keep the config field for a second key anyway so a classifier can
be added later without a schema change.

---

## 4. UI

Single `Window` from `Dalamud.Interface.Windowing`, flags `NoScrollbar`,
`SizeConstraints` with a sensible minimum.

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

| # | Scope | Output |
|---|---|---|
| M0 | Skeleton: SDK csproj, `/jpchat`, empty window, config, DPAPI key storage | loads in dev mode |
| M1 | Ingest + two-pane log with `…` placeholder, script-based language detect | rows appear instantly |
| M2 | OpenRouter streaming translation, bounded concurrency, timing logs | right column fills in <0.5 s typical |
| M3 | Normalized exact cache, per-sender debounce/batching | macros translated as a unit, repeats instant |
| M4 | Outgoing: EN input → structured JA + breakdown → confirm → `ProcessChatBoxEntry` | can chat in JP |
| M5 | Polish: channel colors, tabs/filters, byte counter, politeness toggle, `repo.json` release | installable from custom repo |
| v2 | Hide vanilla chat, backfill history from `RaptureLogModule`, optional LLM gate for ambiguous bursts, glossary editor | |

---

## 8. Open decisions

- **Hide vanilla chat in v1?** Recommendation: no. Keep it simple; let the
  window live next to a shrunk vanilla log. Revisit once the window is
  trusted.
- **Translate own EN messages for JP readers in the log?** Not needed; the
  user sees their own text.
- **Single key vs. two keys**: one key is enough with a client-side gate.
  Keep a second optional key field for a future classifier role.
- **Direction EN→JA on incoming?** Some JP-server English speakers will chat
  in EN; translating EN lines to JA in the log is pointless for the user.
  Only JA (and optionally other non-EN) lines go to the translator.
