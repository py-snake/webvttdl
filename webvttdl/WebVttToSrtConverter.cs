using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
// Note: System.IO is still needed for StreamWriter (writing the .srt output file).

namespace webvttdl
{
    public static class WebVttToSrtConverter
    {
        // Regex that matches any HTML-like tag that is NOT <i>, </i>, <b>, </b>, <u>, </u>.
        // These basic formatting tags are preserved as SRT supports them.
        private static readonly Regex _stripTagsRegex = new Regex(
            @"<(?!/?(i|b|u)>)[^>]*>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Matches the two timestamps in a VTT cue timing line, e.g.:
        //   00:00:01.500 --> 00:00:04.000 align:center position:50%
        // Captures group 1 = start, group 2 = end (without trailing settings).
        private static readonly Regex _timingRegex = new Regex(
            @"^(\d{2}:\d{2}:\d{2}\.\d{3})\s+-->\s+(\d{2}:\d{2}:\d{2}\.\d{3})",
            RegexOptions.Compiled);

        // Converts merged WebVTT content (as a string) to an SRT file.
        // Writes UTF-8 with BOM for Windows XP media player compatibility.
        public static void Convert(string vttContent, string srtFilePath)
        {
            // Strip UTF-8 BOM if present, normalize line endings.
            string content = vttContent;
            if (content.Length > 0 && content[0] == '\uFEFF')
                content = content.Substring(1);

            // Normalize line endings.
            content = content.Replace("\r\n", "\n").Replace("\r", "\n");

            // Split into blocks separated by one or more blank lines.
            string[] blocks = Regex.Split(content, @"\n{2,}");

            // UTF8Encoding(true) = UTF-8 with BOM. Windows XP media players need this
            // to correctly display non-ASCII characters (öüóőúáűí) in .srt files.
            using (var writer = new StreamWriter(srtFilePath, false, new UTF8Encoding(true)))
            {
                int cueNumber = 0;

                for (int b = 0; b < blocks.Length; b++)
                {
                    string block = blocks[b].Trim();
                    if (string.IsNullOrEmpty(block))
                        continue;

                    // Skip the WEBVTT file header block (first non-empty block starting with WEBVTT).
                    if (block.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase) && cueNumber == 0)
                        continue;

                    // Skip NOTE, STYLE, and REGION blocks.
                    if (block.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase) ||
                        block.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase) ||
                        block.StartsWith("REGION", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string[] lines = block.Split('\n');

                    // Find the timing line (the one containing " --> ").
                    int timingIdx = -1;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].IndexOf(" --> ", StringComparison.Ordinal) >= 0)
                        {
                            timingIdx = i;
                            break;
                        }
                    }

                    // If no timing line found this is not a cue block; skip it.
                    if (timingIdx < 0)
                        continue;

                    // Parse and convert the timing line.
                    string srtTiming = ConvertTimingLine(lines[timingIdx]);
                    if (srtTiming == null)
                        continue;

                    // Collect cue text lines (everything after the timing line).
                    var textLines = new List<string>();
                    for (int i = timingIdx + 1; i < lines.Length; i++)
                    {
                        string stripped = StripVttTags(lines[i]);
                        textLines.Add(stripped);
                    }

                    // Remove trailing empty lines from cue text.
                    while (textLines.Count > 0 &&
                           string.IsNullOrEmpty(textLines[textLines.Count - 1]))
                        textLines.RemoveAt(textLines.Count - 1);

                    if (textLines.Count == 0)
                        continue;

                    cueNumber++;

                    // Write SRT cue block with \r\n line endings (Windows convention).
                    writer.Write(cueNumber.ToString());
                    writer.Write("\r\n");
                    writer.Write(srtTiming);
                    writer.Write("\r\n");
                    foreach (string tl in textLines)
                    {
                        writer.Write(tl);
                        writer.Write("\r\n");
                    }
                    writer.Write("\r\n");
                }
            }
        }

        // Converts a VTT timing line to SRT format.
        // VTT: 00:00:01.500 --> 00:00:04.000 align:center
        // SRT: 00:00:01,500 --> 00:00:04,000
        private static string ConvertTimingLine(string line)
        {
            Match m = _timingRegex.Match(line);
            if (!m.Success)
                return null;

            string start = m.Groups[1].Value.Replace('.', ',');
            string end = m.Groups[2].Value.Replace('.', ',');
            return start + " --> " + end;
        }

        // Strips WebVTT-specific tags from cue text, preserving basic <i>, <b>, <u> formatting.
        // Removes: <c.class>, </c>, <ruby>, </ruby>, <rt>, </rt>, <v Name>, <00:00:01.000>, etc.
        private static string StripVttTags(string text)
        {
            return _stripTagsRegex.Replace(text, string.Empty);
        }
    }
}
