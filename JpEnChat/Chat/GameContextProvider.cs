using System;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using JpEnChat.Translation;
using Lumina.Excel.Sheets;

namespace JpEnChat.Chat;

/// <summary>
/// Reads the player's zone, duty and job for <see cref="TranslationContext"/> (PLAN §12). Framework thread only.
/// </summary>
/// <remarks>
/// Zone and duty names come from the <c>TerritoryType</c> row of <c>IClientState.TerritoryType</c> (its
/// <c>PlaceName</c> and <c>ContentFinderCondition</c>) in the client's language and are cached per territory id. The
/// duty is reported only while <see cref="ConditionFlag.BoundByDuty"/> is set. The job is the English
/// <c>ClassJob.Abbreviation</c> of <c>IPlayerState.ClassJob</c> (e.g. WHM), cached per job id.
/// </remarks>
internal sealed class GameContextProvider
{
    private uint cachedTerritory = uint.MaxValue;
    private string cachedZone = string.Empty;
    private string cachedDuty = string.Empty;
    private uint cachedJob = uint.MaxValue;
    private string cachedJobAbbreviation = string.Empty;

    /// <summary>The current environment; <see cref="GameEnvironment.Unknown"/> parts stay empty when not logged in.</summary>
    public GameEnvironment Current()
    {
        RefreshTerritory();
        var duty = cachedDuty.Length > 0 && Services.Condition[ConditionFlag.BoundByDuty] ? cachedDuty : string.Empty;
        return new GameEnvironment(cachedZone, duty, CurrentJob());
    }

    private void RefreshTerritory()
    {
        uint territory = Services.ClientState.TerritoryType;
        if (territory == cachedTerritory)
        {
            return;
        }

        cachedTerritory = territory;
        cachedZone = string.Empty;
        cachedDuty = string.Empty;
        if (territory == 0)
        {
            return;
        }

        var row = Services.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory);
        if (row is not { } t)
        {
            return;
        }

        cachedZone = t.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
        cachedDuty = t.ContentFinderCondition.RowId == 0
            ? string.Empty
            : t.ContentFinderCondition.ValueNullable?.Name.ExtractText() ?? string.Empty;
    }

    private string CurrentJob()
    {
        var player = Services.PlayerState;
        if (!player.IsLoaded)
        {
            return string.Empty;
        }

        var job = player.ClassJob.RowId;
        if (job != cachedJob)
        {
            cachedJob = job;
            cachedJobAbbreviation = Services.DataManager.GetExcelSheet<ClassJob>(ClientLanguage.English)
                .GetRowOrDefault(job)?.Abbreviation.ExtractText() ?? string.Empty;
        }

        return cachedJobAbbreviation;
    }
}
