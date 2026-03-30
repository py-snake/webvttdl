using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace webvttdl
{
    class CliArgs
    {
        public string Url;          // positional: master M3U8 URL (required)
        public string CurlPath;     // --curl <path>
        public string CurlOpts;     // --curl-opts <extra flags>
        public string OutputName;   // -o / --output <basename without extension>
        public string OutputDir;    // -d / --output-dir <dir>
        public string Language;     // -l / --lang <code>  (filter tracks)
        public bool NoSrt;          // --no-srt  (VTT only)
        public bool NoVtt;          // --no-vtt  (SRT only, VTT not kept)
        public bool LiveMode;       // --live   (force live recording mode)
        public int Duration;        // --duration <sec>  (0 = unlimited)
        public int PollInterval;    // --poll <sec>  (0 = use EXT-X-TARGETDURATION)
        public int Retries;         // --retries <n>  (per-segment retry attempts, default 3)
        public bool Help;           // -h / --help
        public string Error;        // set on parse failure
    }

    class Program
    {
        // Set to true by the Ctrl+C handler; checked in the live poll loop.
        private static volatile bool _cancelled = false;

        static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);

            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FATAL: " + ex.Message);
                return 1;
            }
        }

        static int Run(string[] args)
        {
            CliArgs opts = ParseArgs(args);

            if (opts.Error != null)
            {
                Console.Error.WriteLine("ERROR: " + opts.Error);
                Console.Error.WriteLine("Run with --help for usage.");
                return 1;
            }

            if (opts.Help)
            {
                PrintHelp();
                return 0;
            }

            if (opts.Url == null)
            {
                PrintHelp();
                return 1;
            }

            if (opts.NoSrt && opts.NoVtt)
            {
                Console.Error.WriteLine("ERROR: --no-srt and --no-vtt cannot be used together.");
                return 1;
            }

            // Resolve output directory.
            string outDir = string.IsNullOrEmpty(opts.OutputDir)
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(opts.OutputDir);

            if (!Directory.Exists(outDir))
            {
                try { Directory.CreateDirectory(outDir); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("ERROR: Cannot create output directory: " + ex.Message);
                    return 1;
                }
            }

            // Initialise curl.
            CurlDownloader curl;
            try
            {
                curl = new CurlDownloader(
                    string.IsNullOrEmpty(opts.CurlPath) ? null : opts.CurlPath,
                    opts.CurlOpts);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 1;
            }

            // Timestamp prefix computed once so all files from this run share it.
            string runStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            Log("Master URL:   " + opts.Url);
            if (!string.IsNullOrEmpty(opts.Language))
                Log("Language:     " + opts.Language);
            Log("Output dir:   " + outDir);

            // First Ctrl+C: set cancel flag (graceful stop after current segment).
            // Second Ctrl+C: exit immediately without saving.
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                if (_cancelled)
                {
                    // Already cancelling — force exit now.
                    Console.Error.WriteLine("\n  [Ctrl+C again -- force exit, no files saved.]");
                    System.Environment.Exit(1);
                }
                e.Cancel = true;
                _cancelled = true;
                Console.Error.WriteLine("\n  [Ctrl+C -- stopping after current segment... press again to force exit]");
            };

            return Download(opts, curl, outDir, runStamp);
        }

        // -------------------------------------------------------------------------
        // Core download pipeline
        // -------------------------------------------------------------------------

        static int Download(CliArgs opts, CurlDownloader curl, string outDir, string runStamp)
        {
            Log("Downloading master playlist...");
            string masterContent = curl.DownloadString(opts.Url);
            if (masterContent == null)
            {
                Console.Error.WriteLine("ERROR: Failed to download master playlist.");
                return 1;
            }
            Log(string.Format("Master playlist downloaded ({0} bytes)", masterContent.Length));

            List<SubtitleTrack> tracks = M3u8Parser.ParseMasterPlaylist(masterContent, opts.Url);

            if (tracks.Count == 0)
            {
                // No subtitle tracks found — check if the URL is itself a media playlist
                // (user passed a subtitle .m3u8 directly instead of a master index.m3u8).
                var directInfo = M3u8Parser.ParseMediaPlaylistInfo(masterContent, opts.Url);
                if (directInfo.SegmentUrls.Count > 0 || directInfo.FirstSequenceNumber > 0)
                {
                    Log("URL appears to be a subtitle media playlist — using it directly.");
                    string directUri = opts.Url.Substring(opts.Url.LastIndexOf('/') + 1);
                    int q = directUri.IndexOf('?');
                    if (q >= 0) directUri = directUri.Substring(0, q);
                    tracks = new List<SubtitleTrack>
                    {
                        new SubtitleTrack
                        {
                            Uri = directUri,
                            Language = "und",
                            Name = "subtitles",
                            GroupId = string.Empty,
                            ResolvedPlaylistUrl = opts.Url
                        }
                    };
                }
                else
                {
                    Log("No subtitle tracks found in the master playlist.");
                    return 0;
                }
            }

            // Apply language filter if requested.
            if (!string.IsNullOrEmpty(opts.Language))
            {
                var filtered = new List<SubtitleTrack>();
                foreach (SubtitleTrack t in tracks)
                {
                    if (t.Language.Equals(opts.Language, StringComparison.OrdinalIgnoreCase))
                        filtered.Add(t);
                }

                if (filtered.Count == 0)
                {
                    Console.Error.WriteLine(string.Format(
                        "ERROR: No subtitle tracks found for language '{0}'.", opts.Language));
                    Console.Error.WriteLine("Available tracks:");
                    foreach (SubtitleTrack t in tracks)
                        Console.Error.WriteLine(string.Format("  [{0}] {1}", t.Language, t.Name));
                    return 1;
                }
                tracks = filtered;
            }

            Log(string.Format("Found {0} subtitle track(s):", tracks.Count));
            foreach (SubtitleTrack t in tracks)
                Log(string.Format("  [{0}] {1}  ->  {2}", t.Language, t.Name, t.ResolvedPlaylistUrl));

            for (int i = 0; i < tracks.Count; i++)
            {
                SubtitleTrack track = tracks[i];
                Log(string.Format(
                    "\n[Track {0}/{1}]  [{2}] {3}",
                    i + 1, tracks.Count, track.Language, track.Name));

                Log("  Downloading subtitle playlist...");
                string playlistContent = curl.DownloadString(track.ResolvedPlaylistUrl);
                if (playlistContent == null)
                {
                    Console.Error.WriteLine("  ERROR: Failed to download subtitle playlist, skipping.");
                    continue;
                }

                var playlistInfo = M3u8Parser.ParseMediaPlaylistInfo(
                    playlistContent, track.ResolvedPlaylistUrl);

                // Determine output base name.
                // --output overrides; if multiple tracks are present, append _<lang> to avoid collision.
                string baseName;
                if (!string.IsNullOrEmpty(opts.OutputName))
                {
                    baseName = (tracks.Count > 1)
                        ? opts.OutputName + "_" + SanitizeFilenameAscii(track.Language)
                        : opts.OutputName;
                }
                else
                {
                    baseName = GetBaseNameFromUri(track.Uri);
                }

                // Prepend run timestamp so every invocation produces unique filenames.
                string stampedBaseName = runStamp + "_" + baseName;

                bool isLive = opts.LiveMode || playlistInfo.IsLive;

                if (isLive)
                {
                    Log(string.Format("  Live stream detected (target duration: {0}s).",
                        playlistInfo.TargetDuration));
                    int liveResult = DownloadLive(opts, curl, outDir, track, stampedBaseName, playlistInfo);
                    if (liveResult != 0)
                        Console.Error.WriteLine("  WARNING: Live capture ended with errors.");
                    continue;
                }

                // --- VOD path ---
                List<string> segmentUrls = playlistInfo.SegmentUrls;

                Log(string.Format("  Found {0} segment(s)", segmentUrls.Count));

                if (segmentUrls.Count == 0)
                {
                    Console.Error.WriteLine("  WARNING: No segments found, skipping.");
                    continue;
                }

                // Download all segments into RAM.
                int maxRetries = opts.Retries > 0 ? opts.Retries : 3;
                var segmentContents = new List<string>(segmentUrls.Count);
                int failed = 0;
                for (int j = 0; j < segmentUrls.Count && !_cancelled; j++)
                {
                    Console.Write(string.Format(
                        "\r  Downloading segment {0}/{1}...    ", j + 1, segmentUrls.Count));

                    string segContent = DownloadWithRetry(curl, segmentUrls[j], j, maxRetries);
                    if (segContent == null)
                    {
                        failed++;
                        continue;
                    }
                    segmentContents.Add(segContent);
                }
                Console.WriteLine();

                if (_cancelled)
                {
                    Console.Error.WriteLine("  Cancelled.");
                    continue;
                }

                if (segmentContents.Count == 0)
                {
                    Console.Error.WriteLine("  ERROR: No segments downloaded, skipping track.");
                    continue;
                }

                if (failed > 0)
                    Console.Error.WriteLine(string.Format(
                        "  WARNING: {0} segment(s) failed to download.", failed));

                Log(string.Format("  Downloaded {0}/{1} segments.",
                    segmentContents.Count, segmentUrls.Count));

                string vttPath = Path.Combine(outDir, stampedBaseName + ".vtt");
                string srtPath = Path.Combine(outDir, stampedBaseName + ".srt");

                // Merge all segments in RAM.
                string mergedVtt = WebVttMerger.Merge(segmentContents);

                if (!opts.NoVtt)
                {
                    Log("  Writing VTT -> " + vttPath);
                    File.WriteAllText(vttPath, mergedVtt, new UTF8Encoding(false));
                }

                if (!opts.NoSrt)
                {
                    Log("  Converting to SRT -> " + srtPath);
                    WebVttToSrtConverter.Convert(mergedVtt, srtPath);
                }

                Log("  Done.");
            }

            Log("\nAll done.");
            return 0;
        }

        // -------------------------------------------------------------------------
        // Live stream recording loop
        // -------------------------------------------------------------------------

        static int DownloadLive(CliArgs opts, CurlDownloader curl, string outDir,
                                SubtitleTrack track, string baseName,
                                MediaPlaylistInfo initialInfo)
        {
            string vttPath = Path.Combine(outDir, baseName + ".vtt");
            string srtPath = Path.Combine(outDir, baseName + ".srt");

            var merger = new WebVttMerger.IncrementalMerger();
            int maxRetries = opts.Retries > 0 ? opts.Retries : 3;

            // pendingBuffer: successfully downloaded content, held until all earlier
            // sequence numbers are resolved so we always flush in order.
            var pendingBuffer = new SortedDictionary<long, string>();

            // retryQueue: segments still failing after inline retries — seqNum -> url.
            var retryQueue = new SortedDictionary<long, string>();

            // gapSet: sequences permanently lost (expired from CDN window).
            // Treated as empty segments when flushing so later seqs can unblock.
            var gapSet = new System.Collections.Generic.HashSet<long>();

            long lastSeenSeq    = initialInfo.FirstSequenceNumber - 1; // highest seq attempted
            long lastFlushedSeq = initialInfo.FirstSequenceNumber - 1; // highest seq flushed
            int  totalSegments  =  0;

            DateTime? stopAt = opts.Duration > 0
                ? (DateTime?)DateTime.UtcNow.AddSeconds(opts.Duration)
                : null;

            Log("  Live mode -- press Ctrl+C to stop.");
            if (stopAt.HasValue)
                Log(string.Format("  Auto-stop after {0}s.", opts.Duration));

            MediaPlaylistInfo info = initialInfo;
            bool firstPoll = true;

            while (!_cancelled)
            {
                if (stopAt.HasValue && DateTime.UtcNow >= stopAt.Value)
                {
                    Console.WriteLine();
                    Log("  Duration limit reached.");
                    break;
                }

                // --- Refresh the media playlist ---
                if (!firstPoll)
                {
                    string playlistContent = curl.DownloadString(track.ResolvedPlaylistUrl);
                    if (playlistContent == null)
                    {
                        Console.Error.WriteLine("\n  WARNING: Failed to refresh playlist, retrying...");
                        SleepCancellable(2000);
                        continue;
                    }
                    info = M3u8Parser.ParseMediaPlaylistInfo(playlistContent, track.ResolvedPlaylistUrl);
                }
                firstPoll = false;

                // --- Expire segments that fell off the CDN window ---
                long windowStart = info.FirstSequenceNumber;
                var toExpire = new List<long>();
                foreach (long seq in retryQueue.Keys)
                    if (seq < windowStart) toExpire.Add(seq);
                foreach (long seq in toExpire)
                {
                    Console.Error.WriteLine(string.Format(
                        "\n  WARNING: Segment seq={0} expired from CDN window, lost.", seq));
                    gapSet.Add(seq);
                    retryQueue.Remove(seq);
                }

                // --- Retry queued segments ---
                var retryKeys = new List<long>(retryQueue.Keys);
                foreach (long seq in retryKeys)
                {
                    if (_cancelled) break;
                    string content = DownloadWithRetry(curl, retryQueue[seq], seq, maxRetries);
                    if (content != null)
                    {
                        pendingBuffer[seq] = content;
                        retryQueue.Remove(seq);
                    }
                }

                // --- Download new segments ---
                for (int j = 0; j < info.SegmentUrls.Count && !_cancelled; j++)
                {
                    long seqNum = info.FirstSequenceNumber + j;
                    if (seqNum <= lastSeenSeq) continue;

                    string segContent = DownloadWithRetry(curl, info.SegmentUrls[j], seqNum, maxRetries);
                    if (segContent != null)
                        pendingBuffer[seqNum] = segContent;
                    else
                        retryQueue[seqNum] = info.SegmentUrls[j];

                    lastSeenSeq = seqNum;
                }

                // --- Flush contiguous segments in order ---
                // Advance past gaps (lost segments) and flush downloaded ones.
                var flushBatch = new List<string>();
                while (true)
                {
                    long next = lastFlushedSeq + 1;
                    if (gapSet.Contains(next))
                    {
                        gapSet.Remove(next);
                        lastFlushedSeq = next; // skip the gap
                    }
                    else if (pendingBuffer.ContainsKey(next))
                    {
                        flushBatch.Add(pendingBuffer[next]);
                        pendingBuffer.Remove(next);
                        lastFlushedSeq = next;
                    }
                    else
                    {
                        break; // hole still present — wait for retry or expiry
                    }
                }

                if (flushBatch.Count > 0)
                {
                    totalSegments += flushBatch.Count;
                    merger.AddSegments(flushBatch);

                    string mergedVtt = merger.ToVtt();
                    if (!opts.NoVtt)
                        File.WriteAllText(vttPath, mergedVtt, new UTF8Encoding(false));
                    if (!opts.NoSrt)
                        WebVttToSrtConverter.Convert(mergedVtt, srtPath);

                    Console.Write(string.Format(
                        "\r  +{0} seg | total: {1} | cues: {2} | seq: {3} | queued: {4} | {5} | {6}    ",
                        flushBatch.Count, totalSegments, merger.CueCount,
                        lastFlushedSeq, retryQueue.Count,
                        merger.LastCueEndTime, merger.LastCueText));
                }

                // If the server sent EXT-X-ENDLIST the stream has ended.
                if (!info.IsLive && !opts.LiveMode)
                {
                    Console.WriteLine();
                    Log("  Stream ended (EXT-X-ENDLIST received).");
                    break;
                }

                if (_cancelled) break;

                int sleepMs = (opts.PollInterval > 0 ? opts.PollInterval : info.TargetDuration) * 1000;
                SleepCancellable(sleepMs);
            }

            Console.WriteLine();

            if (totalSegments == 0)
            {
                Console.Error.WriteLine("  No segments captured.");
                return 1;
            }

            Log(string.Format("  Captured {0} segment(s), {1} cues.", totalSegments, merger.CueCount));
            if (!opts.NoVtt) Log("  VTT -> " + vttPath);
            if (!opts.NoSrt) Log("  SRT -> " + srtPath);
            return 0;
        }

        // Downloads a URL with up to maxAttempts tries, sleeping 1s between each.
        // Returns null only if all attempts fail.
        static string DownloadWithRetry(CurlDownloader curl, string url, long seqNum, int maxAttempts)
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                string content = curl.DownloadString(url);
                if (content != null)
                    return content;

                if (attempt < maxAttempts)
                {
                    Console.Error.WriteLine(string.Format(
                        "\n  WARNING: Segment seq={0} failed (attempt {1}/{2}), retrying in 1s...",
                        seqNum, attempt, maxAttempts));
                    SleepCancellable(1000);
                }
                else
                {
                    Console.Error.WriteLine(string.Format(
                        "\n  WARNING: Segment seq={0} failed after {1} attempts, queuing for retry.",
                        seqNum, maxAttempts));
                }
            }
            return null;
        }

        // Sleeps for up to totalMs milliseconds, waking every 250 ms to check _cancelled.
        static void SleepCancellable(int totalMs)
        {
            int waited = 0;
            while (waited < totalMs && !_cancelled)
            {
                System.Threading.Thread.Sleep(250);
                waited += 250;
            }
        }

        // -------------------------------------------------------------------------
        // Argument parser
        // -------------------------------------------------------------------------

        static CliArgs ParseArgs(string[] args)
        {
            var opts = new CliArgs();

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];

                // Help
                if (a == "-h" || a == "--help")
                { opts.Help = true; return opts; }

                // Value-less flags — handled before the value-consuming branch so
                // they are never mistaken for option names that need a value.
                if (a == "--no-srt") { opts.NoSrt = true; continue; }
                if (a == "--no-vtt") { opts.NoVtt = true; continue; }
                if (a == "--live")   { opts.LiveMode = true; continue; }

                // Named options: support both "-k value"/"--key value" and "-k=value"/"--key=value".
                if (a.StartsWith("-"))
                {
                    string key, val;
                    int eq = a.IndexOf('=');

                    if (eq > 0)
                    {
                        // "--key=value" or "-k=value" form
                        key = a.Substring(0, eq);
                        val = a.Substring(eq + 1);
                        // Boolean flags never take a value.
                        if (key == "--no-srt" || key == "--no-vtt" || key == "--live")
                        {
                            opts.Error = string.Format(
                                "'{0}' is a flag and does not accept a value.", key);
                            return opts;
                        }
                        if (val.Length == 0)
                        {
                            opts.Error = string.Format(
                                "'{0}' requires a non-empty value. Use: {0} <value> or {0}=<value>", key);
                            return opts;
                        }
                    }
                    else
                    {
                        // "--key value" or "-k value" form — consume next arg as value
                        key = a;
                        if (i + 1 >= args.Length)
                        {
                            opts.Error = string.Format("'{0}' requires a value.", key);
                            return opts;
                        }
                        val = args[++i];
                    }

                    // Strip surrounding matching quotes that Windows CMD passes through
                    // literally when the user wraps a value in single quotes, e.g.
                    // --lang 'hu' or --duration '01:10:00'.
                    // Double quotes are normally stripped by CMD itself, but handle
                    // them here too for symmetry (Mono/Wine edge cases).
                    if (val.Length >= 2 &&
                        ((val[0] == '\'' && val[val.Length - 1] == '\'') ||
                         (val[0] == '"'  && val[val.Length - 1] == '"')))
                        val = val.Substring(1, val.Length - 2);

                    switch (key)
                    {
                        case "-o":
                        case "--output":
                            opts.OutputName = SanitizeFilenameAscii(val);
                            if (string.IsNullOrEmpty(opts.OutputName))
                            { opts.Error = "--output: '" + val + "' contains no valid ASCII filename characters."; return opts; }
                            break;

                        case "-d":
                        case "--output-dir":
                            opts.OutputDir = val;
                            break;

                        case "-l":
                        case "--lang":
                            opts.Language = val;
                            break;

                        case "--curl":
                            opts.CurlPath = val;
                            break;

                        case "--curl-opts":
                            opts.CurlOpts = val;
                            break;

                        case "--duration":
                            if (!TryParseDuration(val, out opts.Duration) || opts.Duration <= 0)
                            { opts.Error = "--duration: expected seconds (e.g. 3600) or HH:MM:SS (e.g. 01:10:00), got '" + val + "'."; return opts; }
                            break;

                        case "--poll":
                            if (!int.TryParse(val, out opts.PollInterval) || opts.PollInterval <= 0)
                            { opts.Error = "--poll: expected a positive integer (seconds), got '" + val + "'."; return opts; }
                            break;

                        case "--retries":
                            if (!int.TryParse(val, out opts.Retries) || opts.Retries < 1)
                            { opts.Error = "--retries: expected a positive integer, got '" + val + "'."; return opts; }
                            break;

                        default:
                            opts.Error = "Unknown option: '" + key + "'. Run with --help for usage.";
                            return opts;
                    }
                    continue;
                }

                // Positional argument: the URL
                if (opts.Url == null)
                    opts.Url = a;
                else
                {
                    opts.Error = "Unexpected argument: '" + a + "'. Only one URL is accepted.";
                    return opts;
                }
            }

            return opts;
        }

        // Parses a duration string: plain integer seconds OR HH:MM:SS.
        // Returns false on parse failure.
        static bool TryParseDuration(string val, out int seconds)
        {
            seconds = 0;
            if (string.IsNullOrEmpty(val))
                return false;

            if (val.IndexOf(':') >= 0)
            {
                // HH:MM:SS format
                string[] parts = val.Split(':');
                if (parts.Length != 3)
                    return false;
                int h, m, s;
                if (!int.TryParse(parts[0], out h) || h < 0)  return false;
                if (!int.TryParse(parts[1], out m) || m < 0 || m > 59) return false;
                if (!int.TryParse(parts[2], out s) || s < 0 || s > 59) return false;
                seconds = h * 3600 + m * 60 + s;
                return true;
            }

            return int.TryParse(val, out seconds);
        }

        // -------------------------------------------------------------------------
        // Help text
        // -------------------------------------------------------------------------

        static void PrintHelp()
        {
            Console.WriteLine("webvttdl - WebVTT subtitle downloader for HLS streams");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  webvttdl.exe [options] <master-m3u8-url>");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -o, --output <name>       Output base filename (no extension).");
            Console.WriteLine("                            Default: derived from the subtitle playlist filename.");
            Console.WriteLine("                            If multiple tracks are downloaded, _<lang> is appended.");
            Console.WriteLine("  -d, --output-dir <dir>    Directory to write output files to.");
            Console.WriteLine("                            Default: current directory. Created if missing.");
            Console.WriteLine("  -l, --lang <code>         Only download tracks matching this language code.");
            Console.WriteLine("                            Example: hu, en, de  (case-insensitive).");
            Console.WriteLine("      --curl <path>         Path to curl executable.");
            Console.WriteLine("                            Default: auto-detected from app directory or PATH.");
            Console.WriteLine("      --curl-opts <flags>   Extra flags passed verbatim to every curl call.");
            Console.WriteLine("                            Useful for proxies, auth, timeouts, retries, etc.");
            Console.WriteLine("      --no-srt              Output VTT only; skip SRT conversion.");
            Console.WriteLine("      --no-vtt              Output SRT only; do not write the VTT file.");
            Console.WriteLine("      --live                Force live recording mode.");
            Console.WriteLine("                            Normally auto-detected from the playlist.");
            Console.WriteLine("      --duration <time>     Stop live recording after the given duration.");
            Console.WriteLine("                            Accepts seconds (e.g. 3600) or HH:MM:SS (e.g. 01:10:00).");
            Console.WriteLine("      --poll <sec>          Playlist refresh interval for live mode.");
            Console.WriteLine("                            Default: EXT-X-TARGETDURATION from the playlist.");
            Console.WriteLine("      --retries <n>         Per-segment download retry attempts (default: 3).");
            Console.WriteLine("                            Failed segments are also queued across polls");
            Console.WriteLine("                            until they expire from the CDN window.");
            Console.WriteLine("  -h, --help                Show this help message.");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  webvttdl.exe \"https://cdn.example.com/stream/index.m3u8\"");
            Console.WriteLine("  webvttdl.exe -l hu -o mysub -d C:\\Subs \"https://cdn.example.com/index.m3u8\"");
            Console.WriteLine("  webvttdl.exe --lang en --no-vtt \"https://cdn.example.com/index.m3u8\"");
            Console.WriteLine("  webvttdl.exe --curl C:\\tools\\curl.exe \"https://cdn.example.com/index.m3u8\"");
            Console.WriteLine("  webvttdl.exe --curl-opts \"-x http://proxy:8080\" \"https://cdn.example.com/index.m3u8\"");
            Console.WriteLine("  webvttdl.exe --curl-opts \"--retry 3 --max-time 60\" \"https://cdn.example.com/index.m3u8\"");
            Console.WriteLine("  webvttdl.exe --live \"https://cdn.example.com/live/index.m3u8\"");
            Console.WriteLine("  webvttdl.exe --live --duration 3600 \"https://cdn.example.com/live/index.m3u8\"");
            Console.WriteLine("  webvttdl.exe --live --duration 01:10:00 \"https://cdn.example.com/live/index.m3u8\"");
            Console.WriteLine("  webvttdl.exe --live --poll 4 --retries 5 \"https://cdn.example.com/live/index.m3u8\"");
            Console.WriteLine();
            Console.WriteLine("Notes:");
            Console.WriteLine("  curl.exe must be available. Place it next to webvttdl.exe or add it to PATH.");
            Console.WriteLine("  SRT files are written UTF-8 with BOM (required by Windows XP media players).");
            Console.WriteLine("  VTT files are written UTF-8 without BOM (per WebVTT spec).");
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        static void Log(string message)
        {
            Console.WriteLine(message);
        }

        static string GetBaseNameFromUri(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return "subtitles";

            string s = uri;
            int q = s.IndexOf('?');
            if (q >= 0) s = s.Substring(0, q);

            int slash = Math.Max(s.LastIndexOf('/'), s.LastIndexOf('\\'));
            if (slash >= 0) s = s.Substring(slash + 1);

            int dot = s.LastIndexOf('.');
            if (dot > 0) s = s.Substring(0, dot);

            s = SanitizeFilenameAscii(s);
            return string.IsNullOrEmpty(s) ? "subtitles" : s;
        }

        static string SanitizeFilenameAscii(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') ||
                    c == '_' || c == '-' || c == '=' || c == '.')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            return sb.ToString();
        }
    }
}
