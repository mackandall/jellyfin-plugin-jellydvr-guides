using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyDvrGuides.Configuration;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.JellyDvrGuides.Guides;

/// <summary>
/// The guide files on disk: guide-&lt;id&gt;.xml per source, each with a guide-&lt;id&gt;.xml.src
/// saying which settings it was made from (a copy made under other settings is never fresh).
/// Under the server's data folder, not the plugin's own (versioned) install folder, so the
/// last good copy survives a plugin upgrade.
/// </summary>
public sealed partial class GuideCopies
{
    public GuideCopies(IApplicationPaths applicationPaths)
    {
        Directory = Path.Combine(applicationPaths.DataPath, Plugin.DataFolderName);

        // 1.0 kept its one guide as guide.xml: it's the first source's last good copy now.
        var old = Path.Combine(Directory, "guide.xml");
        if (Plugin.Instance?.Configuration.Sources.FirstOrDefault() is { } first && File.Exists(old) && !File.Exists(PathOf(first.Id)))
        {
            File.Move(old, PathOf(first.Id));
        }
    }

    public string Directory { get; }

    /// <summary>
    /// The source's guide file. Address names are checked by the settings page; this keeps a
    /// hand-edited one from naming a file outside the folder.
    /// </summary>
    public string PathOf(string id) =>
        AddressName().IsMatch(id)
            ? Path.Combine(Directory, "guide-" + id.ToLowerInvariant() + ".xml")
            : throw new InvalidOperationException($"\"{id}\" isn't a usable address name (letters, digits and dashes).");

    /// <summary>Whether the copy at <paramref name="guidePath"/> was made from these settings (and tuner channels).</summary>
    public static bool MadeFrom(string guidePath, GuideSource source, ISet<string>? numbers) =>
        File.Exists(guidePath + ".src") && File.ReadAllText(guidePath + ".src") == Signature(source, numbers);

    /// <summary>Records what the copy just written was made from.</summary>
    public static void RecordMadeFrom(string guidePath, GuideSource source, ISet<string>? numbers) =>
        File.WriteAllText(guidePath + ".src", Signature(source, numbers));

    // The tuners' channel numbers count, so a channel added to a tuner gets its guide at the
    // next request.
    private static string Signature(GuideSource s, ISet<string>? numbers) =>
        string.Join('|', s.Type, s.Url.Trim(), s.Lineup.Trim(), s.Days, s.Clean, s.CleanYears, s.CleanIds, s.NumberlessAsSeries)
        + (numbers is null ? string.Empty : "|" + string.Join(',', numbers.OrderBy(n => n, StringComparer.Ordinal)));

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]*$")]
    private static partial Regex AddressName();
}
