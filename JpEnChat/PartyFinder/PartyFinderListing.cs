namespace JpEnChat.PartyFinder;

/// <summary>What the plugin read from a Party Finder listing's detail window when "Translate" was clicked.</summary>
/// <param name="RawDescription">The description's raw bytes, used to notice when the window shows another listing.</param>
/// <param name="Description">Description cleaned by <see cref="PartyFinderText"/>; may be empty.</param>
/// <param name="Duty">Duty name as displayed; may be empty.</param>
/// <param name="Leader">Party leader (recruiter) name as displayed; may be empty.</param>
internal sealed record PartyFinderListing(byte[] RawDescription, string Description, string Duty, string Leader);
