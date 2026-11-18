using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SoundpadCompanion.Soundpad;

/// <summary>The real Soundpad.exe, opened for reading and writing its memory.</summary>
internal sealed class SoundpadProcessMemory : ISoundpadMemory
{
    private const int Page = 4096;

    public ISoundpadProcess? Open()
    {
        var running = Process.GetProcessesByName("Soundpad");
        try
        {
            if (running.Length == 0)
                return null;
            var soundpad = running[0];
            var handle = Native.OpenProcess(
                Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE | Native.PROCESS_VM_OPERATION | Native.PROCESS_QUERY_INFORMATION,
                false, soundpad.Id);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new MicBoostException($"Windows wouldn't let this app reach Soundpad (error {error}). Is Soundpad running as administrator?");
            }
            try
            {
                var main = soundpad.MainModule ?? throw new MicBoostException("Couldn't find Soundpad.exe inside Soundpad's process.");
                return new Opened(handle, main.BaseAddress, main.ModuleMemorySize);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                handle.Dispose();
                throw new MicBoostException($"Couldn't look inside Soundpad: {e.Message}", e);
            }
        }
        finally
        {
            foreach (var p in running)
                p.Dispose();
        }
    }

    private sealed class Opened(SafeProcessHandle handle, IntPtr imageBase, int imageSize) : ISoundpadProcess
    {
        public long ImageBase => imageBase;
        public int ImageSize => imageSize;

        public byte[] Read(long address, int count)
        {
            var buffer = new byte[count];
            if (Native.ReadProcessMemory(handle, (IntPtr)address, buffer, count, out nint read) && read == count)
                return buffer;
            // Something in the range can't be read: take it a page at a time and leave those pages as zeros.
            var page = new byte[Page];
            for (int off = 0; off < count; off += Page)
            {
                int size = Math.Min(Page, count - off);
                if (Native.ReadProcessMemory(handle, (IntPtr)(address + off), page, size, out read) && read == size)
                    Array.Copy(page, 0, buffer, off, size);
                else
                    Array.Clear(buffer, off, size);
            }
            return buffer;
        }

        public void Write(long address, byte[] data)
        {
            if (!Native.WriteProcessMemory(handle, (IntPtr)address, data, data.Length, out nint written) || written != data.Length)
                throw new MicBoostException($"Couldn't change Soundpad's volume adjustment (error {Marshal.GetLastPInvokeError()}).");
        }

        public void Dispose() => handle.Dispose();
    }
}
