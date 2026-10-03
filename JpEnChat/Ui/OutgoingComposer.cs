using System;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using JpEnChat.Models;
using JpEnChat.Translation;

namespace JpEnChat.Ui;

/// <summary>
/// Bottom input row plus the breakdown panel above it: the outgoing EN→JA flow of PLAN §4.1.
/// </summary>
/// <remarks>
/// <para><b>States</b> (<see cref="OutgoingState"/>): Editing → Translating → Confirming → Sending → Editing.</para>
/// <para><b>Keys.</b> "EN box" is the English input on the bottom row, "JA box" is the editable Japanese in the
/// breakdown panel. Enter and Ctrl+Enter are read from <c>InputText(EnterReturnsTrue)</c> (Ctrl via
/// <c>io.KeyCtrl</c>); Esc is handled once per frame, either when an input box was deactivated by Esc (ImGui
/// reverts the text, so the pre-Esc text is restored) or when the window is focused and Esc is pressed.</para>
/// <list type="table">
/// <listheader><term>State + key</term><description>Action</description></listheader>
/// <item><term>Editing, Enter in EN box</term><description>Parse a typed channel prefix; if text remains, translate (→ Translating).</description></item>
/// <item><term>Editing, Esc</term><description>Dismiss the last error, keep text.</description></item>
/// <item><term>Translating, Enter in EN box</term><description>If the English changed, cancel and re-translate; else ignore.</description></item>
/// <item><term>Translating, Esc</term><description>Cancel the request (→ Editing), keep the English.</description></item>
/// <item><term>Confirming, Enter / Ctrl+Enter in JA box</term><description>Send prefix + JA as edited (→ Sending).</description></item>
/// <item><term>Confirming, Ctrl+Enter in EN box</term><description>Send the current JA without re-translating.</description></item>
/// <item><term>Confirming, Enter in EN box</term><description>English changed since the translation: re-translate. Unchanged: send (so "Enter, Enter" sends even if focus stayed in the EN box).</description></item>
/// <item><term>Confirming, Esc</term><description>Discard the translation (→ Editing), keep the English.</description></item>
/// <item><term>Confirming, register toggle</term><description>Re-translate the EN box text with the new register.</description></item>
/// <item><term>Sending, any key</term><description>Ignored until the send completes (→ Editing, or back to Confirming with the error).</description></item>
/// </list>
/// <para><b>Threading.</b> All fields are touched only on the draw/framework thread. The translator runs in
/// <c>Task.Run</c>; its result is marshalled back with <c>IFramework.RunOnFrameworkThread</c> and dropped if a newer
/// request or a cancel happened in between (generation counter).</para>
/// </remarks>
internal sealed class OutgoingComposer : IDisposable
{
    private const int MaxInputBytes = IChatSender.MaxMessageBytes;
    private const int MaxTellTargetBytes = 64;

    private static readonly string[] TranslatingFrames = ["Translating", "Translating.", "Translating..", "Translating..."];

    private readonly ChatLog log;
    private readonly IOutgoingTranslator translator;
    private readonly Func<string, Task> send;
    private readonly Func<string> localPlayerName;

    private OutgoingState state = OutgoingState.Editing;
    private string english = string.Empty;
    private string japanese = string.Empty;
    private string tellTarget = string.Empty;
    private int channelIndex = OutgoingChannels.SayIndex;
    private string register;
    private OutgoingDraft? draft;
    private string requestedEnglish = string.Empty;
    private string? error;

    private CancellationTokenSource? cts;
    private int generation;
    private bool disposed;

    private bool focusEnglish;
    private bool focusJapanese;
    private bool escapeFromInput;
    private float panelHeight;
    private OutgoingState panelHeightState;

    public OutgoingComposer(
        Configuration configuration,
        ChatLog log,
        IOutgoingTranslator translator,
        Func<string, Task> send,
        Func<string> localPlayerName)
    {
        this.log = log;
        this.translator = translator;
        this.send = send;
        this.localPlayerName = localPlayerName;
        register = configuration.DefaultRegister == Registers.Casual ? Registers.Casual : Registers.Polite;
    }

    public OutgoingState State => state;

    private OutgoingChannel Channel => OutgoingChannels.All[channelIndex];

    private bool PanelVisible => state != OutgoingState.Editing || error != null;

    /// <summary>Height the panel will take this frame (last frame's measurement), including spacing; 0 when hidden.</summary>
    public float ReservedPanelHeight()
    {
        if (!PanelVisible)
        {
            return 0f;
        }

        var estimate = ImGui.GetFrameHeightWithSpacing() * (state == OutgoingState.Confirming ? 4f : 1f);
        // Separator (1 px) + spacing above and below it + the panel group.
        var measured = panelHeight > 0f && panelHeightState == state ? panelHeight : estimate;
        return measured + (ImGui.GetStyle().ItemSpacing.Y * 2f) + 1f;
    }

    /// <summary>Height of the input row.</summary>
    public static float InputRowHeight() => ImGui.GetFrameHeightWithSpacing();

    public void Dispose()
    {
        disposed = true;
        CancelInFlight();
    }

    /// <summary>Draws the breakdown panel (if any). Call directly above <see cref="DrawInputRow"/>.</summary>
    public void DrawPanel()
    {
        if (!PanelVisible)
        {
            panelHeight = 0f;
            return;
        }

        ImGui.Separator();
        using (ImRaii.Group())
        {
            switch (state)
            {
                case OutgoingState.Translating:
                    var frame = (int)(ImGui.GetTime() * 3.0) % TranslatingFrames.Length;
                    ImGui.TextDisabled(TranslatingFrames[frame]);
                    ImGui.SameLine();
                    ImGui.TextDisabled("(Esc to cancel)");
                    break;

                case OutgoingState.Sending:
                    ImGui.TextDisabled("Sending...");
                    break;

                case OutgoingState.Confirming:
                    DrawConfirming();
                    break;
            }

            if (error != null)
            {
                ImGui.TextColoredWrapped(ImGuiColors.DalamudRed, error);
                if (state == OutgoingState.Editing)
                {
                    ImGui.TextDisabled("(Esc to dismiss)");
                }
            }
        }

        panelHeight = ImGui.GetItemRectSize().Y;
        panelHeightState = state;
    }

    /// <summary>Draws <c>EN&gt; [channel] [tell target] [english............] n/500</c>.</summary>
    public void DrawInputRow()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("EN>");
        ImGui.SameLine();

        DrawChannelCombo();

        if (Channel.IsTell)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10f);
            ImGui.InputTextWithHint("##jpenTellTarget", "Name Surname@World", ref tellTarget, MaxTellTargetBytes);
        }

        ImGui.SameLine();

        var showCounter = state == OutgoingState.Confirming;
        var counterWidth = showCounter
            ? ImGui.CalcTextSize("000/500").X + ImGui.GetStyle().ItemSpacing.X
            : 0f;
        ImGui.SetNextItemWidth(Math.Max(ImGui.GetContentRegionAvail().X - counterWidth, ImGui.GetFontSize() * 4f));

        if (focusEnglish)
        {
            ImGui.SetKeyboardFocusHere();
            focusEnglish = false;
        }

        var hint = state switch
        {
            OutgoingState.Confirming => "Enter: send  |  edit + Enter: re-translate  |  Esc: cancel",
            OutgoingState.Translating => "Esc: cancel",
            _ => "Type English (or /p, /fc, /l1 ... prefix), Enter to translate",
        };

        var before = english;
        var entered = ImGui.InputTextWithHint(
            "##jpenEnglish", hint, ref english, MaxInputBytes, ImGuiInputTextFlags.EnterReturnsTrue);
        if (ImGui.IsItemDeactivated() && ImGui.IsKeyDown(ImGuiKey.Escape))
        {
            english = before; // ImGui reverts to the text at activation on Esc; keep what the user typed.
            escapeFromInput = true;
        }

        if (entered)
        {
            OnEnglishEnter(ImGui.GetIO().KeyCtrl);
        }

        if (showCounter)
        {
            ImGui.SameLine();
            var bytes = CommandByteCount();
            var color = bytes > MaxInputBytes ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey;
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(color, $"{bytes}/{MaxInputBytes}");
        }
    }

    /// <summary>Runs the Esc transition at most once per frame. Call after drawing the panel and input row.</summary>
    public void EndFrame(bool windowFocused)
    {
        var escape = escapeFromInput || (windowFocused && ImGui.IsKeyPressed(ImGuiKey.Escape, false));
        escapeFromInput = false;
        if (escape)
        {
            HandleEscape();
        }
    }

    private void DrawChannelCombo()
    {
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6.5f);
        using var combo = ImRaii.Combo("##jpenChannel", Channel.Label);
        if (!combo.Success)
        {
            return;
        }

        for (var i = 0; i < OutgoingChannels.All.Count; i++)
        {
            var selected = i == channelIndex;
            if (ImGui.Selectable(OutgoingChannels.All[i].Label, selected))
            {
                channelIndex = i;
                focusEnglish = true;
            }

            if (selected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }
    }

    private void DrawConfirming()
    {
        // Editable Japanese. Enter or Ctrl+Enter sends what is in this box.
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("JA>");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        if (focusJapanese)
        {
            ImGui.SetKeyboardFocusHere();
            focusJapanese = false;
        }

        var before = japanese;
        var entered = ImGui.InputText("##jpenJapanese", ref japanese, MaxInputBytes, ImGuiInputTextFlags.EnterReturnsTrue);
        if (ImGui.IsItemDeactivated() && ImGui.IsKeyDown(ImGuiKey.Escape))
        {
            japanese = before;
            escapeFromInput = true;
        }

        if (entered)
        {
            Send();
        }

        if (draft is { } d)
        {
            DrawSegments(d);

            if (d.BackTranslation.Length > 0)
            {
                ImGui.TextDisabled("back:");
                ImGui.SameLine();
                ImGui.TextWrapped(d.BackTranslation);
            }
        }

        var changed = false;
        if (ImGui.RadioButton("polite", register == Registers.Polite) && register != Registers.Polite)
        {
            register = Registers.Polite;
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("casual", register == Registers.Casual) && register != Registers.Casual)
        {
            register = Registers.Casual;
            changed = true;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("Enter: send   Ctrl+Enter: send edited   Esc: cancel");

        if (changed)
        {
            Retranslate();
        }
    }

    /// <summary>Flows <c>ja (reading) = en</c> glosses left to right, wrapping at the panel width.</summary>
    private static void DrawSegments(OutgoingDraft d)
    {
        if (d.Segments.Count == 0)
        {
            return;
        }

        var spacing = ImGui.GetStyle().ItemSpacing.X * 2f;
        var available = ImGui.GetContentRegionAvail().X;
        var used = 0f;

        for (var i = 0; i < d.Segments.Count; i++)
        {
            var s = d.Segments[i];
            var showReading = s.Reading.Length > 0 && !string.Equals(s.Reading, s.Ja, StringComparison.Ordinal);
            var jaWidth = ImGui.CalcTextSize(s.Ja).X;
            var glossWidth = showReading
                ? ImGui.CalcTextSize($"({s.Reading}) = {s.En}").X
                : ImGui.CalcTextSize($"= {s.En}").X;
            var width = jaWidth + ImGui.GetStyle().ItemInnerSpacing.X + glossWidth;

            if (i > 0 && used + spacing + width <= available)
            {
                ImGui.SameLine(0f, spacing);
                used += spacing + width;
            }
            else
            {
                used = width;
            }

            ImGui.TextUnformatted(s.Ja);
            ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
            if (showReading)
            {
                ImGui.TextDisabled($"({s.Reading}) = {s.En}");
            }
            else
            {
                ImGui.TextDisabled($"= {s.En}");
            }
        }
    }

    private void OnEnglishEnter(bool ctrl)
    {
        // Keep typing focus after Enter, except Enter on an empty box while editing, which hands the keyboard back
        // to the game like the vanilla chat box does.
        focusEnglish = state != OutgoingState.Editing || english.Trim().Length > 0;
        ApplyTypedPrefix();
        var text = english.Trim();

        switch (state)
        {
            case OutgoingState.Editing:
                if (text.Length > 0)
                {
                    StartTranslation(text);
                }

                break;

            case OutgoingState.Translating:
                if (text.Length > 0 && !string.Equals(text, requestedEnglish, StringComparison.Ordinal))
                {
                    StartTranslation(text);
                }

                break;

            case OutgoingState.Confirming:
                if (ctrl || text.Length == 0 || string.Equals(text, draft?.EnglishText, StringComparison.Ordinal))
                {
                    Send();
                }
                else
                {
                    StartTranslation(text);
                }

                break;

            case OutgoingState.Sending:
                break;
        }
    }

    /// <summary>"/p hello" → channel Party, text "hello". Unknown commands are left as text.</summary>
    private void ApplyTypedPrefix()
    {
        if (!OutgoingChannels.TryParsePrefix(english, out var index, out var target, out var body))
        {
            return;
        }

        channelIndex = index;
        if (OutgoingChannels.All[index].IsTell && target.Length > 0)
        {
            tellTarget = target;
        }

        english = body;
    }

    private void Retranslate()
    {
        var text = english.Trim();
        if (text.Length == 0)
        {
            text = draft?.EnglishText ?? requestedEnglish;
        }

        if (text.Length > 0)
        {
            StartTranslation(text);
        }
    }

    private void StartTranslation(string text)
    {
        CancelInFlight();

        var request = new OutgoingDraft
        {
            EnglishText = text,
            Register = register,
            ChannelPrefix = OutgoingChannels.Prefix(Channel, tellTarget) ?? string.Empty,
        };

        requestedEnglish = text;
        error = null;
        state = OutgoingState.Translating;
        var gen = ++generation;
        var source = new CancellationTokenSource();
        cts = source;
        var token = source.Token;

        _ = Task.Run(() => TranslateAsync(request, gen, token), CancellationToken.None);
    }

    private async Task TranslateAsync(OutgoingDraft request, int gen, CancellationToken token)
    {
        try
        {
            var result = await translator.TranslateOutgoingAsync(request, token).ConfigureAwait(false);
            PostToFramework(() => OnTranslated(gen, result, null));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Esc, a newer request or dispose; the state was already changed by whoever cancelled.
        }
        catch (Exception ex)
        {
            PostToFramework(() => OnTranslated(gen, null, ex));
        }
    }

    private void OnTranslated(int gen, OutgoingDraft? result, Exception? ex)
    {
        if (disposed || gen != generation || state != OutgoingState.Translating)
        {
            return; // superseded or cancelled
        }

        DisposeCts();

        if (result == null)
        {
            state = OutgoingState.Editing;
            error = $"Translation failed: {Describe(ex)}";
            focusEnglish = true;
            return;
        }

        draft = result;
        japanese = result.JapaneseText;
        error = null;
        state = OutgoingState.Confirming;

        // Move focus to the JA box so the second Enter sends, unless the user kept typing English meanwhile.
        if (string.Equals(english.Trim(), result.EnglishText, StringComparison.Ordinal))
        {
            focusJapanese = true;
        }
    }

    private void Send()
    {
        if (state != OutgoingState.Confirming || draft == null)
        {
            return;
        }

        var ja = japanese.Trim();
        var channel = Channel;
        var prefix = OutgoingChannels.Prefix(channel, tellTarget);
        if (prefix == null)
        {
            error = "Enter the tell target as Name Surname@World.";
            return;
        }

        if (ja.Length == 0)
        {
            error = "Nothing to send.";
            return;
        }

        var command = prefix + ja;
        var bytes = Encoding.UTF8.GetByteCount(command);
        if (bytes > MaxInputBytes)
        {
            error = $"Too long for one chat line: {bytes}/{MaxInputBytes} bytes. Shorten the Japanese.";
            return;
        }

        var sent = new SentMessage(channel, tellTarget.Trim(), draft.EnglishText, ja);
        state = OutgoingState.Sending;
        error = null;
        var gen = ++generation;

        Task task;
        try
        {
            task = send(command);
        }
        catch (Exception ex)
        {
            OnSent(gen, sent, ex);
            return;
        }

        if (task.IsCompleted)
        {
            OnSent(gen, sent, task.Exception?.GetBaseException());
            return;
        }

        task.ContinueWith(
            t => PostToFramework(() => OnSent(gen, sent, t.Exception?.GetBaseException())),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnSent(int gen, SentMessage sent, Exception? ex)
    {
        if (disposed || gen != generation || state != OutgoingState.Sending)
        {
            return;
        }

        if (ex != null)
        {
            state = OutgoingState.Confirming;
            error = $"Send failed: {Describe(ex)}";
            focusJapanese = true;
            return;
        }

        log.Add(BuildSentLine(sent));

        english = string.Empty;
        japanese = string.Empty;
        draft = null;
        requestedEnglish = string.Empty;
        state = OutgoingState.Editing;
        focusEnglish = true;
    }

    private ChatLine BuildSentLine(SentMessage sent)
    {
        string name;
        var world = string.Empty;
        if (sent.Channel.IsTell)
        {
            var at = sent.TellTarget.IndexOf('@');
            name = at < 0 ? sent.TellTarget : sent.TellTarget[..at];
            world = at < 0 ? string.Empty : sent.TellTarget[(at + 1)..];
        }
        else
        {
            name = localPlayerName();
            if (name.Length == 0)
            {
                name = "You";
            }
        }

        return new ChatLine
        {
            Kind = sent.Channel.Kind,
            SenderName = name,
            SenderWorld = world,
            Original = sent.English,
            OriginalLang = Lang.En,
            Translation = sent.Japanese,
            Status = TranslationStatus.Done,
            IsOwn = true,
            IsSentByPlugin = true,
        };
    }

    private void HandleEscape()
    {
        switch (state)
        {
            case OutgoingState.Translating:
                CancelInFlight();
                state = OutgoingState.Editing;
                focusEnglish = true;
                break;

            case OutgoingState.Confirming:
                draft = null;
                japanese = string.Empty;
                error = null;
                state = OutgoingState.Editing;
                focusEnglish = true;
                break;

            case OutgoingState.Editing:
                error = null;
                break;

            case OutgoingState.Sending:
                break; // cannot be recalled
        }
    }

    private int CommandByteCount() =>
        OutgoingChannels.PrefixByteCount(Channel, tellTarget) + Encoding.UTF8.GetByteCount(japanese.AsSpan().Trim());

    private void CancelInFlight()
    {
        generation++;
        if (cts == null)
        {
            return;
        }

        cts.Cancel();
        DisposeCts();
    }

    private void DisposeCts()
    {
        cts?.Dispose();
        cts = null;
    }

    private static string Describe(Exception? ex) => ex switch
    {
        null => "unknown error",
        _ when string.IsNullOrWhiteSpace(ex.Message) => ex.GetType().Name,
        _ => ex.Message,
    };

    private static void PostToFramework(Action action)
    {
        try
        {
            _ = Services.Framework.RunOnFrameworkThread(action);
        }
        catch (Exception ex)
        {
            // Plugin unloading; nothing left to update.
            Services.Log.Debug($"Dropped outgoing UI update: {ex.GetType().Name}");
        }
    }

    private sealed record SentMessage(OutgoingChannel Channel, string TellTarget, string English, string Japanese);
}
