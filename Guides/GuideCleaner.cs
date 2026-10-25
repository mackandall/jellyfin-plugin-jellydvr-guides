using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Plugin.XmltvCleaner.Guides;

/// <summary>
/// The pure XMLTV transform. Channels DVR's XMLTV feed bakes a movie's release year into
/// its <c>&lt;title&gt;</c> ("The Lord of the Rings: The Fellowship of the Ring (2001)")
/// even though the same programme also carries a proper <c>&lt;date&gt;20011219&lt;/date&gt;</c>.
/// Jellyfin reads that date as the production year and appends "(2001)" again when it
/// names the recording, giving "... (2001) (2001)" for both the title and the folder.
///
/// Fix: strip a trailing "(YYYY)" from a programme's title ONLY when YYYY equals the year
/// of that same programme's <c>&lt;date&gt;</c>. A title whose parenthesised year disagrees
/// with the date, or a programme with no date at all, is left untouched — the year might
/// genuinely be part of the name (e.g. a remake distinguished by year) and there's then
/// no year for Jellyfin to re-append anyway.
///
/// Second fix: Channels DVR gives each programme its Gracenote/TMS id as
/// <c>&lt;episode-num system="tms"&gt;EP000191100039&lt;/episode-num&gt;</c>, but Jellyfin's
/// XMLTV reader only understands the <c>dd_progid</c> system and drops "tms". Without it
/// Jellyfin falls back to a ShowId hashed from the episode number alone (its fallback does
/// <c>str = "-" + episode</c> where it means <c>+=</c>), so S1E5 and S3E5 of a show share a
/// ShowId and "skip episodes already in library" cancels one as a duplicate of the other.
/// Copying the tms id into a <c>dd_progid</c> entry gives Jellyfin the real, cross-channel
/// episode id — the same thing its Schedules Direct provider uses as ShowId.
///
/// Streams the document: each <c>&lt;programme&gt;</c> is materialised on its own (the
/// date comes after the title, so it can't be done token by token) and everything else is
/// copied straight through, so a 14-day, ~30k-programme, ~28 MB guide never sits in memory
/// as one tree.
/// </summary>
public static class GuideCleaner
{
    private const int MaxSamples = 10;

    private static readonly Regex TrailingYear = new(
        @"^(?<name>.*\S)\s*\((?<year>\d{4})\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TmsProgramId = new(
        @"^(EP|MV|SH|SP)\d{8,}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public sealed record CleanResult(int Programmes, int TitlesCleaned, int ProgramIdsAdded, IReadOnlyList<string> Samples);

    /// <param name="stripYears">Remove a year already in a title (the first fix).</param>
    /// <param name="copyIds">Copy tms ids to dd_progid (the second fix). Both off: the guide passes
    /// through unchanged, only checked and counted.</param>
    public static CleanResult Clean(Stream input, Stream output, CancellationToken cancellationToken, bool stripYears = true, bool copyIds = true)
    {
        var readerSettings = new XmlReaderSettings
        {
            // The feed declares <!DOCTYPE tv SYSTEM "xmltv.dtd">; don't try to fetch or
            // process it. The DOCTYPE is dropped from the output, which Jellyfin's XMLTV
            // reader doesn't need.
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreWhitespace = true,
            IgnoreComments = false
        };
        var writerSettings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true
        };

        int programmes = 0;
        int cleaned = 0;
        int idsAdded = 0;
        var samples = new List<string>();

        using var reader = XmlReader.Create(input, readerSettings);
        using var writer = XmlWriter.Create(output, writerSettings);

        writer.WriteStartDocument();
        reader.MoveToContent();
        if (reader.NodeType != XmlNodeType.Element)
        {
            throw new InvalidDataException("Upstream guide has no root element.");
        }

        // Root (<tv>): copy the start tag and attributes by hand so its children can be
        // walked one at a time instead of copying the whole subtree in one go.
        writer.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
        writer.WriteAttributes(reader, true);
        bool rootIsEmpty = reader.IsEmptyElement;
        reader.Read();

        if (!rootIsEmpty)
        {
            while (!reader.EOF && reader.NodeType != XmlNodeType.EndElement)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "programme")
                {
                    // ReadFrom advances the reader past the element.
                    var programme = (XElement)XNode.ReadFrom(reader);
                    programmes++;
                    if (stripYears && CleanProgramme(programme, out var sample))
                    {
                        cleaned++;
                        if (samples.Count < MaxSamples && !samples.Contains(sample))
                        {
                            samples.Add(sample);
                        }
                    }

                    if (copyIds && AddProgramId(programme))
                    {
                        idsAdded++;
                    }

                    programme.WriteTo(writer);
                }
                else
                {
                    // <channel> and anything else: copied verbatim (also advances).
                    writer.WriteNode(reader, true);
                }
            }
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();

        return new CleanResult(programmes, cleaned, idsAdded, samples);
    }

    private static bool AddProgramId(XElement programme)
    {
        XElement? tms = null;
        foreach (var num in programme.Elements("episode-num"))
        {
            var system = (string?)num.Attribute("system");
            if (system == "dd_progid")
            {
                return false;
            }

            if (system == "tms" && tms is null && TmsProgramId.IsMatch(num.Value.Trim()))
            {
                tms = num;
            }
        }

        if (tms is null)
        {
            return false;
        }

        tms.AddAfterSelf(new XElement("episode-num", new XAttribute("system", "dd_progid"), tms.Value.Trim()));
        return true;
    }

    private static bool CleanProgramme(XElement programme, out string sample)
    {
        sample = string.Empty;

        var date = programme.Element("date")?.Value.Trim();
        if (date is null || date.Length < 4)
        {
            return false;
        }

        var dateYear = date[..4];
        bool changed = false;

        // A programme may carry several <title lang="..."> variants; clean each one.
        foreach (var title in programme.Elements("title"))
        {
            var match = TrailingYear.Match(title.Value.Trim());
            if (!match.Success || match.Groups["year"].Value != dateYear)
            {
                continue;
            }

            if (!changed)
            {
                sample = title.Value.Trim() + " -> " + match.Groups["name"].Value;
            }

            title.Value = match.Groups["name"].Value;
            changed = true;
        }

        return changed;
    }
}
