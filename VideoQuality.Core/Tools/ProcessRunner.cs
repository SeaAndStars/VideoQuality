using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VideoQuality.Core.Localization;

namespace VideoQuality.Core.Tools;

/// <summary>One external program to run (ffmpeg, mkvmerge, nvidia-smi, …).</summary>
public sealed class ProcessRequest
{
    public required string FileName { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    /// <summary>Called for every standard-output line as it arrives (on a pool thread).</summary>
    public Action<string>? OnOutputLine { get; init; }

    /// <summary>Called for every standard-error line as it arrives (on a pool thread).</summary>
    public Action<string>? OnErrorLine { get; init; }

    /// <summary>Called once the process runs, with the handle used to pause, resume or read its load.</summary>
    public Action<IProcessControl>? OnStarted { get; init; }

    /// <summary>Characters of each stream kept in <see cref="ProcessResult"/>; older text is dropped first.</summary>
    public int CaptureLimit { get; init; } = 256 * 1024;
}

/// <summary>How a process ended, with the (tail of the) text it printed.</summary>
public sealed record ProcessResult(int ExitCode, string Output, string Error);

/// <summary>A running process: pause and resume it, stop it, or read how much CPU it used.</summary>
public interface IProcessControl
{
    int Id { get; }

    void Suspend();

    void Resume();

    void Kill();

    /// <summary>CPU time used so far (the last known value once the process has exited).</summary>
    TimeSpan ProcessorTime { get; }
}

/// <summary>The seam every external program goes through, so tests can substitute scripted tools.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs the process to the end. Cancelling kills it (with its children) and then throws
    /// <see cref="OperationCanceledException"/>; a program that cannot be started throws
    /// <see cref="ProcessStartException"/>.
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The program does not exist or cannot be executed.</summary>
public sealed class ProcessStartException(string fileName, Exception inner)
    : InvalidOperationException(Loc.T("process.error.start", fileName, inner.Message), inner)
{
    public string FileName { get; } = fileName;
}

/// <summary>Runs real operating-system processes.</summary>
public sealed partial class SystemProcessRunner : IProcessRunner
{
    public static SystemProcessRunner Instance { get; } = new();

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(request.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
        };

        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            // Start() only returns false when reusing a process component, which never happens here.
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ProcessStartException(request.FileName, ex);
        }

        var control = new Control(process);
        using CancellationTokenRegistration registration = cancellationToken.Register(control.Kill);
        request.OnStarted?.Invoke(control);

        var output = new TailBuffer(request.CaptureLimit);
        var error = new TailBuffer(request.CaptureLimit);
        Task outputPump = PumpAsync(process.StandardOutput, output, request.OnOutputLine);
        Task errorPump = PumpAsync(process.StandardError, error, request.OnErrorLine);

        // Cancellation kills the process, so this wait always ends.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(outputPump, errorPump).ConfigureAwait(false);
        control.Exited();
        cancellationToken.ThrowIfCancellationRequested();
        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }

    private static async Task PumpAsync(StreamReader reader, TailBuffer buffer, Action<string>? onLine)
    {
        while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
        {
            buffer.AppendLine(line);
            onLine?.Invoke(line);
        }
    }

    /// <summary>Keeps the last <c>limit</c> characters (roughly: trimmed in halves to stay cheap).</summary>
    private sealed class TailBuffer(int limit)
    {
        private readonly StringBuilder _text = new();

        public void AppendLine(string line)
        {
            lock (_text)
            {
                _text.Append(line).Append('\n');
                if (_text.Length > limit * 2)
                {
                    _text.Remove(0, _text.Length - limit);
                }
            }
        }

        public override string ToString()
        {
            lock (_text)
            {
                return _text.Length > limit ? _text.ToString(_text.Length - limit, limit) : _text.ToString();
            }
        }
    }

    internal sealed class Control(Process process) : IProcessControl
    {
        private TimeSpan _lastProcessorTime;
        private bool _exited;

        public int Id { get; } = process.Id;

        public TimeSpan ProcessorTime
        {
            get
            {
                if (!_exited)
                {
                    try
                    {
                        _lastProcessorTime = process.TotalProcessorTime;
                    }
                    catch (InvalidOperationException)
                    {
                        // Exited between the check and the read; keep the last value.
                    }
                }

                return _lastProcessorTime;
            }
        }

        public void Suspend() => Signal(suspend: true);

        public void Resume() => Signal(suspend: false);

        public void Kill()
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }

        public void Exited()
        {
            _ = ProcessorTime;
            _exited = true;
        }

        private void Signal(bool suspend)
        {
            if (_exited)
            {
                return;
            }

            // Windows: freeze or thaw every thread at once (NTSTATUS < 0 is failure).
            // POSIX: SIGSTOP / SIGCONT (-1 and errno on failure, mapped to -errno).
            int status = OperatingSystem.IsWindows()
                ? suspend ? NtSuspendProcess(process.Handle) : NtResumeProcess(process.Handle)
                : PosixStatus(kill(Id, suspend ? SigStop : SigCont), Marshal.GetLastPInvokeError());
            ThrowIfFailed(status, Id);
        }
    }

    /// <summary>kill(2)'s result as a status: 0, or the negated errno when it failed.</summary>
    internal static int PosixStatus(int result, int errno) => result == 0 ? 0 : -errno;

    internal static void ThrowIfFailed(int status, int processId)
    {
        if (status < 0)
        {
            throw new InvalidOperationException(Loc.T("process.error.signal", processId, status));
        }
    }

    private const int SigStop = 19;
    private const int SigCont = 18;

    [LibraryImport("ntdll")]
    private static partial int NtSuspendProcess(IntPtr processHandle);

    [LibraryImport("ntdll")]
    private static partial int NtResumeProcess(IntPtr processHandle);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int signal);
}
