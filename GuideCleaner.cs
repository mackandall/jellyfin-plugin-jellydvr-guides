using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Plugin.XmltvCleaner;

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

    public sealed record CleanResult(int Programmes, int TitlesCleaned, IReadOnlyList<string> Samples);

    public static CleanResult Clean(Stream input, Stream output, CancellationToken cancellationToken)
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
                    if (CleanProgramme(programme, out var sample))
                    {
                        cleaned++;
                        if (samples.Count < MaxSamples && !samples.Contains(sample))
                        {
                            samples.Add(sample);
                        }
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

        return new CleanResult(programmes, cleaned, samples);
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
