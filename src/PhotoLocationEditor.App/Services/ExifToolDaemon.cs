using System.Diagnostics;
using System.IO;
using System.Text;

namespace PhotoLocationEditor.App.Services;

/// <summary>
/// Keeps a single persistent exiftool.exe (-stay_open True) process alive so sequential
/// metadata reads/writes execute in ~15-40ms instead of paying 500ms+ Perl cold-start per call.
/// </summary>
public sealed class ExifToolDaemon : IAsyncDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly string _exifToolPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private StreamReader? _stderr;
    private long _commandCounter;
    private bool _disposed;

    public ExifToolDaemon(string exifToolPath)
    {
        _exifToolPath = exifToolPath;
    }

    public bool IsRunning => _process is { HasExited: false };

    public async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(_exifToolPath))
            throw new FileNotFoundException("ExifTool was not found.", _exifToolPath);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStartedAsync().ConfigureAwait(false);
            var id = Interlocked.Increment(ref _commandCounter);
            var readyMarker = $"{{ready{id}}}";
            var errMarker = $"{{err_ready{id}}}";

            try
            {
                var sb = new StringBuilder();
                foreach (var raw in arguments)
                {
                    var sanitized = raw.Replace("\r", " ").Replace("\n", " ");
                    sb.Append(sanitized).Append('\n');
                }

                // Force stderr flush deterministically using -echo4 before -executeN
                sb.Append("-echo4\n").Append(errMarker).Append('\n');
                sb.Append("-execute").Append(id).Append('\n');

                await _stdin!.WriteAsync(sb.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
                await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);

                var stdoutTask = ReadUntilMarkerAsync(_stdout!, readyMarker, cancellationToken);
                var stderrTask = ReadUntilMarkerAsync(_stderr!, errMarker, cancellationToken);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

                var output = stdoutTask.Result.result;
                var error = stderrTask.Result.result;
                if (!stdoutTask.Result.foundMarker)
                {
                    ResetProcess();
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(error) ? "ExifTool process exited unexpectedly." : error.Trim());
                }

                // ExifTool returns exit-like errors in stderr when fatal
                var hasError = ContainsFatalError(error);
                return (hasError ? 1 : 0, output, error.Trim());
            }
            catch (OperationCanceledException)
            {
                // Canceling an active -stay_open command mid-stream leaves pipes out of sync; kill & respawn cleanly next call.
                ResetProcess();
                throw;
            }
            catch
            {
                ResetProcess();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_exifToolPath))
            return;
        await ExecuteAsync(["-ver"], cancellationToken).ConfigureAwait(false);
    }

    private Task EnsureStartedAsync()
    {
        if (_process is { HasExited: false } && _stdin is not null && _stdout is not null && _stderr is not null)
            return Task.CompletedTask;

        ResetProcess();

        var startInfo = new ProcessStartInfo
        {
            FileName = _exifToolPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-stay_open");
        startInfo.ArgumentList.Add("True");
        startInfo.ArgumentList.Add("-@");
        startInfo.ArgumentList.Add("-");
        startInfo.ArgumentList.Add("-common_args");
        startInfo.ArgumentList.Add("-charset");
        startInfo.ArgumentList.Add("filename=utf8");
        startInfo.ArgumentList.Add("-charset");
        startInfo.ArgumentList.Add("exif=utf8");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ExifTool daemon.");

        _process = process;
        _stdin = new StreamWriter(process.StandardInput.BaseStream, Utf8NoBom, bufferSize: 16384, leaveOpen: true)
        {
            NewLine = "\n",
            AutoFlush = false
        };
        _stdout = process.StandardOutput;
        _stderr = process.StandardError;
        return Task.CompletedTask;
    }

    private static async Task<(string result, bool foundMarker)> ReadUntilMarkerAsync(
        StreamReader reader,
        string marker,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                return (sb.ToString(), false);

            var trimmed = line.TrimEnd('\r');
            if (trimmed == marker)
                return (sb.ToString(), true);

            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(trimmed);
        }
    }

    private static bool ContainsFatalError(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return false;

        foreach (var line in error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void ResetProcess()
    {
        try { _stdin?.Dispose(); } catch { }
        _stdin = null;
        _stdout = null;
        _stderr = null;

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch { }
            try { _process.Dispose(); } catch { }
            _process = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (_process is { HasExited: false } && _stdin is not null)
            {
                await _stdin.WriteAsync("-stay_open\nFalse\n").ConfigureAwait(false);
                await _stdin.FlushAsync().ConfigureAwait(false);
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
                try { await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch { }
            }
        }
        catch { }

        ResetProcess();
        _gate.Dispose();
    }
}
