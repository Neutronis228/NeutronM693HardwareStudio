using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NeutronM693DirectHID
{
    internal enum ClickAction { None = 0, Left = 1, Right = 2 }

    internal sealed class TimingSnapshot
    {
        public int Min, Max, Center, Smooth;
        public TimingSnapshot(int min, int max, int center, int smooth)
        {
            Min = min; Max = max; Center = center; Smooth = smooth;
        }
    }

    internal sealed class SmoothDelayGenerator
    {
        private readonly Random _random;
        private double _current;
        private bool _initialized;

        public SmoothDelayGenerator(int seed) { _random = new Random(seed); }

        private double NextGaussian()
        {
            double u1 = 1.0 - _random.NextDouble();
            double u2 = 1.0 - _random.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public int Next(TimingSnapshot s)
        {
            if (s.Max < s.Min) { int t = s.Min; s.Min = s.Max; s.Max = t; }
            s.Center = Math.Max(s.Min, Math.Min(s.Max, s.Center));
            s.Smooth = Math.Max(0, Math.Min(99, s.Smooth));

            if (!_initialized) { _current = s.Center; _initialized = true; }

            double sigma = Math.Max(2.0, (s.Max - s.Min) / 4.0);
            double candidate = Clamp(s.Center + NextGaussian() * sigma, s.Min, s.Max);
            double keep = s.Smooth / 100.0;
            double next = _current * keep + candidate * (1.0 - keep);
            double maxStep = Math.Max(2.0, (s.Max - s.Min) * 0.06);
            double delta = next - _current;
            if (delta > maxStep) delta = maxStep;
            if (delta < -maxStep) delta = -maxStep;
            _current = Clamp(_current + delta, s.Min, s.Max);
            return (int)Math.Round(_current);
        }
    }

    internal static class MouseInjector
    {
        public const uint MAGIC = 0x4E693933; // "Ni93" marker for our own SendInput events
        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public MOUSEINPUT mi; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public UIntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public static void Click(ClickAction action)
        {
            uint down, up;
            if (action == ClickAction.Left) { down = MOUSEEVENTF_LEFTDOWN; up = MOUSEEVENTF_LEFTUP; }
            else if (action == ClickAction.Right) { down = MOUSEEVENTF_RIGHTDOWN; up = MOUSEEVENTF_RIGHTUP; }
            else return;

            INPUT[] i = new INPUT[2];
            i[0].type = INPUT_MOUSE; i[0].mi.dwFlags = down; i[0].mi.dwExtraInfo = new UIntPtr(MAGIC);
            i[1].type = INPUT_MOUSE; i[1].mi.dwFlags = up;   i[1].mi.dwExtraInfo = new UIntPtr(MAGIC);
            SendInput(2, i, Marshal.SizeOf(typeof(INPUT)));
        }
    }

    internal sealed class ClickWorker : IDisposable
    {
        private readonly ClickAction _action;
        private readonly Func<TimingSnapshot> _settingsProvider;
        private readonly Action<int> _delayReported;
        private readonly ManualResetEvent _release = new ManualResetEvent(false);
        private readonly SmoothDelayGenerator _generator;
        private volatile bool _held;
        private Thread _thread;

        public ClickWorker(ClickAction action, Func<TimingSnapshot> settingsProvider, Action<int> delayReported, int seed)
        {
            _action = action; _settingsProvider = settingsProvider; _delayReported = delayReported;
            _generator = new SmoothDelayGenerator(seed);
        }

        public void StartHold()
        {
            if (_held) return;
            _held = true; _release.Reset();
            _thread = new Thread(Loop); _thread.IsBackground = true; _thread.Name = "M693-" + _action;
            _thread.Start();
        }

        public void StopHold() { _held = false; _release.Set(); }

        private void Loop()
        {
            while (_held)
            {
                MouseInjector.Click(_action);
                int d = _generator.Next(_settingsProvider());
                if (_delayReported != null) _delayReported(d);
                if (_release.WaitOne(d)) break;
            }
        }

        public void Dispose()
        {
            StopHold();
            try { if (_thread != null && _thread.IsAlive) _thread.Join(250); } catch { }
            _release.Close();
        }
    }

    internal sealed class HidFingerprint
    {
        public uint VendorId, ProductId;
        public ushort UsagePage, Usage;

        public string Key { get { return VendorId.ToString("X4") + ":" + ProductId.ToString("X4") + ":" + UsagePage.ToString("X4") + ":" + Usage.ToString("X4"); } }
        public override string ToString() { return "VID_" + VendorId.ToString("X4") + " PID_" + ProductId.ToString("X4") + " UP_" + UsagePage.ToString("X4") + " U_" + Usage.ToString("X4"); }
    }

    internal sealed class HidDeviceInfo
    {
        public IntPtr Handle;
        public HidFingerprint Fp;
    }

    internal sealed class DirectBinding
    {
        public HidFingerprint Fp = new HidFingerprint();
        public int ByteIndex;
        public byte Mask;
        public bool ActiveBit;
        public bool HasTag;
        public byte TagValue;
        public bool CurrentActive;

        public string Serialize()
        {
            return Fp.VendorId + "," + Fp.ProductId + "," + Fp.UsagePage + "," + Fp.Usage + "," + ByteIndex + "," + Mask + "," + (ActiveBit ? 1 : 0) + "," + (HasTag ? 1 : 0) + "," + TagValue;
        }

        public static DirectBinding Parse(string s)
        {
            if (String.IsNullOrEmpty(s)) return null;
            string[] p = s.Split(','); if (p.Length < 9) return null;
            DirectBinding b = new DirectBinding();
            uint v, pr; ushort up, u; int bi, m, act, ht, tv;
            if (!UInt32.TryParse(p[0], out v) || !UInt32.TryParse(p[1], out pr) || !UInt16.TryParse(p[2], out up) || !UInt16.TryParse(p[3], out u)) return null;
            if (!Int32.TryParse(p[4], out bi) || !Int32.TryParse(p[5], out m) || !Int32.TryParse(p[6], out act) || !Int32.TryParse(p[7], out ht) || !Int32.TryParse(p[8], out tv)) return null;
            b.Fp.VendorId = v; b.Fp.ProductId = pr; b.Fp.UsagePage = up; b.Fp.Usage = u;
            b.ByteIndex = bi; b.Mask = (byte)m; b.ActiveBit = act != 0; b.HasTag = ht != 0; b.TagValue = (byte)tv;
            return b;
        }

        public string Describe()
        {
            if (Fp == null || Mask == 0) return "не навчено";
            return Fp + "  byte=" + ByteIndex + " mask=0x" + Mask.ToString("X2") + " active=" + (ActiveBit ? "1" : "0") + (HasTag ? " tag0=0x" + TagValue.ToString("X2") : "");
        }
    }

    internal sealed class LearnCandidate
    {
        public string Key;
        public HidDeviceInfo Device;
        public byte[] First;
        public DateTime FirstAt;
    }

    internal sealed class LearnSession
    {
        public int Button;
        public DateTime Started;
        public Dictionary<string, LearnCandidate> Candidates = new Dictionary<string, LearnCandidate>();
    }

    internal sealed class RawMouseDeviceInfo
    {
        public IntPtr Handle;
        public string DeviceName;
        public string Signature;

        public override string ToString()
        {
            return String.IsNullOrEmpty(Signature) ? DeviceName : Signature;
        }
    }

    internal sealed class MouseDirectBinding
    {
        public string Signature;
        public int XButton; // 1 = XBUTTON1, 2 = XBUTTON2
        public bool CurrentActive;

        public string Serialize()
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Signature ?? ""));
            return encoded + "," + XButton;
        }

        public static MouseDirectBinding Parse(string s)
        {
            if (String.IsNullOrEmpty(s)) return null;
            int c = s.LastIndexOf(',');
            if (c <= 0) return null;
            try
            {
                MouseDirectBinding b = new MouseDirectBinding();
                b.Signature = Encoding.UTF8.GetString(Convert.FromBase64String(s.Substring(0, c)));
                int x;
                if (!Int32.TryParse(s.Substring(c + 1), out x)) return null;
                if (x != 1 && x != 2) return null;
                b.XButton = x;
                return b;
            }
            catch { return null; }
        }

        public string Describe()
        {
            if (String.IsNullOrEmpty(Signature) || (XButton != 1 && XButton != 2)) return "не навчено";
            return "RAW MOUSE " + Signature + " / XBUTTON" + XButton;
        }
    }

    internal sealed class RawHidManager
    {
        public const int WM_INPUT = 0x00FF;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIDI_DEVICENAME = 0x20000007;
        private const uint RIDI_DEVICEINFO = 0x2000000b;
        private const uint RIM_TYPEMOUSE = 0;
        private const uint RIM_TYPEHID = 2;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RIDEV_DEVNOTIFY = 0x00002000;

        public const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
        public const ushort RI_MOUSE_LEFT_BUTTON_UP   = 0x0002;
        public const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
        public const ushort RI_MOUSE_RIGHT_BUTTON_UP   = 0x0008;
        public const ushort RI_MOUSE_BUTTON_4_DOWN = 0x0040;
        public const ushort RI_MOUSE_BUTTON_4_UP   = 0x0080;
        public const ushort RI_MOUSE_BUTTON_5_DOWN = 0x0100;
        public const ushort RI_MOUSE_BUTTON_5_UP   = 0x0200;

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }
        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
        [StructLayout(LayoutKind.Sequential)]
        private struct RID_DEVICE_INFO_HID { public uint dwVendorId, dwProductId, dwVersionNumber; public ushort usUsagePage, usUsage; }
        [StructLayout(LayoutKind.Explicit)]
        private struct RID_DEVICE_INFO_UNION { [FieldOffset(0)] public RID_DEVICE_INFO_HID hid; }
        [StructLayout(LayoutKind.Sequential)]
        private struct RID_DEVICE_INFO { public uint cbSize, dwType; public RID_DEVICE_INFO_UNION data; }
        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWMOUSE
        {
            public ushort usFlags;
            public uint ulButtons;
            public uint ulRawButtons;
            public int lLastX;
            public int lLastY;
            public uint ulExtraInformation;

            public ushort ButtonFlags { get { return (ushort)(ulButtons & 0xFFFF); } }
            public ushort ButtonData  { get { return (ushort)((ulButtons >> 16) & 0xFFFF); } }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceList([In, Out] RAWINPUTDEVICELIST[] pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, ref RID_DEVICE_INFO pData, ref uint pcbSize);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, StringBuilder pData, ref uint pcbSize);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        private readonly Dictionary<IntPtr, HidDeviceInfo> _hidDevices = new Dictionary<IntPtr, HidDeviceInfo>();
        private readonly Dictionary<IntPtr, RawMouseDeviceInfo> _mouseDevices = new Dictionary<IntPtr, RawMouseDeviceInfo>();
        private readonly List<string> _diag = new List<string>();
        private IntPtr _hwnd;

        public event Action<HidDeviceInfo, byte[]> Report;
        public event Action<RawMouseDeviceInfo, ushort> MouseButtons;

        public int DeviceCount { get { return _hidDevices.Count; } }
        public int MouseDeviceCount { get { return _mouseDevices.Count; } }

        public string DeviceSummary()
        {
            Dictionary<string, bool> uniq = new Dictionary<string, bool>();
            foreach (HidDeviceInfo d in _hidDevices.Values) uniq[d.Fp.Key] = true;
            StringBuilder sb = new StringBuilder();
            foreach (string k in uniq.Keys) { if (sb.Length > 0) sb.Append(" | "); sb.Append(k); }
            return sb.ToString();
        }

        public string MouseDeviceSummary()
        {
            Dictionary<string, bool> uniq = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (RawMouseDeviceInfo d in _mouseDevices.Values)
            {
                string k = String.IsNullOrEmpty(d.Signature) ? d.DeviceName : d.Signature;
                if (!String.IsNullOrEmpty(k)) uniq[k] = true;
            }
            StringBuilder sb = new StringBuilder();
            foreach (string k in uniq.Keys) { if (sb.Length > 0) sb.Append(" | "); sb.Append(k); }
            return sb.ToString();
        }

        private static string GetDeviceName(IntPtr hDevice)
        {
            uint chars = 0;
            try
            {
                GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, (StringBuilder)null, ref chars);
                if (chars == 0) return "";
                StringBuilder sb = new StringBuilder((int)chars + 2);
                uint c2 = (uint)sb.Capacity;
                uint r = GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, sb, ref c2);
                if (r == UInt32.MaxValue) return "";
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static string MakeSignature(string deviceName)
        {
            if (String.IsNullOrEmpty(deviceName)) return "";
            string u = deviceName.ToUpperInvariant();
            int vi = u.IndexOf("VID_");
            int pi = u.IndexOf("PID_");
            if (vi >= 0 && vi + 8 <= u.Length && pi >= 0 && pi + 8 <= u.Length)
            {
                string sig = u.Substring(vi, 8) + "&" + u.Substring(pi, 8);
                int mi = u.IndexOf("MI_");
                if (mi >= 0 && mi + 5 <= u.Length) sig += "&" + u.Substring(mi, 5);
                int col = u.IndexOf("COL");
                if (col >= 0 && col + 5 <= u.Length) sig += "&" + u.Substring(col, 5);
                return sig;
            }
            return u;
        }

        public void Refresh(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _hidDevices.Clear();
            _mouseDevices.Clear();

            uint count = 0;
            uint dlSize = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
            GetRawInputDeviceList(null, ref count, dlSize);
            if (count == 0)
            {
                AddDiag("REFRESH: GetRawInputDeviceList returned 0 devices");
                return;
            }

            RAWINPUTDEVICELIST[] list = new RAWINPUTDEVICELIST[count];
            uint actual = GetRawInputDeviceList(list, ref count, dlSize);
            if (actual == UInt32.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());

            Dictionary<string, RAWINPUTDEVICE> regs = new Dictionary<string, RAWINPUTDEVICE>();

            for (int i = 0; i < actual; i++)
            {
                if (list[i].dwType == RIM_TYPEMOUSE)
                {
                    RawMouseDeviceInfo m = new RawMouseDeviceInfo();
                    m.Handle = list[i].hDevice;
                    m.DeviceName = GetDeviceName(list[i].hDevice);
                    m.Signature = MakeSignature(m.DeviceName);
                    _mouseDevices[m.Handle] = m;

                    string rk = "1:2";
                    if (!regs.ContainsKey(rk))
                    {
                        RAWINPUTDEVICE r = new RAWINPUTDEVICE();
                        r.usUsagePage = 0x01; // Generic Desktop
                        r.usUsage = 0x02;     // Mouse
                        r.dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY;
                        r.hwndTarget = hwnd;
                        regs[rk] = r;
                    }
                    AddDiag("MOUSE: " + m.Signature + " path=" + m.DeviceName);
                    continue;
                }

                if (list[i].dwType != RIM_TYPEHID) continue;

                RID_DEVICE_INFO info = new RID_DEVICE_INFO();
                info.cbSize = (uint)Marshal.SizeOf(typeof(RID_DEVICE_INFO));
                uint cb = info.cbSize;
                if (GetRawInputDeviceInfo(list[i].hDevice, RIDI_DEVICEINFO, ref info, ref cb) == UInt32.MaxValue) continue;

                HidDeviceInfo d = new HidDeviceInfo();
                d.Handle = list[i].hDevice;
                d.Fp = new HidFingerprint();
                d.Fp.VendorId = info.data.hid.dwVendorId;
                d.Fp.ProductId = info.data.hid.dwProductId;
                d.Fp.UsagePage = info.data.hid.usUsagePage;
                d.Fp.Usage = info.data.hid.usUsage;
                _hidDevices[d.Handle] = d;

                string hk = d.Fp.UsagePage + ":" + d.Fp.Usage;
                if (!regs.ContainsKey(hk))
                {
                    RAWINPUTDEVICE r = new RAWINPUTDEVICE();
                    r.usUsagePage = d.Fp.UsagePage;
                    r.usUsage = d.Fp.Usage;
                    r.dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY;
                    r.hwndTarget = hwnd;
                    regs[hk] = r;
                }
            }

            if (regs.Count > 0)
            {
                RAWINPUTDEVICE[] arr = new RAWINPUTDEVICE[regs.Count];
                regs.Values.CopyTo(arr, 0);
                if (!RegisterRawInputDevices(arr, (uint)arr.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterRawInputDevices failed");
            }

            AddDiag("REFRESH: raw mice=" + _mouseDevices.Count + "; other HID collections=" + _hidDevices.Count);
            AddDiag("RAW MICE: " + MouseDeviceSummary());
            AddDiag("OTHER HID: " + DeviceSummary());
        }

        public void ProcessInput(IntPtr lParam)
        {
            uint size = 0;
            uint hs = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
            if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, hs) != 0 || size == 0) return;

            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                uint got = GetRawInputData(lParam, RID_INPUT, buf, ref size, hs);
                if (got == UInt32.MaxValue || got == 0) return;

                RAWINPUTHEADER h = (RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(RAWINPUTHEADER));
                IntPtr p = IntPtr.Add(buf, (int)hs);

                if (h.dwType == RIM_TYPEMOUSE)
                {
                    RawMouseDeviceInfo md;
                    if (!_mouseDevices.TryGetValue(h.hDevice, out md))
                    {
                        md = new RawMouseDeviceInfo();
                        md.Handle = h.hDevice;
                        md.DeviceName = GetDeviceName(h.hDevice);
                        md.Signature = MakeSignature(md.DeviceName);
                        _mouseDevices[h.hDevice] = md;
                    }

                    RAWMOUSE rm = (RAWMOUSE)Marshal.PtrToStructure(p, typeof(RAWMOUSE));
                    ushort flags = rm.ButtonFlags;
                    if (flags != 0)
                    {
                        AddDiag(DateTime.Now.ToString("HH:mm:ss.fff") + " RAWMOUSE " + md.Signature +
                            " flags=0x" + flags.ToString("X4") + " raw=0x" + rm.ulRawButtons.ToString("X8"));
                        Action<RawMouseDeviceInfo, ushort> mev = MouseButtons;
                        if (mev != null) mev(md, flags);
                    }
                    return;
                }

                if (h.dwType != RIM_TYPEHID) return;

                HidDeviceInfo d;
                if (!_hidDevices.TryGetValue(h.hDevice, out d)) return;

                int reportSize = Marshal.ReadInt32(p, 0);
                int reportCount = Marshal.ReadInt32(p, 4);
                if (reportSize <= 0 || reportSize > 4096 || reportCount <= 0 || reportCount > 128) return;

                int pos = 8;
                for (int n = 0; n < reportCount; n++)
                {
                    byte[] data = new byte[reportSize];
                    Marshal.Copy(IntPtr.Add(p, pos), data, 0, reportSize);
                    pos += reportSize;
                    AddDiag(DateTime.Now.ToString("HH:mm:ss.fff") + " HID " + d.Fp.Key + " " + Hex(data));
                    Action<HidDeviceInfo, byte[]> ev = Report;
                    if (ev != null) ev(d, data);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder();
            int max = Math.Min(b.Length, 64);
            for (int i = 0; i < max; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(b[i].ToString("X2"));
            }
            if (b.Length > max) sb.Append(" ...");
            return sb.ToString();
        }

        private void AddDiag(string s)
        {
            _diag.Add(s);
            if (_diag.Count > 1200) _diag.RemoveRange(0, 200);
        }

        public string GetDiagnostic()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Neutron M693 Direct HID / RAW MOUSE diagnostic");
            sb.AppendLine("Generated: " + DateTime.Now);
            sb.AppendLine("Raw mouse devices: " + _mouseDevices.Count);
            sb.AppendLine("Raw mice: " + MouseDeviceSummary());
            sb.AppendLine("Other HID collections: " + _hidDevices.Count);
            sb.AppendLine("Other HID: " + DeviceSummary());
            sb.AppendLine();
            sb.AppendLine("IMPORTANT:");
            sb.AppendLine("XBUTTON flags: 0x0040/0x0080 = XBUTTON1 down/up; 0x0100/0x0200 = XBUTTON2 down/up.");
            sb.AppendLine("If pressing physical side key while an onboard macro is assigned produces ONLY 0x0001/0x0002 (left) or 0x0004/0x0008 (right),");
            sb.AppendLine("the mouse firmware is sending only the macro result to Windows. In that case the physical key identity is not present in Raw Input.");
            sb.AppendLine();
            foreach (string x in _diag) sb.AppendLine(x);
            return sb.ToString();
        }
    }

    internal sealed class MacroClickFilter : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public UIntPtr dwExtraInfo; }
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern IntPtr GetModuleHandle(string name);

        private readonly LowLevelMouseProc _proc; private IntPtr _hook;
        public Func<bool> SuppressLeft; public Func<bool> SuppressRight;
        public MacroClickFilter() { _proc = Callback; }
        public void Install()
        {
            if (_hook != IntPtr.Zero) return;
            using (Process p = Process.GetCurrentProcess()) using (ProcessModule m = p.MainModule)
                _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(m.ModuleName), 0);
            if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "mouse filter hook failed");
        }
        private IntPtr Callback(int code, IntPtr wp, IntPtr lp)
        {
            if (code >= 0)
            {
                int msg = wp.ToInt32();
                MSLLHOOKSTRUCT d = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lp, typeof(MSLLHOOKSTRUCT));
                if (d.dwExtraInfo.ToUInt64() != MouseInjector.MAGIC)
                {
                    if ((msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP) && SuppressLeft != null && SuppressLeft()) return new IntPtr(1);
                    if ((msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP) && SuppressRight != null && SuppressRight()) return new IntPtr(1);
                }
            }
            return CallNextHookEx(_hook, code, wp, lp);
        }
        public void Dispose() { if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; } }
    }

    internal sealed class MainForm : Form
    {
        private readonly RawHidManager _hid = new RawHidManager();
        private ClickWorker _leftWorker, _rightWorker;
        private DirectBinding _b4, _b5;
        private MouseDirectBinding _mb4, _mb5;
        private LearnSession _learn;
        private readonly Dictionary<string, byte[]> _lastReports = new Dictionary<string, byte[]>();
        private readonly string _settingsFile;

        private CheckBox _enabled;
        private ComboBox _a4, _a5;
        private NumericUpDown _min, _center, _max;
        private TrackBar _smooth;
        private Label _smoothLabel, _hidStatus, _b4Label, _b5Label, _learnStatus, _leftStatus, _rightStatus;
        private Button _learn4, _learn5, _refresh, _save, _diag;
        private NotifyIcon _tray;
        private bool _reallyClose;

        private volatile bool _button4Held, _button5Held;
        private volatile bool _enabledValue = true;
        private volatile bool _learnSawLogicalMacro;
        private volatile int _minValue = 10, _centerValue = 45, _maxValue = 150, _smoothValue = 88;
        private volatile ClickAction _action4 = ClickAction.Left, _action5 = ClickAction.Right;
        private volatile int _lastLeftDelay, _lastRightDelay;

        public MainForm()
        {
            Text = "Neutron M693 Direct HID 1.2.0";
            StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(760, 600); MinimumSize = new Size(776, 639);
            Font = new Font("Segoe UI", 9F); Icon = SystemIcons.Application;
            _settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NeutronM693DirectHID", "settings.ini");
            LoadSettings(); BuildUi(); ApplyToUi(); WireUi();

            _leftWorker = new ClickWorker(ClickAction.Left, Timing, delegate(int d) { _lastLeftDelay = d; }, Environment.TickCount ^ 0x5112);
            _rightWorker = new ClickWorker(ClickAction.Right, Timing, delegate(int d) { _lastRightDelay = d; }, Environment.TickCount ^ 0xA173);
            _hid.Report += OnHidReport;
            _hid.MouseButtons += OnRawMouseButtons;

            try { _hid.Refresh(Handle); UpdateHidStatus(); }
            catch (Exception ex) { _hidStatus.Text = "RAW/HID: помилка — " + ex.Message; }

            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer(); t.Interval = 200;
            t.Tick += delegate
            {
                _leftStatus.Text = "Ліва: " + (_button4Held || _button5Held ? (_lastLeftDelay > 0 ? _lastLeftDelay + " мс" : "активна") : "очікує");
                _rightStatus.Text = "Права: " + (_button4Held || _button5Held ? (_lastRightDelay > 0 ? _lastRightDelay + " мс" : "активна") : "очікує");
                if (_learn != null && (DateTime.Now - _learn.Started).TotalSeconds > 10)
                {
                    if (_learnSawLogicalMacro)
                        EndLearn("Тайм-аут: Windows бачила лише LEFT/RIGHT від поточного макросу, але не окремий XBUTTON/HID-код фізичної бокової кнопки. Збережіть RAW діагностику — це покаже, чи треба йти через vendor HID протокол M693.");
                    else
                        EndLearn("Тайм-аут: окремого сигналу фізичної кнопки не знайдено. Натисніть «RAW/HID діагностика», поклацайте 4 і 5 та збережіть файл.");
                }
            };
            t.Start();

            FormClosing += OnClosing;
            Resize += delegate { if (WindowState == FormWindowState.Minimized) { Hide(); _tray.Visible = true; } };
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == RawHidManager.WM_INPUT) _hid.ProcessInput(m.LParam);
            base.WndProc(ref m);
        }

        private void BuildUi()
        {
            Label title = NewLabel("Redragon TRIDENT M693 — Direct RAW/HID прив’язування фізичних кнопок", 18, 14, true); Controls.Add(title);
            Label desc = NewLabel("Профіль Redragon не змінюється. FIX2 бачить стандартний RAW MOUSE та додаткові HID-колекції, а не лише RIM_TYPEHID.", 18, 40, false); Controls.Add(desc);

            _enabled = new CheckBox(); _enabled.Text = "Увімкнути"; _enabled.AutoSize = true; _enabled.Location = new Point(20, 72); Controls.Add(_enabled);

            GroupBox direct = new GroupBox(); direct.Text = "Direct RAW/HID — фізичні бокові кнопки"; direct.Location = new Point(18, 104); direct.Size = new Size(724, 205); Controls.Add(direct);
            _hidStatus = NewLabel("RAW/HID: сканування...", 14, 26, false); _hidStatus.MaximumSize = new Size(530, 50); direct.Controls.Add(_hidStatus);
            _refresh = new Button(); _refresh.Text = "Пересканувати RAW/HID"; _refresh.Location = new Point(542, 20); _refresh.Size = new Size(166, 30); direct.Controls.Add(_refresh);

            Label l4 = NewLabel("Кнопка 4:", 14, 72, true); direct.Controls.Add(l4);
            _a4 = ActionCombo(); _a4.Location = new Point(88, 68); _a4.Width = 120; direct.Controls.Add(_a4);
            _learn4 = new Button(); _learn4.Text = "Навчити фізичну 4"; _learn4.Location = new Point(220, 66); _learn4.Size = new Size(140, 30); direct.Controls.Add(_learn4);
            _b4Label = NewLabel("не навчено", 374, 72, false); _b4Label.MaximumSize = new Size(330, 38); direct.Controls.Add(_b4Label);

            Label l5 = NewLabel("Кнопка 5:", 14, 116, true); direct.Controls.Add(l5);
            _a5 = ActionCombo(); _a5.Location = new Point(88, 112); _a5.Width = 120; direct.Controls.Add(_a5);
            _learn5 = new Button(); _learn5.Text = "Навчити фізичну 5"; _learn5.Location = new Point(220, 110); _learn5.Size = new Size(140, 30); direct.Controls.Add(_learn5);
            _b5Label = NewLabel("не навчено", 374, 116, false); _b5Label.MaximumSize = new Size(330, 38); direct.Controls.Add(_b5Label);

            _learnStatus = NewLabel("Навчання: натисніть кнопку «Навчити», потім потримайте потрібну бокову кнопку ~1 секунду і відпустіть.", 14, 158, false); _learnStatus.MaximumSize = new Size(690, 40); direct.Controls.Add(_learnStatus);

            GroupBox timing = new GroupBox(); timing.Text = "Плавний інтервал"; timing.Location = new Point(18, 320); timing.Size = new Size(724, 150); Controls.Add(timing);
            AddNumeric(timing, "Мінімум, мс", 18, 30, out _min); AddNumeric(timing, "Типово, мс", 205, 30, out _center); AddNumeric(timing, "Максимум, мс", 392, 30, out _max);
            Label sl = NewLabel("Плавність:", 18, 98, false); timing.Controls.Add(sl);
            _smooth = new TrackBar(); _smooth.Minimum = 0; _smooth.Maximum = 99; _smooth.TickFrequency = 10; _smooth.Location = new Point(90, 86); _smooth.Width = 480; timing.Controls.Add(_smooth);
            _smoothLabel = NewLabel("", 585, 98, false); timing.Controls.Add(_smoothLabel);

            _leftStatus = NewLabel("Ліва: очікує", 20, 486, false); Controls.Add(_leftStatus);
            _rightStatus = NewLabel("Права: очікує", 160, 486, false); Controls.Add(_rightStatus);
            _save = new Button(); _save.Text = "Зберегти"; _save.Location = new Point(536, 480); _save.Size = new Size(96, 32); Controls.Add(_save);
            _diag = new Button(); _diag.Text = "RAW/HID діагностика"; _diag.Location = new Point(638, 480); _diag.Size = new Size(104, 32); Controls.Add(_diag);

            Label footer = NewLabel("FIX2 нічого не перехоплює і не змінює профіль мишки. Якщо M693 при макросі віддає Windows тільки LEFT/RIGHT, а не фізичний XBUTTON/HID-сигнал — це буде прямо видно у діагностиці.", 18, 530, false); footer.MaximumSize = new Size(720, 48); Controls.Add(footer);

            _tray = new NotifyIcon(); _tray.Icon = SystemIcons.Application; _tray.Text = "Neutron M693 Direct HID 1.2"; _tray.Visible = false; _tray.DoubleClick += delegate { ShowFromTray(); };
            ContextMenuStrip cm = new ContextMenuStrip(); ToolStripMenuItem op = new ToolStripMenuItem("Відкрити"); op.Click += delegate { ShowFromTray(); }; cm.Items.Add(op);
            ToolStripMenuItem ex = new ToolStripMenuItem("Вийти"); ex.Click += delegate { _reallyClose = true; Close(); }; cm.Items.Add(ex); _tray.ContextMenuStrip = cm;
        }

        private static Label NewLabel(string s, int x, int y, bool bold)
        {
            Label l = new Label(); l.Text = s; l.AutoSize = true; l.Location = new Point(x, y); if (bold) l.Font = new Font("Segoe UI", 9F, FontStyle.Bold); return l;
        }
        private static ComboBox ActionCombo()
        {
            ComboBox c = new ComboBox(); c.DropDownStyle = ComboBoxStyle.DropDownList; c.Items.AddRange(new object[] { "Вимкнено", "Ліва кнопка", "Права кнопка" }); return c;
        }
        private static void AddNumeric(Control p, string text, int x, int y, out NumericUpDown n)
        {
            Label l = NewLabel(text, x, y, false); p.Controls.Add(l); n = new NumericUpDown(); n.Minimum = 1; n.Maximum = 1000; n.Location = new Point(x, y + 25); n.Width = 110; p.Controls.Add(n);
        }

        private void WireUi()
        {
            _enabled.CheckedChanged += delegate { _enabledValue = _enabled.Checked; if (!_enabledValue) StopAll(); };
            _a4.SelectedIndexChanged += delegate { _action4 = IndexAction(_a4.SelectedIndex); };
            _a5.SelectedIndexChanged += delegate { _action5 = IndexAction(_a5.SelectedIndex); };
            _min.ValueChanged += delegate { TimingUi(); }; _center.ValueChanged += delegate { TimingUi(); }; _max.ValueChanged += delegate { TimingUi(); }; _smooth.ValueChanged += delegate { TimingUi(); };
            _learn4.Click += delegate { BeginLearn(4); }; _learn5.Click += delegate { BeginLearn(5); };
            _refresh.Click += delegate { try { _hid.Refresh(Handle); UpdateHidStatus(); } catch (Exception ex) { _hidStatus.Text = "RAW/HID: " + ex.Message; } };
            _save.Click += delegate { SaveSettings(); _save.Text = "Збережено"; System.Windows.Forms.Timer t = new System.Windows.Forms.Timer(); t.Interval = 800; t.Tick += delegate { t.Stop(); t.Dispose(); if (!_save.IsDisposed) _save.Text = "Зберегти"; }; t.Start(); };
            _diag.Click += delegate
            {
                SaveFileDialog s = new SaveFileDialog(); s.Filter = "Text file|*.txt"; s.FileName = "M693_RAW_HID_Diagnostic_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                if (s.ShowDialog(this) == DialogResult.OK) File.WriteAllText(s.FileName, _hid.GetDiagnostic(), Encoding.UTF8);
            };
        }

        private void ApplyToUi()
        {
            _enabled.Checked = _enabledValue; _a4.SelectedIndex = ActionIndex(_action4); _a5.SelectedIndex = ActionIndex(_action5);
            _min.Value = Clamp(_minValue, _min.Minimum, _min.Maximum); _center.Value = Clamp(_centerValue, _center.Minimum, _center.Maximum); _max.Value = Clamp(_maxValue, _max.Minimum, _max.Maximum);
            _smooth.Value = Math.Max(_smooth.Minimum, Math.Min(_smooth.Maximum, _smoothValue)); _smoothLabel.Text = _smooth.Value + "%"; UpdateBindingLabels();
        }
        private static decimal Clamp(int v, decimal min, decimal max) { decimal d = v; if (d < min) return min; if (d > max) return max; return d; }
        private static ClickAction IndexAction(int i) { return i == 1 ? ClickAction.Left : i == 2 ? ClickAction.Right : ClickAction.None; }
        private static int ActionIndex(ClickAction a) { return a == ClickAction.Left ? 1 : a == ClickAction.Right ? 2 : 0; }
        private void TimingUi() { _minValue = (int)_min.Value; _centerValue = (int)_center.Value; _maxValue = (int)_max.Value; _smoothValue = _smooth.Value; _smoothLabel.Text = _smoothValue + "%"; }
        private TimingSnapshot Timing() { return new TimingSnapshot(_minValue, _maxValue, _centerValue, _smoothValue); }

        private void UpdateHidStatus()
        {
            _hidStatus.Text = "RAW/HID: мишей=" + _hid.MouseDeviceCount + ", додаткових HID-колекцій=" + _hid.DeviceCount +
                ". " + (_hid.MouseDeviceCount > 0 || _hid.DeviceCount > 0 ? "Готово до навчання." : "Windows Raw Input не повернув жодної миші/HID.");
        }

        private void UpdateBindingLabels()
        {
            _b4Label.Text = _mb4 != null ? _mb4.Describe() : (_b4 != null ? _b4.Describe() : "не навчено");
            _b5Label.Text = _mb5 != null ? _mb5.Describe() : (_b5 != null ? _b5.Describe() : "не навчено");
        }

        private void BeginLearn(int button)
        {
            StopAll();
            _learnSawLogicalMacro = false;
            if (button == 4) { _mb4 = null; _b4 = null; }
            else { _mb5 = null; _b5 = null; }
            UpdateBindingLabels();

            _learn = new LearnSession(); _learn.Button = button; _learn.Started = DateTime.Now;
            _learnStatus.Text = "НАВЧАННЯ кнопки " + button + ": натисніть і ТРИМАЙТЕ саме фізичну бокову кнопку ~1 секунду, потім відпустіть. FIX2 шукає XBUTTON або окремий HID-біт, а не LEFT/RIGHT макрос.";
        }
        private void EndLearn(string text) { _learn = null; _learnStatus.Text = text; }

        private void OnRawMouseButtons(RawMouseDeviceInfo dev, ushort flags)
        {
            if (_learn != null)
            {
                if ((flags & (RawHidManager.RI_MOUSE_LEFT_BUTTON_DOWN | RawHidManager.RI_MOUSE_LEFT_BUTTON_UP |
                              RawHidManager.RI_MOUSE_RIGHT_BUTTON_DOWN | RawHidManager.RI_MOUSE_RIGHT_BUTTON_UP)) != 0)
                    _learnSawLogicalMacro = true;

                int xb = 0;
                if ((flags & RawHidManager.RI_MOUSE_BUTTON_4_DOWN) != 0) xb = 1;
                else if ((flags & RawHidManager.RI_MOUSE_BUTTON_5_DOWN) != 0) xb = 2;

                if (xb != 0)
                {
                    MouseDirectBinding b = new MouseDirectBinding();
                    b.Signature = dev.Signature;
                    b.XButton = xb;
                    int button = _learn.Button;
                    if (button == 4) { _mb4 = b; _b4 = null; }
                    else { _mb5 = b; _b5 = null; }
                    UpdateBindingLabels();
                    SaveSettings();
                    EndLearn("Готово: фізична кнопка " + button + " прив'язана напряму до " + b.Describe() + ". Профіль Redragon не змінено.");
                }
                return;
            }

            EvaluateMouseBinding(_mb4, dev, flags, 4);
            EvaluateMouseBinding(_mb5, dev, flags, 5);
        }

        private void EvaluateMouseBinding(MouseDirectBinding b, RawMouseDeviceInfo dev, ushort flags, int button)
        {
            if (b == null || String.IsNullOrEmpty(b.Signature) || !String.Equals(b.Signature, dev.Signature, StringComparison.OrdinalIgnoreCase)) return;

            ushort down = b.XButton == 1 ? RawHidManager.RI_MOUSE_BUTTON_4_DOWN : RawHidManager.RI_MOUSE_BUTTON_5_DOWN;
            ushort up = b.XButton == 1 ? RawHidManager.RI_MOUSE_BUTTON_4_UP : RawHidManager.RI_MOUSE_BUTTON_5_UP;

            if ((flags & down) != 0 && !b.CurrentActive)
            {
                b.CurrentActive = true;
                DirectButtonChanged(button, true);
            }
            if ((flags & up) != 0 && b.CurrentActive)
            {
                b.CurrentActive = false;
                DirectButtonChanged(button, false);
            }
        }

        private void OnHidReport(HidDeviceInfo dev, byte[] report)
        {
            string key = dev.Handle.ToInt64().ToString("X") + ":" + report.Length;
            _lastReports[key] = (byte[])report.Clone();

            if (_learn != null)
            {
                HandleLearn(dev, report, key);
                return;
            }
            if (_mb4 == null) EvaluateBinding(_b4, dev, report, 4);
            if (_mb5 == null) EvaluateBinding(_b5, dev, report, 5);
        }

        private void HandleLearn(HidDeviceInfo dev, byte[] report, string key)
        {
            LearnCandidate c;
            if (!_learn.Candidates.TryGetValue(key, out c))
            {
                c = new LearnCandidate(); c.Key = key; c.Device = dev; c.First = (byte[])report.Clone(); c.FirstAt = DateTime.Now; _learn.Candidates[key] = c; return;
            }
            if (Same(c.First, report)) return;
            double ms = (DateTime.Now - c.FirstAt).TotalMilliseconds;
            if (ms < 120) { c.First = (byte[])report.Clone(); c.FirstAt = DateTime.Now; return; }
            if (ms > 5000) { c.First = (byte[])report.Clone(); c.FirstAt = DateTime.Now; return; }

            int bestByte = -1; byte bestMask = 0; int bestScore = -999;
            int len = Math.Min(c.First.Length, report.Length);
            for (int i = 0; i < len; i++)
            {
                byte diff = (byte)(c.First[i] ^ report[i]);
                if (diff == 0) continue;
                for (int bit = 0; bit < 8; bit++)
                {
                    byte mask = (byte)(1 << bit); if ((diff & mask) == 0) continue;
                    int score = 0;
                    if ((diff & (diff - 1)) == 0) score += 4;
                    if (i > 0) score += 2;
                    if (i < 16) score += 1;
                    if (score > bestScore) { bestScore = score; bestByte = i; bestMask = mask; }
                }
            }
            if (bestByte < 0) return;

            DirectBinding b = new DirectBinding(); b.Fp.VendorId = dev.Fp.VendorId; b.Fp.ProductId = dev.Fp.ProductId; b.Fp.UsagePage = dev.Fp.UsagePage; b.Fp.Usage = dev.Fp.Usage;
            b.ByteIndex = bestByte; b.Mask = bestMask; b.ActiveBit = (c.First[bestByte] & bestMask) != 0;
            if (c.First.Length > 0 && report.Length > 0 && c.First[0] == report[0] && bestByte != 0) { b.HasTag = true; b.TagValue = c.First[0]; }

            int button = _learn.Button; if (button == 4) _b4 = b; else _b5 = b;
            UpdateBindingLabels(); SaveSettings();
            EndLearn("Готово: фізична кнопка " + button + " прив'язана напряму через HID. Налаштування Redragon не змінювалися.");
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true;
        }

        private void EvaluateBinding(DirectBinding b, HidDeviceInfo d, byte[] r, int button)
        {
            if (b == null || b.Mask == 0 || b.ByteIndex < 0 || b.ByteIndex >= r.Length) return;
            if (b.Fp.VendorId != d.Fp.VendorId || b.Fp.ProductId != d.Fp.ProductId || b.Fp.UsagePage != d.Fp.UsagePage || b.Fp.Usage != d.Fp.Usage) return;
            if (b.HasTag && (r.Length == 0 || r[0] != b.TagValue)) return;
            bool bit = (r[b.ByteIndex] & b.Mask) != 0; bool active = bit == b.ActiveBit;
            if (active == b.CurrentActive) return; b.CurrentActive = active;
            DirectButtonChanged(button, active);
        }

        private void DirectButtonChanged(int button, bool down)
        {
            if (button == 4) _button4Held = down; else _button5Held = down;
            ClickAction a = button == 4 ? _action4 : _action5;
            if (!_enabledValue || a == ClickAction.None) { if (!down) StopActionIfUnused(a); return; }
            if (down)
            {
                if (a == ClickAction.Left) _leftWorker.StartHold(); else if (a == ClickAction.Right) _rightWorker.StartHold();
            }
            else StopActionIfUnused(a);
        }

        private void StopActionIfUnused(ClickAction a)
        {
            bool needLeft = (_button4Held && _action4 == ClickAction.Left) || (_button5Held && _action5 == ClickAction.Left);
            bool needRight = (_button4Held && _action4 == ClickAction.Right) || (_button5Held && _action5 == ClickAction.Right);
            if (!needLeft) _leftWorker.StopHold(); if (!needRight) _rightWorker.StopHold();
        }
        private void StopAll() { _button4Held = false; _button5Held = false; if (_leftWorker != null) _leftWorker.StopHold(); if (_rightWorker != null) _rightWorker.StopHold(); }

        private void LoadSettings()
        {
            if (!File.Exists(_settingsFile)) return;
            try
            {
                foreach (string raw in File.ReadAllLines(_settingsFile, Encoding.UTF8))
                {
                    string s = raw.Trim(); if (s.Length == 0 || s.StartsWith("#")) continue; int eq = s.IndexOf('='); if (eq < 1) continue;
                    string k = s.Substring(0, eq).Trim().ToLowerInvariant(), v = s.Substring(eq + 1).Trim(); int n; bool bo;
                    if (k == "enabled" && Boolean.TryParse(v, out bo)) _enabledValue = bo;
                    else if (k == "min" && Int32.TryParse(v, out n)) _minValue = n;
                    else if (k == "center" && Int32.TryParse(v, out n)) _centerValue = n;
                    else if (k == "max" && Int32.TryParse(v, out n)) _maxValue = n;
                    else if (k == "smooth" && Int32.TryParse(v, out n)) _smoothValue = n;
                    else if (k == "action4" && Int32.TryParse(v, out n)) _action4 = (ClickAction)n;
                    else if (k == "action5" && Int32.TryParse(v, out n)) _action5 = (ClickAction)n;
                    else if (k == "binding4") _b4 = DirectBinding.Parse(v);
                    else if (k == "binding5") _b5 = DirectBinding.Parse(v);
                    else if (k == "mousebinding4") _mb4 = MouseDirectBinding.Parse(v);
                    else if (k == "mousebinding5") _mb5 = MouseDirectBinding.Parse(v);
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile));
                List<string> l = new List<string>(); l.Add("enabled=" + _enabledValue); l.Add("min=" + _minValue); l.Add("center=" + _centerValue); l.Add("max=" + _maxValue); l.Add("smooth=" + _smoothValue);
                l.Add("action4=" + (int)_action4); l.Add("action5=" + (int)_action5); l.Add("binding4=" + (_b4 != null ? _b4.Serialize() : "")); l.Add("binding5=" + (_b5 != null ? _b5.Serialize() : ""));
                l.Add("mousebinding4=" + (_mb4 != null ? _mb4.Serialize() : "")); l.Add("mousebinding5=" + (_mb5 != null ? _mb5.Serialize() : ""));
                File.WriteAllLines(_settingsFile, l.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        private void ShowFromTray() { Show(); WindowState = FormWindowState.Normal; Activate(); _tray.Visible = false; }
        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!_reallyClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); _tray.Visible = true; return; }
            StopAll(); SaveSettings(); if (_leftWorker != null) _leftWorker.Dispose(); if (_rightWorker != null) _rightWorker.Dispose(); if (_tray != null) _tray.Dispose();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { MessageBox.Show(e.Exception.ToString(), "Neutron M693 Direct HID", MessageBoxButtons.OK, MessageBoxIcon.Error); };
            Application.Run(new MainForm());
        }
    }
}
