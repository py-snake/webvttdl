using System;
using System.Collections.Generic;

namespace webvttdl
{
    public class SubtitleTrack
    {
        public string Uri;
        public string Language;
        public string Name;
        public string GroupId;
        public string ResolvedPlaylistUrl;
    }

    // Richer result from parsing a media (subtitle) playlist.
    public class MediaPlaylistInfo
    {
        public List<string> SegmentUrls;
        public long FirstSequenceNumber;  // from #EXT-X-MEDIA-SEQUENCE, or 0
        public int TargetDuration;        // from #EXT-X-TARGETDURATION, or 8
        public bool IsLive;               // true when #EXT-X-ENDLIST is absent
    }

    public static class M3u8Parser
    {
        // Parses master M3U8 content and returns all SUBTITLES tracks with resolved URLs.
        public static List<SubtitleTrack> ParseMasterPlaylist(string content, string masterUrl)
        {
            var result = new List<SubtitleTrack>();
            if (string.IsNullOrEmpty(content))
                return result;

            string[] lines = content.Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase))
                    continue;

                string attrString = line.Substring("#EXT-X-MEDIA:".Length);
                var attrs = ParseAttributes(attrString);

                string type;
                if (!attrs.TryGetValue("TYPE", out type) ||
                    !type.Equals("SUBTITLES", StringComparison.OrdinalIgnoreCase))
                    continue;

                string uri;
                if (!attrs.TryGetValue("URI", out uri) || string.IsNullOrEmpty(uri))
                    continue;

                string language = GetAttr(attrs, "LANGUAGE");
                string name = GetAttr(attrs, "NAME");
                string groupId = GetAttr(attrs, "GROUP-ID");

                var track = new SubtitleTrack
                {
                    Uri = uri,
                    Language = language,
                    Name = name,
                    GroupId = groupId,
                    ResolvedPlaylistUrl = ResolveUrl(masterUrl, uri)
                };
                result.Add(track);
            }
            return result;
        }

        // Parses a media (subtitle) M3U8 playlist and returns absolute segment URLs.
        public static List<string> ParseMediaPlaylist(string content, string playlistUrl)
        {
            return ParseMediaPlaylistInfo(content, playlistUrl).SegmentUrls;
        }

        // Parses a media playlist and returns full info including sequence number,
        // target duration, live flag, and resolved segment URLs.
        public static MediaPlaylistInfo ParseMediaPlaylistInfo(string content, string playlistUrl)
        {
            var info = new MediaPlaylistInfo
            {
                SegmentUrls = new List<string>(),
                FirstSequenceNumber = 0,
                TargetDuration = 8,
                IsLive = true   // assumed live until #EXT-X-ENDLIST is seen
            };

            if (string.IsNullOrEmpty(content))
                return info;

            foreach (string rawLine in content.Split('\n'))
            {
                string line = rawLine.Trim();

                if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
                {
                    long seq;
                    if (long.TryParse(line.Substring("#EXT-X-MEDIA-SEQUENCE:".Length).Trim(), out seq))
                        info.FirstSequenceNumber = seq;
                }
                else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase))
                {
                    int dur;
                    if (int.TryParse(line.Substring("#EXT-X-TARGETDURATION:".Length).Trim(), out dur))
                        info.TargetDuration = dur;
                }
                else if (line.Equals("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
                {
                    info.IsLive = false;
                }
                else if (!string.IsNullOrEmpty(line) && !line.StartsWith("#"))
                {
                    info.SegmentUrls.Add(ResolveUrl(playlistUrl, line));
                }
            }
            return info;
        }

        // Resolves a possibly-relative URI against a base URL using System.Uri (RFC 3986).
        // The base URL's query string is automatically dropped during relative resolution.
        public static string ResolveUrl(string baseUrl, string relativeUri)
        {
            Uri baseUri = new Uri(baseUrl);
            Uri resolved = new Uri(baseUri, relativeUri);
            return resolved.AbsoluteUri;
        }

        // Parses an M3U8 attribute list string into a dictionary.
        // Handles quoted values (which may contain commas) correctly.
        // Example input: TYPE=SUBTITLES,URI="track.m3u8",LANGUAGE="hu",NAME="magyar"
        private static Dictionary<string, string> ParseAttributes(string attrString)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(attrString))
                return result;

            bool inQuote = false;
            int tokenStart = 0;

            for (int i = 0; i <= attrString.Length; i++)
            {
                char c = (i < attrString.Length) ? attrString[i] : '\0';

                if (c == '"')
                {
                    inQuote = !inQuote;
                }
                else if ((c == ',' || i == attrString.Length) && !inQuote)
                {
                    string token = attrString.Substring(tokenStart, i - tokenStart).Trim();
                    tokenStart = i + 1;

                    if (string.IsNullOrEmpty(token))
                        continue;

                    int eqIdx = token.IndexOf('=');
                    if (eqIdx <= 0)
                        continue;

                    string key = token.Substring(0, eqIdx).Trim();
                    string value = token.Substring(eqIdx + 1).Trim();

                    // Strip surrounding quotes
                    if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                        value = value.Substring(1, value.Length - 2);

                    result[key] = value;
                }
            }

            return result;
        }

        private static string GetAttr(Dictionary<string, string> attrs, string key)
        {
            string value;
            return attrs.TryGetValue(key, out value) ? value : string.Empty;
        }
    }
}
