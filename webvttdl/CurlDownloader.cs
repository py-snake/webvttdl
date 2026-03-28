using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace webvttdl
{
    public class CurlDownloader
    {
        private readonly string _curlPath;
        private readonly string _extraArgs;

        // Auto-detects curl from app directory or PATH.
        // extraArgs: optional string appended verbatim to every curl invocation
        //   (e.g. "-x http://proxy:8080" or "--retry 3 --max-time 60")
        public CurlDownloader(string explicitPath = null, string extraArgs = null)
        {
            _curlPath = string.IsNullOrEmpty(explicitPath) ? LocateCurl() : ValidateExplicitPath(explicitPath);
            _extraArgs = string.IsNullOrEmpty(extraArgs) ? null : extraArgs.Trim();
        }

        private static string ValidateExplicitPath(string path)
        {
            if (!File.Exists(path))
                throw new Exception("curl not found at specified path: " + path);
            return path;
        }

        // Downloads a URL to a local file. Returns true on success.
        public bool DownloadToFile(string url, string destFilePath)
        {
            string args = string.Format(
                "-k -L -s --connect-timeout 30{0} -o \"{1}\" \"{2}\"",
                _extraArgs != null ? " " + _extraArgs : string.Empty,
                destFilePath.Replace("\"", "\\\""),
                url.Replace("\"", "%22"));

            var result = RunCurl(args);
            if (result.ExitCode != 0)
            {
                Console.Error.WriteLine(string.Format(
                    "  [curl error {0}] {1}", result.ExitCode, result.Stderr.Trim()));
                return false;
            }
            return true;
        }

        // Downloads a URL and returns the response body as a UTF-8 string.
        public string DownloadString(string url)
        {
            string args = string.Format(
                "-k -L -s --connect-timeout 30{0} \"{1}\"",
                _extraArgs != null ? " " + _extraArgs : string.Empty,
                url.Replace("\"", "%22"));

            var result = RunCurl(args);
            if (result.ExitCode != 0)
            {
                Console.Error.WriteLine(string.Format(
                    "  [curl error {0}] {1}", result.ExitCode, result.Stderr.Trim()));
                return null;
            }
            return result.Stdout;
        }

        private CurlResult RunCurl(string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _curlPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            var proc = new Process { StartInfo = psi };
            proc.Start();

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            return new CurlResult
            {
                ExitCode = proc.ExitCode,
                Stdout = stdout,
                Stderr = stderr
            };
        }

        private static string LocateCurl()
        {
            // 1. Look next to the executable
            string appDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);

            string[] candidates = new string[]
            {
                Path.Combine(appDir, "curl.exe"),
                Path.Combine(appDir, "curl")
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            // 2. Search each directory in PATH
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(dir))
                    continue;
                string trimmed = dir.Trim();
                string p = Path.Combine(trimmed, "curl.exe");
                if (File.Exists(p))
                    return p;
                p = Path.Combine(trimmed, "curl");
                if (File.Exists(p))
                    return p;
            }

            throw new Exception(
                "curl not found. Place curl.exe next to webvttdl.exe or add curl to PATH.");
        }
    }

    internal struct CurlResult
    {
        public int ExitCode;
        public string Stdout;
        public string Stderr;
    }
}
