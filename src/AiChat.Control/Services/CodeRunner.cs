using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiChat.Control.Services
{
    /// <summary>
    /// Executes code from chat code blocks on the local machine — possible because the
    /// WebView runs locally and the host is a desktop app. Every language maps to a
    /// user-configurable command template ("{file}" is replaced with the temp file path);
    /// languages without a configured command are refused. stdout/stderr stream back to
    /// the UI line by line; runs can be stopped and are killed after a timeout.
    /// </summary>
    public sealed class CodeRunner : IDisposable
    {
        /// <summary>runId → running process, for Stop.</summary>
        private readonly ConcurrentDictionary<string, Process> _running =
            new ConcurrentDictionary<string, Process>(StringComparer.Ordinal);

        private readonly string _workDir;

        public CodeRunner(string dataDirectory)
        {
            _workDir = Path.Combine(dataDirectory, "runs");
            Directory.CreateDirectory(_workDir);
        }

        /// <summary>Sensible Windows defaults; fully editable in Settings and stored as JSON.</summary>
        public static Dictionary<string, RunCommand> DefaultCommands() =>
            new Dictionary<string, RunCommand>(StringComparer.OrdinalIgnoreCase)
            {
                ["python"]     = new RunCommand { Extension = ".py",  Command = "python \"{file}\"" },
                ["javascript"] = new RunCommand { Extension = ".js",  Command = "node \"{file}\"" },
                ["typescript"] = new RunCommand { Extension = ".ts",  Command = "npx tsx \"{file}\"" },
                ["powershell"] = new RunCommand { Extension = ".ps1", Command = "powershell -NoProfile -ExecutionPolicy Bypass -File \"{file}\"" },
                ["batch"]      = new RunCommand { Extension = ".bat", Command = "cmd /c \"{file}\"" },
                ["csharp"]     = new RunCommand { Extension = ".csx", Command = "dotnet script \"{file}\"" },
            };

        public static Dictionary<string, RunCommand> ParseCommands(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return DefaultCommands();
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, RunCommand>>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return parsed != null && parsed.Count > 0
                    ? new Dictionary<string, RunCommand>(parsed, StringComparer.OrdinalIgnoreCase)
                    : DefaultCommands();
            }
            catch
            {
                return DefaultCommands();
            }
        }

        /// <summary>
        /// Writes the code to a temp file, launches the configured command, and pumps
        /// output through <paramref name="onOutput"/>. Returns the exit code, or null
        /// when the run was stopped/killed.
        /// </summary>
        public async Task<int?> RunAsync(
            string runId,
            string language,
            string code,
            RunCommand runCommand,
            int timeoutSeconds,
            Action<string, bool> onOutput, // (text, isStderr)
            CancellationToken ct)
        {
            string ext = string.IsNullOrWhiteSpace(runCommand.Extension) ? ".txt" : runCommand.Extension;
            if (!ext.StartsWith(".")) ext = "." + ext;
            string file = Path.Combine(_workDir, $"run_{runId}{ext}");
            File.WriteAllText(file, code, new UTF8Encoding(false));

            string commandLine = runCommand.Command.Replace("{file}", file);

            var psi = new ProcessStartInfo
            {
                // cmd /c so templates can be full shell commands (pipes, chained args, PATH lookup).
                FileName = "cmd.exe",
                Arguments = "/c " + commandLine,
                WorkingDirectory = _workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (s, e) => exited.TrySetResult(true);
            process.OutputDataReceived += (s, e) => { if (e.Data != null) onOutput(e.Data, false); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) onOutput(e.Data, true); };

            if (!process.Start())
                throw new InvalidOperationException("Failed to start the process.");

            _running[runId] = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                var timeout = Task.Delay(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)), ct);
                var finished = await Task.WhenAny(exited.Task, timeout).ConfigureAwait(false);

                if (finished != exited.Task)
                {
                    KillTree(process);
                    onOutput(ct.IsCancellationRequested
                        ? "[stopped by user]"
                        : $"[killed after {timeoutSeconds}s timeout]", true);
                    return null;
                }

                // Let redirected streams flush their final lines.
                process.WaitForExit();
                return process.ExitCode;
            }
            finally
            {
                _running.TryRemove(runId, out _);
                try { File.Delete(file); } catch { /* best effort */ }
                process.Dispose();
            }
        }

        public void Stop(string runId)
        {
            if (_running.TryGetValue(runId, out var p)) KillTree(p);
        }

        private static void KillTree(Process p)
        {
            try
            {
                if (p.HasExited) return;
                // taskkill /T takes the whole child tree down (e.g. python spawning subprocesses).
                using (var kill = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {p.Id} /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    kill?.WaitForExit(3000);
                }
            }
            catch { /* already gone */ }
        }

        public void Dispose()
        {
            foreach (var p in _running.Values) KillTree(p);
            _running.Clear();
        }
    }

    public sealed class RunCommand
    {
        /// <summary>Temp file extension, e.g. ".py".</summary>
        public string Extension { get; set; }
        /// <summary>Command template; "{file}" is replaced with the temp file path.</summary>
        public string Command { get; set; }
    }
}
