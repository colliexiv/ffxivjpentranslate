# JP/EN Chat

A Dalamud plugin for English speakers playing on Japanese data centers.

- A two-pane chat window: the original message on the left and its English translation on the right, one row per message. A slow translation stays on the same row as its original.
- An English input box: type English, press Enter to get Japanese with a word-by-word breakdown and a back-translation, then press Enter again to send it.
- Translation runs through your own [OpenRouter](https://openrouter.ai) API key. The key is encrypted with Windows DPAPI for your user account before it is written to the config file.

**Status: work in progress.** Only the skeleton exists so far: the `/jpchat` command, a placeholder window, and the settings window with API key storage. Chat capture, translation and sending are not implemented yet. The design is in [`docs/PLAN.md`](docs/PLAN.md).

## Building

Requirements:

- .NET SDK 10.0
- A Dalamud API 15 dev install. On Windows, XIVLauncher places one at `%AppData%\XIVLauncher\addon\Hooks\dev\`. To use a different location, set `DALAMUD_HOME` to the folder that contains `Dalamud.dll`.

```sh
dotnet build JpEnChat/JpEnChat.csproj -c Release
```

The output goes to `JpEnChat/bin/Release/`. That folder holds `JpEnChat.dll` and `JpEnChat.json`, and a packaged `JpEnChat/latest.zip`.

## Loading as a dev plugin

1. Build the project in Debug or Release.
2. In game, run `/xlsettings`, open **Experimental**, and add the full path to `JpEnChat/bin/<Configuration>/JpEnChat.dll` under **Dev Plugin Locations**. Save.
3. Run `/xlplugins`, open **Dev Tools > Installed Dev Plugins**, and enable **JP/EN Chat**.
4. Use `/jpchat` to toggle the window and `/jpchat config` to open settings.

## Commands

| Command | Action |
|---|---|
| `/jpchat` | Toggle the chat window |
| `/jpchat config` | Open settings |
