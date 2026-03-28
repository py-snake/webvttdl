# webvttdl

A .NET Framework 4.0 console application for downloading WebVTT subtitles from HLS streams. Designed to run on Windows XP and newer.

Because Windows XP cannot negotiate modern TLS (1.2/1.3) required by most CDNs, `webvttdl` delegates all HTTP requests to an external `curl.exe`. All M3U8 parsing, URL resolution, segment merging, and WebVTT→SRT conversion are handled internally.

## Features

- Downloads all subtitle tracks from an HLS master playlist (`index.m3u8`)
- Merges all WebVTT segments into a single `.vtt` file per track
- Converts the merged `.vtt` to `.srt` automatically
- Output filenames derived from the subtitle playlist name (e.g. `M2_2_..._hun_CAPT=30000.vtt`)
- Filters by language, custom output name/directory, proxy support via `--curl-opts`
- Runs on Windows XP SP3+ (.NET 4.0), Wine, and Linux/macOS (Mono)

## Requirements

- **.NET Framework 4.0** (Windows) or **Mono** (Linux/macOS)
- **curl.exe** — place next to `webvttdl.exe` or add to `PATH`
  - curl handles HTTPS with modern TLS; use any build with SSL support
  - Download: https://curl.se/windows/

## Building

**Windows** (requires .NET Framework 4.0 SDK or Visual Studio):
```bat
build.cmd
```

**Linux / macOS** (requires Mono):
```sh
sudo dnf install mono-devel   # Fedora/RHEL
sudo apt install mono-devel   # Debian/Ubuntu

chmod +x build.sh
./build.sh
```

Output: `webvttdl/bin/Release/webvttdl.exe`

## Usage

```
webvttdl.exe [options] <master-m3u8-url>
```

### Options

| Option | Short | Description |
|--------|-------|-------------|
| `--output <name>` | `-o` | Output base filename without extension. Default: derived from the subtitle playlist filename. When multiple tracks are downloaded, `_<lang>` is appended automatically. |
| `--output-dir <dir>` | `-d` | Directory to write output files to. Default: current directory. Created automatically if missing. |
| `--lang <code>` | `-l` | Only download tracks matching this language code (e.g. `hu`, `en`). Case-insensitive. |
| `--curl <path>` | | Explicit path to `curl.exe`. Default: auto-detected from the app directory, then `PATH`. |
| `--curl-opts <flags>` | | Extra flags passed verbatim to every curl invocation. Use for proxies, authentication, timeouts, retries, etc. |
| `--no-srt` | | Output `.vtt` only; skip SRT conversion. |
| `--no-vtt` | | Output `.srt` only; do not write the `.vtt` file. |
| `--help` | `-h` | Show help message. |

Both `--key value` and `--key=value` forms are accepted.

### Examples

Download all subtitle tracks from a stream:
```bat
webvttdl.exe "https://cdn.example.com/stream/index.m3u8"
```

Download only the Hungarian track, save to `C:\Subs\` with a custom name:
```bat
webvttdl.exe -l hu -o mysub -d C:\Subs "https://cdn.example.com/stream/index.m3u8"
```

Download English subtitles as SRT only (no VTT kept):
```bat
webvttdl.exe --lang en --no-vtt "https://cdn.example.com/stream/index.m3u8"
```

Use a specific curl executable:
```bat
webvttdl.exe --curl C:\tools\curl.exe "https://cdn.example.com/stream/index.m3u8"
```

Route traffic through an HTTP proxy:
```bat
webvttdl.exe --curl-opts "-x http://proxy.corp:8080" "https://cdn.example.com/stream/index.m3u8"
```

Route traffic through a SOCKS5 proxy:
```bat
webvttdl.exe --curl-opts "--socks5 127.0.0.1:1080" "https://cdn.example.com/stream/index.m3u8"
```

Add retry logic and a longer timeout:
```bat
webvttdl.exe --curl-opts "--retry 3 --max-time 60" "https://cdn.example.com/stream/index.m3u8"
```

Run on Linux with Mono:
```sh
mono webvttdl.exe "https://cdn.example.com/stream/index.m3u8"
```

## Output files

For a subtitle playlist named `M2_2_20260326T..._hun_CAPT=30000.m3u8` the tool produces:

```
M2_2_20260326T..._hun_CAPT=30000.vtt   (UTF-8, no BOM — per WebVTT spec)
M2_2_20260326T..._hun_CAPT=30000.srt   (UTF-8 with BOM, CRLF — for Windows XP media players)
```

Filenames are sanitized to ASCII-safe characters only (`A-Z a-z 0-9 _ - = .`), so they are compatible with every filesystem including FAT32.

## How it works

1. `curl` downloads the master M3U8 playlist (stdout → RAM)
2. The app parses `#EXT-X-MEDIA:TYPE=SUBTITLES` entries to find subtitle tracks
3. For each track, `curl` downloads the subtitle media playlist (stdout → RAM)
4. Segment URLs are resolved and each segment is downloaded via `curl` (stdout → RAM)
5. All segments are merged in RAM — `WEBVTT` headers and `X-TIMESTAMP-MAP` tags are stripped from all but the first segment
6. The merged VTT string is written to disk as `.vtt`
7. The same string is converted to SRT (timestamps, tag stripping) and written as `.srt`

No temporary files are written at any point.

## WebVTT → SRT conversion

- Timestamps converted from `HH:MM:SS.mmm` to `HH:MM:SS,mmm`
- Cue settings (e.g. `align:center position:50%`) stripped
- WebVTT-specific tags (`<c.class>`, `<v Name>`, `<ruby>`, timestamp tags, etc.) removed
- Basic formatting tags (`<i>`, `<b>`, `<u>`) preserved — SRT supports them
- Cues with empty text after stripping are skipped
- Output uses sequential numbering starting from 1

## Project structure

```
webvttdl/
  webvttdl.sln
  build.cmd                    Windows MSBuild script
  build.sh                     Linux/macOS Mono build script
  webvttdl/
    webvttdl.csproj
    Program.cs                 Entry point, CLI argument parser, orchestration
    M3u8Parser.cs              Parses master and media M3U8 playlists, resolves URLs
    CurlDownloader.cs          curl.exe wrapper (auto-detection, extra args)
    WebVttMerger.cs            Merges WebVTT segments, strips per-segment headers
    WebVttToSrtConverter.cs    Converts merged WebVTT content to SRT format
    Properties/AssemblyInfo.cs
```
