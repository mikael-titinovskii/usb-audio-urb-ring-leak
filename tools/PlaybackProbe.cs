// Read-only WASAPI render-loopback capture. Never opens a microphone, changes
// endpoint settings, changes the ASIO buffer, or sends audio to an output.
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ScarlettPlaybackProbe {
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class EnumeratorClass {}
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDevices {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDevice {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }
    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IClient {
        [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }
    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICapture {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpc100ns);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
    public sealed class Recorder : IDisposable {
        public volatile bool Finished;
        public volatile bool Started;
        public volatile bool StopRequested;
        public string Error;
        public string StartedLocal;
        public long OriginQpc;
        public long Frames;
        public int SampleRate;
        public int Channels;
        public int Bits;
        public int Packets;
        public int Discontinuities;
        public int TimestampErrors;
        public double Peak;
        public long NonFiniteSamples;
        public int InitialFlags;
        public long GapFrames;
        public int PositionGaps;
        public int CaptureBufferFrames;
        public double ElapsedSeconds { get { return OriginQpc == 0 ? 0 : (Stopwatch.GetTimestamp() - OriginQpc) / (double)Stopwatch.Frequency; } }
        Thread worker;
        static void Check(int hr) { Marshal.ThrowExceptionForHR(hr); }
        static void Release(object obj) { if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); }
        public void Begin(string endpoint, string directory, int seconds) {
            if (worker != null) throw new InvalidOperationException("Recorder already used.");
            if (!endpoint.StartsWith("{0.0.0.")) throw new ArgumentException("Only render endpoints may be recorded.");
            if (seconds < 1 || seconds > 600) throw new ArgumentOutOfRangeException("seconds");
            worker = new Thread(delegate() { Run(endpoint, directory, seconds); });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }
        void Run(string endpoint, string directory, int seconds) {
            IDevices devices = null; IDevice device = null; IClient client = null; ICapture capture = null;
            IntPtr format = IntPtr.Zero;
            BinaryWriter wav = null; StreamWriter packets = null;
            long dataStart = 0, dataSizeOffset = 0, factOffset = 0;
            bool audioStarted = false;
            try {
                devices = (IDevices)new EnumeratorClass();
                Check(devices.GetDevice(endpoint, out device));
                Guid iid = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"); object obj;
                Check(device.Activate(ref iid, 23, IntPtr.Zero, out obj)); client = (IClient)obj;
                Check(client.GetMixFormat(out format));
                int formatLength = 18 + (ushort)Marshal.ReadInt16(format, 16);
                byte[] fmt = new byte[formatLength]; Marshal.Copy(format, fmt, 0, fmt.Length);
                Channels = (ushort)Marshal.ReadInt16(format, 2);
                SampleRate = Marshal.ReadInt32(format, 4);
                int align = (ushort)Marshal.ReadInt16(format, 12);
                Bits = (ushort)Marshal.ReadInt16(format, 14);
                int tag = (ushort)Marshal.ReadInt16(format, 0);
                int subtag = tag == 65534 && fmt.Length >= 40 ? BitConverter.ToInt32(fmt, 24) : tag;
                bool floating = subtag == 3 && Bits == 32;
                // Shared capture's 100 ms queue is private to this observer;
                // it does not set the Focusrite/ASIO buffer or the engine period.
                Check(client.Initialize(0, 0x00020000, 1000000, 0, format, IntPtr.Zero));
                uint captureFrames; Check(client.GetBufferSize(out captureFrames)); CaptureBufferFrames = (int)captureFrames;
                iid = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
                Check(client.GetService(ref iid, out obj)); capture = (ICapture)obj;
                wav = new BinaryWriter(new FileStream(Path.Combine(directory, "playback.wav"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 131072));
                wav.Write(Encoding.ASCII.GetBytes("RIFF")); wav.Write((uint)0); wav.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                wav.Write((uint)fmt.Length); wav.Write(fmt); if ((fmt.Length & 1) != 0) wav.Write((byte)0);
                wav.Write(Encoding.ASCII.GetBytes("fact")); wav.Write((uint)4); factOffset = wav.BaseStream.Position; wav.Write((uint)0);
                wav.Write(Encoding.ASCII.GetBytes("data")); dataSizeOffset = wav.BaseStream.Position; wav.Write((uint)0); dataStart = wav.BaseStream.Position;
                packets = new StreamWriter(Path.Combine(directory, "packets.tsv"), false, Encoding.UTF8, 65536);
                packets.WriteLine("packet\tfile_frame\tframes\tdevice_frame\tqpc_100ns\tread_seconds\tflags\tgap_frames");
                byte[] buffer = new byte[Math.Max(align * (int)captureFrames, 65536)];
                ulong previousEnd = 0;
                OriginQpc = Stopwatch.GetTimestamp(); StartedLocal = DateTimeOffset.Now.ToString("o");
                Check(client.Start()); audioStarted = true; Started = true;
                while (!StopRequested && ElapsedSeconds < seconds) {
                    uint next; Check(capture.GetNextPacketSize(out next));
                    while (next > 0) {
                        IntPtr data; uint frames, flags; ulong position, qpc;
                        Check(capture.GetBuffer(out data, out frames, out flags, out position, out qpc));
                        int bytes = checked((int)frames * align);
                        try {
                            if (buffer.Length < bytes) buffer = new byte[bytes];
                            if ((flags & 2) != 0) Array.Clear(buffer, 0, bytes);
                            else Marshal.Copy(data, buffer, 0, bytes);
                        } finally { Check(capture.ReleaseBuffer(frames)); }
                        double readSeconds = ElapsedSeconds;
                        long gap = Packets == 0 ? 0 : (long)position - (long)previousEnd;
                        if (Packets == 0) InitialFlags = (int)flags;
                        else if ((flags & 1) != 0) Discontinuities++;
                        if ((flags & 4) != 0) TimestampErrors++;
                        if (gap != 0) { PositionGaps++; GapFrames += gap; }
                        packets.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0}\t{1}\t{2}\t{3}\t{4}\t{5:F6}\t{6}\t{7}", Packets, Frames, frames, position, qpc, readSeconds, flags, gap));
                        wav.Write(buffer, 0, bytes);
                        if (floating) for (int i = 0; i < bytes; i += 4) {
                            float value = BitConverter.ToSingle(buffer, i);
                            if (float.IsNaN(value) || float.IsInfinity(value)) NonFiniteSamples++;
                            else Peak = Math.Max(Peak, Math.Abs(value));
                        }
                        previousEnd = position + frames; Frames += frames; Packets++;
                        if (StopRequested) break;
                        Check(capture.GetNextPacketSize(out next));
                    }
                    Thread.Sleep(5);
                }
            } catch (Exception ex) { Error = ex.ToString(); }
            finally {
                if (audioStarted) { try { Check(client.Stop()); } catch (Exception ex) { if (Error == null) Error = ex.ToString(); } }
                if (wav != null) {
                    try {
                        long length = wav.BaseStream.Position;
                        wav.BaseStream.Position = 4; wav.Write((uint)(length - 8));
                        wav.BaseStream.Position = factOffset; wav.Write((uint)Frames);
                        wav.BaseStream.Position = dataSizeOffset; wav.Write((uint)(length - dataStart));
                    } finally { wav.Dispose(); }
                }
                if (packets != null) packets.Dispose();
                Release(capture); Release(client); Release(device); Release(devices);
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                Finished = true;
            }
        }
        public void Dispose() {
            StopRequested = true;
            if (worker != null && !worker.Join(5000)) throw new TimeoutException("Capture did not stop within five seconds.");
        }
    }
}
