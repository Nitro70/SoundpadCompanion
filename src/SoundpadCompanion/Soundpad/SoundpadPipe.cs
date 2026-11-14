using System.IO;
using System.IO.Pipes;
using System.Text;

namespace SoundpadCompanion.Soundpad;

/// <summary>Sends one command to Soundpad and returns its raw reply.</summary>
public interface ISoundpadTransport
{
    /// <exception cref="SoundpadOfflineException">Soundpad is not running or did not answer.</exception>
    Task<string> RequestAsync(string command, CancellationToken ct = default);
}

/// <summary>
/// Soundpad's remote control interface: the local named pipe <c>\\.\pipe\sp_remote_control</c>.
/// A command is plain UTF-8 text such as <c>DoPlaySound(5)</c>; the reply is one pipe message
/// (Soundpad 4.0.35 creates the pipe in message mode).
/// </summary>
public sealed class SoundpadPipe : ISoundpadTransport
{
    public const string PipeName = "sp_remote_control";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public async Task<string> RequestAsync(string command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A fresh connection per request. Nothing is held open between requests, so other
            // remote-control tools (Stream Deck, Touch Portal...) can still get in, and a Soundpad
            // restart never leaves us holding a dead pipe.
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
            }
            catch (TimeoutException e)
            {
                throw new SoundpadOfflineException("Soundpad isn't running.", e);
            }
            catch (IOException e)
            {
                throw new SoundpadOfflineException("Couldn't connect to Soundpad.", e);
            }

            bool messageMode = TrySetMessageMode(pipe);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReplyTimeout);
            try
            {
                byte[] request = Encoding.UTF8.GetBytes(command);
                await pipe.WriteAsync(request, timeout.Token).ConfigureAwait(false);
                return await ReadReplyAsync(pipe, messageMode, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SoundpadOfflineException($"Soundpad didn't answer within {ReplyTimeout.TotalSeconds:0} s.");
            }
            catch (IOException e)
            {
                throw new SoundpadOfflineException("Lost the connection to Soundpad.", e);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool TrySetMessageMode(NamedPipeClientStream pipe)
    {
        try
        {
            pipe.ReadMode = PipeTransmissionMode.Message;
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            // A byte-mode pipe has no message boundaries; fall back to a single read below.
            return false;
        }
    }

    private static async Task<string> ReadReplyAsync(NamedPipeClientStream pipe, bool messageMode, CancellationToken ct)
    {
        // The full sound list is about 100 KB for a few hundred sounds; message mode hands it over in as many reads as it takes.
        var buffer = new byte[64 * 1024];
        using var reply = new MemoryStream();
        while (true)
        {
            int read = await pipe.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                throw new IOException("Soundpad closed the connection before replying.");
            reply.Write(buffer, 0, read);
            if (!messageMode || pipe.IsMessageComplete)
                break;
        }
        return Encoding.UTF8.GetString(reply.GetBuffer(), 0, (int)reply.Length).TrimEnd('\0');
    }
}
