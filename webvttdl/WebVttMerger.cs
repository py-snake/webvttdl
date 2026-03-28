using System;
using System.Collections.Generic;
using System.Text;

namespace webvttdl
{
    public static class WebVttMerger
    {
        // Merges a list of WebVTT segment content strings into a single VTT string.
        // Segment 0 is used verbatim (keeps the WEBVTT header and X-TIMESTAMP-MAP etc.).
        // Subsequent segments are stripped down to just their cue blocks before appending.
        public static string Merge(List<string> segmentContents)
        {
            var result = new StringBuilder();

            for (int i = 0; i < segmentContents.Count; i++)
            {
                string content = StripHeader(segmentContents[i], i == 0);
                if (string.IsNullOrEmpty(content))
                    continue;

                if (i > 0)
                {
                    // Trim any trailing newlines the previous segment left, then add
                    // exactly one blank separator line between segments.
                    while (result.Length > 0 && result[result.Length - 1] == '\n')
                        result.Length--;
                    result.Append('\n'); // end previous segment with one newline
                    result.Append('\n'); // blank line separator
                }

                result.Append(content);

                // Ensure segment ends with a newline.
                if (!content.EndsWith("\n"))
                    result.Append('\n');
            }

            return result.ToString();
        }

        // Strips everything before the first cue in non-first segments.
        // isFirst=true: return content verbatim (normalize line endings only).
        // isFirst=false: advance to the first timing line (" --> "), stripping the WEBVTT
        //   header, X-TIMESTAMP-MAP=MPEGTS:... lines, and any other per-segment header tags.
        //   If the line immediately before the timing line is non-blank it is a cue ID
        //   and is preserved.
        private static string StripHeader(string content, bool isFirst)
        {
            // Normalize line endings.
            content = content.Replace("\r\n", "\n").Replace("\r", "\n");

            // Strip UTF-8 BOM if present (curl stdout may include it).
            if (content.Length > 0 && content[0] == '\uFEFF')
                content = content.Substring(1);

            if (isFirst)
                return content;

            string[] lines = content.Split('\n');

            // Find the first cue timing line (contains " --> ").
            int timingIdx = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf(" --> ", StringComparison.Ordinal) >= 0)
                {
                    timingIdx = i;
                    break;
                }
            }

            if (timingIdx < 0)
                return string.Empty; // Segment contains no cues.

            // Preserve a cue ID if it sits on the line immediately before the timing line.
            int startIdx = timingIdx;
            if (timingIdx > 0 && lines[timingIdx - 1].Trim() != string.Empty)
                startIdx = timingIdx - 1;

            var sb = new StringBuilder();
            for (int j = startIdx; j < lines.Length; j++)
            {
                sb.Append(lines[j]);
                if (j < lines.Length - 1)
                    sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
