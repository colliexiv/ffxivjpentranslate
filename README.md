# JP/EN Chat

A Dalamud plugin for English speakers playing on Japanese data centers.

- A two-pane chat window: the original message on the left and its English translation on the right, one row per message. A slow translation stays on the same row as its original.
- An English input box: type English, press Enter to get Japanese with a word-by-word breakdown and a back-translation, then press Enter again to send it.
- Translation runs through your own [OpenRouter](https://openrouter.ai) API key. The key is encrypted with Windows DPAPI for your user account before it is written to the config file.

**Status: feature-complete for v1, pending in-game testing.** Chat capture, streamed translation, caching, the outgoing composer and sending are all implemented and unit-tested, but the plugin has not yet been run against a live game client. See [Known unverified](#known-unverified) below. The design is in [`docs/PLAN.md`](docs/PLAN.md).

## Installing from the custom repository

1. In game, run `/xlsettings` and open **Experimental**.
2. Under **Custom Plugin Repositories**, add
   `https://raw.githubusercontent.com/colliexiv/ffxivjpentranslate/claude/dalamud-ffxiv-translation-plugin-66t6li/repo.json`, tick **Enabled**, and click **Save**.
3. Run `/xlplugins`, search for **JP/EN Chat** and install it.
4. Run `/jpchat config`, open the **Keys** tab and paste your OpenRouter API key.

`repo.json` points at `releases/latest/download/latest.zip`, so it always installs the newest GitHub release.

## Using it

### Window layout

```
┌ JP/EN Chat ──────────────────────────────────────────────┐
│ [Channels] [trash] [Latest]                      [gear]  │  toolbar
│ ┌ original ───────────────────┬ translation ──────────┐  │
│ │ 21:03 [P] Tanaka: よろしく   │ Nice to meet you       │  │  log
│ │ 21:03 [P] Suzuki: 1ボス行きます│ …                      │  │
│ └─────────────────────────────┴───────────────────────┘  │
│ 1ボスいきましょう            (editable Japanese)          │  breakdown panel
│   1ボス = first boss · いきましょう = let's go            │  (only while confirming)
│   back: "Let's go to the first boss."  ○polite ○casual   │
│ [Party ▾] [ let's pull the first boss          ] 42/500  │  input row
└──────────────────────────────────────────────────────────┘
```

- Every captured line appears immediately. The right cell shows `…` until the translation streams in. A failed request shows a clickable `retry`.
- Lines without Japanese (for example English) get no translation.
- **Channels** filters which channels are displayed (capture is unaffected). **Latest** jumps back to the bottom when you have scrolled up.
- Hover a translation to see the full original.
- Lines you send through the plugin appear as their own row: English on the left, the Japanese you sent on the right.
- Your own messages typed in the vanilla chat box are not captured, except outgoing tells.

### Keys

"EN box" is the English input on the bottom row. "JA box" is the editable Japanese in the breakdown panel.

| State | Key | Action |
|---|---|---|
| Editing | Enter in EN box | Translate (a typed channel prefix such as `/p` is applied first) |
| Editing | Enter on an empty EN box | Return keyboard focus to the game |
| Editing | Esc | Dismiss the last error |
| Translating | Enter in EN box | Re-translate if the English changed, otherwise ignored |
| Translating | Esc | Cancel the request and keep the English |
| Confirming | Enter or Ctrl+Enter in JA box | Send the Japanese as edited |
| Confirming | Enter in EN box | Send if the English is unchanged; re-translate if it changed |
| Confirming | Ctrl+Enter in EN box | Send the current Japanese without re-translating |
| Confirming | Esc | Discard the translation and keep the English |
| Confirming | polite / casual | Re-translate with that register |
| Sending | any | Ignored until the send completes |

### Channels

Pick the channel in the combo next to the input, or type a prefix at the start of the English text:

| Prefix | Channel |
|---|---|
| `/s`, `/say` | Say |
| `/sh`, `/shout` | Shout |
| `/y`, `/yell` | Yell |
| `/p`, `/party` | Party |
| `/a`, `/alliance` | Alliance |
| `/fc`, `/freecompany` | Free Company |
| `/l1`–`/l8`, `/linkshell1`–`/linkshell8` | Linkshells |
| `/cwl1`–`/cwl8`, `/cwlinkshell1`–`/cwlinkshell8` | Cross-world linkshells |
| `/t First Last@World`, `/tell …` | Tell |
| `/e`, `/echo` | Echo (only you see it; useful for testing) |

One chat line is at most 500 UTF-8 bytes (about 166 Japanese characters) including the prefix. The counter next to the input shows the current size, and sending is blocked when it is over.

### Commands

| Command | Action |
|---|---|
| `/jpchat` | Toggle the chat window |
| `/jpchat config` | Open settings |

### Settings

| Tab | Contents |
|---|---|
| General | Font size, timestamps, maximum log lines, debounce (how long to wait for more lines from the same sender before translating them as one batch), default register |
| Translation | Incoming and outgoing model, fallback models, reasoning effort (empty = omit), concurrent requests, request timeout |
| Channels | Which chat channels are captured and translated |
| Cache | Enable the translation cache, maximum entries, entry count, clear |
| Keys | OpenRouter API key (stored DPAPI-encrypted) and an optional second key reserved for later |

## Cost

You need an OpenRouter account with credits and an API key. Each translated line is billed by OpenRouter at the model's token price. With the default `google/gemini-3.8-flash`, an evening of about 2,000 Japanese lines costs roughly $0.30. A cheaper model such as `google/gemini-2.5-flash-lite` costs under $0.05 for the same amount. Repeated lines (greetings, macros) are served from the local cache and cost nothing. You can set a credit limit on the key in OpenRouter's settings.

## Privacy

- Chat lines from the channels enabled in **Channels** that contain Japanese, and the English you type into the input box, are sent to OpenRouter for translation. Requests ask OpenRouter to route only to providers that do not collect data (`provider.data_collection: "deny"`).
- Sender names are not part of the request text. Nothing else leaves your machine: no telemetry, no other services.
- The API key and the translation cache are stored in the plugin's config folder. The key is encrypted with DPAPI for your Windows account. Logs record request timing and sizes, never keys or message text above debug level.

## Known unverified

These depend on the live client and have not been checked in game yet:

- Sending: `ProcessChatBoxEntry` with the `0x27F` sanitizer flags accepts Japanese text unchanged on a JP client (if not, every send fails safely with "characters the chat box does not allow").
- The tell echo: a tell sent through the plugin is logged once, not twice; tells typed in the vanilla chat box show up with the target as the sender.
- Own-message detection by character name and home world, including cross-world parties and alliances.
- Sender names from party chat (slot-number glyphs stripped) and the sender's world for cross-world players.
- Auto-translate phrases, item links and map links in the original text.
- Font sizes: the Axis font at each size from 10 to 24 px, including Japanese glyphs.
- Log clipping: row heights and scrolling with long logs and wrapped rows.
- Esc handling in the input boxes and with the window focused.
- IME input: confirming an IME composition with Enter must not also trigger a translation or send.
- Height of the breakdown panel on its first frame (it is measured, so the first frame may jump).

## Building

Requirements:

- .NET SDK 10.0
- A Dalamud API 15 dev install. On Windows, XIVLauncher places one at `%AppData%\XIVLauncher\addon\Hooks\dev\`. To use a different location, set `DALAMUD_HOME` to the folder that contains `Dalamud.dll`.

```sh
dotnet build JpEnChat/JpEnChat.csproj -c Release
dotnet test JpEnChat.Tests -c Release
```

The output goes to `JpEnChat/bin/Release/`. That folder holds `JpEnChat.dll` and `JpEnChat.json`, and a packaged `JpEnChat/latest.zip`. The tests run on Windows and Linux.

CI (`.github/workflows/build.yml`) builds and tests every push. Pushing a `v*` tag also creates a GitHub release with `latest.zip`, which the committed `repo.json` picks up through the `releases/latest` link. Bump `<Version>` in `JpEnChat/JpEnChat.csproj` and the `AssemblyVersion`/`LastUpdate` in `repo.json` when tagging (CI produces an up-to-date `repo.json` as a build artifact).

## Loading as a dev plugin

1. Build the project in Debug or Release.
2. In game, run `/xlsettings`, open **Experimental**, and add the full path to `JpEnChat/bin/<Configuration>/JpEnChat.dll` under **Dev Plugin Locations**. Save.
3. Run `/xlplugins`, open **Dev Tools > Installed Dev Plugins**, and enable **JP/EN Chat**.
4. Use `/jpchat` to toggle the window and `/jpchat config` to open settings.
