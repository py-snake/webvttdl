using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace webvttdl
{
    public static class WebVttMerger
    {
        // Matches a full VTT cue timing line:
        //   HH:MM:SS.mmm --> HH:MM:SS.mmm [optional cue settings]
        private static readonly Regex _timingLineRegex = new Regex(
            @"^(\d{2}:\d{2}:\d{2}\.\d{3})\s+-->\s+(\d{2}:\d{2}:\d{2}\.\d{3})(.*)?$",
            RegexOptions.Compiled);

        // Merges a list of WebVTT segment content strings into a single VTT string
        // with corrected absolute timestamps.
        //
        // Each HLS WebVTT segment has timestamps relative to its own segment start.
        // The X-TIMESTAMP-MAP=MPEGTS:<pts>,LOCAL:<time> header maps those local times
        // to the MPEG-TS 90 kHz clock so we can compute the absolute stream offset.
        //
        // offset_ms = (MPEGTS_N - MPEGTS_0) / 90  +  local_0_ms  -  local_N_ms
        public static string Merge(List<string> segmentContents)
        {
            // --- Pass 1: parse MPEGTS values from every segment ---
            long[] mpegTs = new long[segmentContents.Count];
            long[] localMs = new long[segmentContents.Count];
            bool[] hasTsMap = new bool[segmentContents.Count];

            for (int i = 0; i < segmentContents.Count; i++)
            {
                long pts; long loc;
                hasTsMap[i] = TryParseMpegTsMap(segmentContents[i], out pts, out loc);
                mpegTs[i] = pts;
                localMs[i] = loc;
            }

            // Base values come from the first segment that has a map.
            long baseMpegTs = 0;
            long baseLocalMs = 0;
            for (int i = 0; i < segmentContents.Count; i++)
            {
                if (hasTsMap[i]) { baseMpegTs = mpegTs[i]; baseLocalMs = localMs[i]; break; }
            }

            // --- Pass 2: strip headers, apply offsets, and concatenate ---
            var result = new StringBuilder();
            result.Append("WEBVTT\n\n");

            for (int i = 0; i < segmentContents.Count; i++)
            {
                long offsetMs = 0;
                if (hasTsMap[i])
                {
                    // offset_ms = (MPEGTS_N - MPEGTS_0) / 90  +  local_0_ms - local_N_ms
                    // MPEG-TS clock is 90 kHz → divide by 90 to get milliseconds.
                    offsetMs = (long)Math.Round((double)(mpegTs[i] - baseMpegTs) / 90.0)
                               + baseLocalMs - localMs[i];
                }

                string cues = ExtractCues(segmentContents[i]);
                if (string.IsNullOrEmpty(cues))
                    continue;

                if (offsetMs != 0)
                    cues = ApplyOffset(cues, offsetMs);

                // Trim trailing whitespace from this segment's cues, then always
                // append exactly one blank line after them. This guarantees a proper
                // cue separator between every pair of adjacent segments without
                // ever accumulating extra blank lines.
                cues = cues.TrimEnd('\r', '\n');
                if (string.IsNullOrEmpty(cues))
                    continue;

                result.Append(cues);
                result.Append('\n'); // end of last text line
                result.Append('\n'); // blank line — required WebVTT cue separator
            }

            // --- Pass 3: deduplicate adjacent cues with identical text ---
            // The DFXP→WebVTT segmenter splits each subtitle at every 2-second and
            // segment boundary, producing 2-4 consecutive cues with the same text.
            // Merge them into one cue spanning from the first start to the last end.
            List<VttCue> cueList = ParseCues(result.ToString());
            cueList = MergeAdjacentDuplicates(cueList);
            return EmitVtt(cueList);
        }

        // -------------------------------------------------------------------------
        // Cue representation used for deduplication
        // -------------------------------------------------------------------------

        private class VttCue
        {
            public long StartMs;
            public long EndMs;
            public string Settings; // cue settings string, e.g. " align:middle line:95%,end position:50%"
            public string Text;     // cue text, newline-joined lines (may contain WebVTT markup)
        }

        // Parses a full VTT string (including WEBVTT header) into a list of cues.
        private static List<VttCue> ParseCues(string vtt)
        {
            var cues = new List<VttCue>();
            // Normalise and strip WEBVTT header block.
            string content = vtt.Replace("\r\n", "\n").Replace("\r", "\n");
            // Split on blank lines.
            string[] blocks = Regex.Split(content, @"\n{2,}");
            foreach (string rawBlock in blocks)
            {
                string block = rawBlock.Trim();
                if (string.IsNullOrEmpty(block)) continue;
                if (block.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)) continue;

                string[] lines = block.Split('\n');
                int timingIdx = -1;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (_timingLineRegex.IsMatch(lines[i])) { timingIdx = i; break; }
                }
                if (timingIdx < 0) continue;

                Match m = _timingLineRegex.Match(lines[timingIdx]);
                var textLines = new List<string>();
                for (int i = timingIdx + 1; i < lines.Length; i++)
                    textLines.Add(lines[i]);

                cues.Add(new VttCue
                {
                    StartMs  = ParseTimestampMs(m.Groups[1].Value),
                    EndMs    = ParseTimestampMs(m.Groups[2].Value),
                    Settings = m.Groups[3].Value,
                    Text     = string.Join("\n", textLines.ToArray()).Trim()
                });
            }
            return cues;
        }

        // Merges consecutive cues whose text is identical and whose timestamps are
        // adjacent (end of one == start of the next, within a 2 ms tolerance).
        private static List<VttCue> MergeAdjacentDuplicates(List<VttCue> cues)
        {
            if (cues.Count == 0) return cues;
            var result = new List<VttCue>(cues.Count);
            VttCue cur = cues[0];
            for (int i = 1; i < cues.Count; i++)
            {
                VttCue next = cues[i];
                bool adjacent = Math.Abs(next.StartMs - cur.EndMs) <= 2;
                bool sameText = string.Equals(next.Text, cur.Text, StringComparison.Ordinal);
                if (adjacent && sameText)
                {
                    cur.EndMs = next.EndMs; // extend end time
                }
                else
                {
                    result.Add(cur);
                    cur = next;
                }
            }
            result.Add(cur);
            return result;
        }

        // Emits a well-formed VTT string from a cue list.
        private static string EmitVtt(List<VttCue> cues)
        {
            var sb = new StringBuilder();
            sb.Append("WEBVTT\n\n");
            foreach (VttCue cue in cues)
            {
                sb.Append(FormatTimestampMs(cue.StartMs));
                sb.Append(" --> ");
                sb.Append(FormatTimestampMs(cue.EndMs));
                sb.Append(cue.Settings);
                sb.Append('\n');
                sb.Append(cue.Text);
                sb.Append('\n');
                sb.Append('\n');
            }
            return sb.ToString();
        }

        // -------------------------------------------------------------------------
        // Incremental merger for live recording — O(new_segments) per update.
        // Holds parsed cue state; only new segments are processed each call.
        // -------------------------------------------------------------------------
        public class IncrementalMerger
        {
            private long _baseMpegTs;
            private long _baseLocalMs;
            private bool _baseSet;
            private readonly List<VttCue> _cues = new List<VttCue>();

            public int CueCount { get { return _cues.Count; } }

            // Processes a batch of new segment content strings and appends their
            // cues to the internal list.  Returns the number of cues added.
            public int AddSegments(List<string> segments)
            {
                // Establish base MPEGTS from the first segment that carries a map.
                if (!_baseSet)
                {
                    foreach (string seg in segments)
                    {
                        long pts, loc;
                        if (TryParseMpegTsMap(seg, out pts, out loc))
                        {
                            _baseMpegTs = pts;
                            _baseLocalMs = loc;
                            _baseSet = true;
                            break;
                        }
                    }
                }

                var newCues = new List<VttCue>();
                foreach (string seg in segments)
                {
                    long offsetMs = 0;
                    if (_baseSet)
                    {
                        long pts, loc;
                        if (TryParseMpegTsMap(seg, out pts, out loc))
                            offsetMs = (long)Math.Round((double)(pts - _baseMpegTs) / 90.0)
                                       + _baseLocalMs - loc;
                    }

                    string raw = ExtractCues(seg);
                    if (string.IsNullOrEmpty(raw)) continue;
                    if (offsetMs != 0) raw = ApplyOffset(raw, offsetMs);

                    // Re-use ParseCues by wrapping the raw cue block in a minimal header.
                    newCues.AddRange(ParseCues("WEBVTT\n\n" + raw));
                }

                if (newCues.Count == 0) return 0;

                // Pull the last existing cue back so the boundary dedup can merge it
                // with the first cue(s) of the new batch if they have identical text.
                VttCue boundary = _cues.Count > 0 ? _cues[_cues.Count - 1] : null;
                if (boundary != null)
                    _cues.RemoveAt(_cues.Count - 1);

                var toMerge = new List<VttCue>();
                if (boundary != null) toMerge.Add(boundary);
                toMerge.AddRange(newCues);
                toMerge = MergeAdjacentDuplicates(toMerge);

                _cues.AddRange(toMerge);
                return toMerge.Count - (boundary != null ? 1 : 0);
            }

            // Returns the accumulated content as a well-formed VTT string.
            public string ToVtt()
            {
                return EmitVtt(_cues);
            }
        }

        // -------------------------------------------------------------------------
        // Strips the WEBVTT header block (WEBVTT line, X-TIMESTAMP-MAP, NOTE/REGION
        // header blocks) and returns only the cue content.
        // Works for both first and subsequent segments.
        // -------------------------------------------------------------------------
        private static string ExtractCues(string content)
        {
            // Normalize line endings and strip BOM.
            content = content.Replace("\r\n", "\n").Replace("\r", "\n");
            if (content.Length > 0 && content[0] == '\uFEFF')
                content = content.Substring(1);

            string[] lines = content.Split('\n');

            // Find the first cue timing line.
            int timingIdx = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (_timingLineRegex.IsMatch(lines[i]))
                {
                    timingIdx = i;
                    break;
                }
            }

            if (timingIdx < 0)
                return string.Empty;

            // Include the cue ID line if it sits immediately before the timing line.
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

        // -------------------------------------------------------------------------
        // Parses X-TIMESTAMP-MAP=MPEGTS:<pts>,LOCAL:<HH:MM:SS.mmm> from a segment.
        // Returns false if the line is absent.
        // -------------------------------------------------------------------------
        private static bool TryParseMpegTsMap(string content, out long mpegTs, out long localMs)
        {
            mpegTs = 0;
            localMs = 0;

            foreach (string rawLine in content.Split('\n'))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("X-TIMESTAMP-MAP=", StringComparison.OrdinalIgnoreCase))
                    continue;

                string rest = line.Substring("X-TIMESTAMP-MAP=".Length);

                // Parse MPEGTS:<value>
                int mpegtsStart = rest.IndexOf("MPEGTS:", StringComparison.OrdinalIgnoreCase);
                if (mpegtsStart < 0) continue;
                mpegtsStart += 7;
                int mpegtsEnd = rest.IndexOf(',', mpegtsStart);
                string mpegtsStr = mpegtsEnd > 0
                    ? rest.Substring(mpegtsStart, mpegtsEnd - mpegtsStart).Trim()
                    : rest.Substring(mpegtsStart).Trim();

                if (!long.TryParse(mpegtsStr, out mpegTs)) continue;

                // Parse LOCAL:<HH:MM:SS.mmm>
                int localStart = rest.IndexOf("LOCAL:", StringComparison.OrdinalIgnoreCase);
                if (localStart < 0) { localMs = 0; return true; }
                localStart += 6;
                string localStr = rest.Substring(localStart).Trim();
                int commaIdx = localStr.IndexOf(',');
                if (commaIdx > 0) localStr = localStr.Substring(0, commaIdx).Trim();

                localMs = ParseTimestampMs(localStr);
                return true;
            }
            return false;
        }

        // -------------------------------------------------------------------------
        // Adds offsetMs to every cue start and end timestamp in a block of VTT cues.
        // -------------------------------------------------------------------------
        private static string ApplyOffset(string cues, long offsetMs)
        {
            string[] lines = cues.Split('\n');
            var sb = new StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                Match m = _timingLineRegex.Match(lines[i]);
                if (m.Success)
                {
                    long startMs = ParseTimestampMs(m.Groups[1].Value) + offsetMs;
                    long endMs   = ParseTimestampMs(m.Groups[2].Value) + offsetMs;
                    string settings = m.Groups[3].Value; // leading space + cue settings or empty
                    sb.Append(FormatTimestampMs(startMs));
                    sb.Append(" --> ");
                    sb.Append(FormatTimestampMs(endMs));
                    sb.Append(settings);
                }
                else
                {
                    sb.Append(lines[i]);
                }

                if (i < lines.Length - 1)
                    sb.Append('\n');
            }
            return sb.ToString();
        }

        // -------------------------------------------------------------------------
        // Timestamp helpers — integer millisecond arithmetic to avoid float drift.
        // -------------------------------------------------------------------------

        // Parses HH:MM:SS.mmm → milliseconds.
        private static long ParseTimestampMs(string ts)
        {
            ts = ts.Trim();
            string[] colon = ts.Split(':');
            if (colon.Length != 3) return 0;

            long h = 0, m = 0, s = 0, ms = 0;
            long.TryParse(colon[0], out h);
            long.TryParse(colon[1], out m);

            string secPart = colon[2];
            int dot = secPart.IndexOf('.');
            if (dot >= 0)
            {
                long.TryParse(secPart.Substring(0, dot), out s);
                string msPart = secPart.Substring(dot + 1);
                while (msPart.Length < 3) msPart += "0";
                if (msPart.Length > 3) msPart = msPart.Substring(0, 3);
                long.TryParse(msPart, out ms);
            }
            else
            {
                long.TryParse(secPart, out s);
            }

            return h * 3600000L + m * 60000L + s * 1000L + ms;
        }

        // Formats milliseconds → HH:MM:SS.mmm.
        private static string FormatTimestampMs(long totalMs)
        {
            if (totalMs < 0) totalMs = 0;
            long h  = totalMs / 3600000L; totalMs %= 3600000L;
            long m  = totalMs / 60000L;   totalMs %= 60000L;
            long s  = totalMs / 1000L;
            long ms = totalMs % 1000L;
            return string.Format("{0:D2}:{1:D2}:{2:D2}.{3:D3}", h, m, s, ms);
        }
    }
}
