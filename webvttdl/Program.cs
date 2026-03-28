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
        public bool Help;           // -h / --help
        public string Error;        // set on parse failure
    }

    class Program
    {
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

            Log("Master URL:   " + opts.Url);
            if (!string.IsNullOrEmpty(opts.Language))
                Log("Language:     " + opts.Language);
            Log("Output dir:   " + outDir);

            return Download(opts, curl, outDir);
        }

        // -------------------------------------------------------------------------
        // Core download pipeline
        // -------------------------------------------------------------------------

        static int Download(CliArgs opts, CurlDownloader curl, string outDir)
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
                Log("No subtitle tracks found in the master playlist.");
                return 0;
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

                List<string> segmentUrls = M3u8Parser.ParseMediaPlaylist(
                    playlistContent, track.ResolvedPlaylistUrl);

                Log(string.Format("  Found {0} segment(s)", segmentUrls.Count));

                if (segmentUrls.Count == 0)
                {
                    Console.Error.WriteLine("  WARNING: No segments found, skipping.");
                    continue;
                }

                // Download all segments into RAM.
                var segmentContents = new List<string>(segmentUrls.Count);
                int failed = 0;
                for (int j = 0; j < segmentUrls.Count; j++)
                {
                    Console.Write(string.Format(
                        "\r  Downloading segment {0}/{1}...    ", j + 1, segmentUrls.Count));

                    string segContent = curl.DownloadString(segmentUrls[j]);
                    if (segContent == null)
                    {
                        failed++;
                        continue;
                    }
                    segmentContents.Add(segContent);
                }
                Console.WriteLine();

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

                string vttPath = Path.Combine(outDir, baseName + ".vtt");
                string srtPath = Path.Combine(outDir, baseName + ".srt");

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
        // Argument parser
        // -------------------------------------------------------------------------

        static CliArgs ParseArgs(string[] args)
        {
            var opts = new CliArgs();

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];

                // -h / --help
                if (a == "-h" || a == "--help")
                {
                    opts.Help = true;
                    return opts;
                }

                // Flags with no value
                if (a == "--no-srt") { opts.NoSrt = true; continue; }
                if (a == "--no-vtt") { opts.NoVtt = true; continue; }

                // Options that take a value — support both "--key value" and "--key=value"
                string key = null, val = null;

                if (a.StartsWith("--") && a.Contains("="))
                {
                    int eq = a.IndexOf('=');
                    key = a.Substring(0, eq);
                    val = a.Substring(eq + 1);
                }
                else if (a.StartsWith("-"))
                {
                    key = a;
                    // Always consume the next arg as the value for options that require one.
                    // (Valueless flags like --no-srt/--help are handled above and never reach here.)
                    if (i + 1 < args.Length)
                    {
                        val = args[i + 1];
                        i++;
                    }
                }

                if (key != null)
                {
                    switch (key)
                    {
                        case "-o":
                        case "--output":
                            if (val == null) { opts.Error = "--output requires a value."; return opts; }
                            opts.OutputName = SanitizeFilenameAscii(val);
                            if (string.IsNullOrEmpty(opts.OutputName))
                            {
                                opts.Error = "--output value contains no valid filename characters.";
                                return opts;
                            }
                            break;

                        case "-d":
                        case "--output-dir":
                            if (val == null) { opts.Error = "--output-dir requires a value."; return opts; }
                            opts.OutputDir = val;
                            break;

                        case "-l":
                        case "--lang":
                            if (val == null) { opts.Error = "--lang requires a value."; return opts; }
                            opts.Language = val;
                            break;

                        case "--curl":
                            if (val == null) { opts.Error = "--curl requires a value."; return opts; }
                            opts.CurlPath = val;
                            break;

                        case "--curl-opts":
                            if (val == null) { opts.Error = "--curl-opts requires a value."; return opts; }
                            opts.CurlOpts = val;
                            break;

                        default:
                            opts.Error = "Unknown option: " + key;
                            return opts;
                    }
                    continue;
                }

                // Positional argument: the URL
                if (opts.Url == null)
                    opts.Url = a;
                else
                {
                    opts.Error = "Unexpected argument: " + a;
                    return opts;
                }
            }

            return opts;
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
            Console.WriteLine("  -h, --help                Show this help message.");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  webvttdl.exe \"https://cdn.example.com/stream/index.m3u8\"");
            Console.WriteLine("  webvttdl.exe -l hu -o mysub -d C:\\Subs \"https://cdn.example.com/index.m3u8\"");
            Console.WriteLine("  webvttdl.exe --lang en --no-vtt \"https://cdn.example.com/index.m3u8\"");
            Console.WriteLine("  webvttdl.exe --curl C:\\tools\\curl.exe \"https://cdn.example.com/index.m3u8\"");
            Console.WriteLine("  webvttdl.exe --curl-opts \"-x http://proxy:8080\" \"https://cdn.example.com/index.m3u8\"");
            Console.WriteLine("  webvttdl.exe --curl-opts \"--retry 3 --max-time 60\" \"https://cdn.example.com/index.m3u8\"");
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
