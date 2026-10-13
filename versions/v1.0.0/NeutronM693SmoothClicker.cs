using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace NeutronM693SmoothClicker
{
    internal enum ClickAction
    {
        None = 0,
        Left = 1,
        Right = 2
    }

    internal sealed class TimingSnapshot
    {
        public int Min;
        public int Max;
        public int Center;
        public int Smooth;

        public TimingSnapshot(int min, int max, int center, int smooth)
        {
            Min = min;
            Max = max;
            Center = center;
            Smooth = smooth;
        }
    }

    internal sealed class SmoothDelayGenerator
    {
        private readonly Random _random;
        private double _current;
        private bool _initialized;

        public SmoothDelayGenerator(int seed)
        {
            _random = new Random(seed);
        }

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
            if (s.Max < s.Min)
            {
                int tmp = s.Min;
                s.Min = s.Max;
                s.Max = tmp;
            }

            s.Center = Math.Max(s.Min, Math.Min(s.Max, s.Center));
            s.Smooth = Math.Max(0, Math.Min(99, s.Smooth));

            if (!_initialized)
            {
                _current = s.Center;
                _initialized = true;
            }

            // Candidate timing is centered around the preferred delay, but the
            // next value is strongly correlated with the previous one. This
            // produces a smooth drift instead of sharp 10 -> 150 -> 70 jumps.
            double sigma = Math.Max(2.0, (s.Max - s.Min) / 4.0);
            double candidate = Clamp(s.Center + NextGaussian() * sigma, s.Min, s.Max);

            double keep = s.Smooth / 100.0;           // e.g. 0.88
            double next = _current * keep + candidate * (1.0 - keep);

            // Extra anti-jump limiter. The interval can drift through the full
            // range, but does so over several clicks rather than teleporting.
            double maxStep = Math.Max(2.0, (s.Max - s.Min) * 0.06);
            double delta = next - _current;
            if (delta > maxStep) delta = maxStep;
            if (delta < -maxStep) delta = -maxStep;

            _current = Clamp(_current + delta, s.Min, s.Max);
            return (int)Math.Round(_current);
        }
    }

    internal sealed class ClickWorker : IDisposable
    {
        private readonly ClickAction _action;
        private readonly Func<TimingSnapshot> _settingsProvider;
        private readonly Action<int> _delayReported;
        private readonly ManualResetEvent _releaseEvent = new ManualResetEvent(false);
        private readonly object _sync = new object();
        private volatile bool _held;
        private Thread _thread;
        private readonly SmoothDelayGenerator _generator;

        public ClickWorker(ClickAction action, Func<TimingSnapshot> settingsProvider, Action<int> delayReported, int seed)
        {
            _action = action;
            _settingsProvider = settingsProvider;
            _delayReported = delayReported;
            _generator = new SmoothDelayGenerator(seed);
        }

        public void StartHold()
        {
            lock (_sync)
            {
                if (_held) return;
                _held = true;
                _releaseEvent.Reset();
                _thread = new Thread(Loop);
                _thread.IsBackground = true;
                _thread.Name = "NeutronClickWorker-" + _action.ToString();
                _thread.Start();
            }
        }

        public void StopHold()
        {
            _held = false;
            _releaseEvent.Set();
        }

        private void Loop()
        {
            while (_held)
            {
                MouseInjector.Click(_action);

                TimingSnapshot s = _settingsProvider();
                int delay = _generator.Next(s);
                if (_delayReported != null) _delayReported(delay);

                if (_releaseEvent.WaitOne(delay)) break;
            }
        }

        public void Dispose()
        {
            StopHold();
            try
            {
                if (_thread != null && _thread.IsAlive) _thread.Join(250);
            }
            catch { }
            _releaseEvent.Close();
        }
    }

    internal static class MouseInjector
    {
        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public static void Click(ClickAction action)
        {
            uint down;
            uint up;

            if (action == ClickAction.Left)
            {
                down = MOUSEEVENTF_LEFTDOWN;
                up = MOUSEEVENTF_LEFTUP;
            }
            else if (action == ClickAction.Right)
            {
                down = MOUSEEVENTF_RIGHTDOWN;
                up = MOUSEEVENTF_RIGHTUP;
            }
            else
            {
                return;
            }

            INPUT[] inputs = new INPUT[2];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].mi.dwFlags = down;
            inputs[1].type = INPUT_MOUSE;
            inputs[1].mi.dwFlags = up;
            SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
        }
    }

    internal sealed class GlobalMouseHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_XBUTTONUP = 0x020C;
        private const int XBUTTON1 = 0x0001;
        private const int XBUTTON2 = 0x0002;

        [Flags]
        private enum LowLevelMouseFlags : uint
        {
            Injected = 0x00000001,
            LowerIntegrityInjected = 0x00000002
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public LowLevelMouseFlags flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private readonly LowLevelMouseProc _proc;
        private IntPtr _hookId = IntPtr.Zero;

        public event Action<int, bool> XButtonChanged;
        public Func<bool> ShouldSuppress;

        public GlobalMouseHook()
        {
            _proc = HookCallback;
        }

        public void Install()
        {
            if (_hookId != IntPtr.Zero) return;

            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
            }

            if (_hookId == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Не вдалося встановити глобальний mouse hook.");
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
                {
                    MSLLHOOKSTRUCT data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    int which = (int)((data.mouseData >> 16) & 0xFFFF);
                    int logicalButton = 0;
                    if (which == XBUTTON1) logicalButton = 4;
                    if (which == XBUTTON2) logicalButton = 5;

                    if (logicalButton != 0)
                    {
                        bool down = msg == WM_XBUTTONDOWN;
                        Action<int, bool> handler = XButtonChanged;
                        if (handler != null) handler(logicalButton, down);

                        if (ShouldSuppress != null && ShouldSuppress())
                            return new IntPtr(1);
                    }
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private CheckBox _enabled;
        private CheckBox _suppress;
        private ComboBox _button4Action;
        private ComboBox _button5Action;
        private NumericUpDown _min;
        private NumericUpDown _max;
        private NumericUpDown _center;
        private TrackBar _smooth;
        private Label _smoothLabel;
        private Label _leftStatus;
        private Label _rightStatus;
        private Label _hookStatus;
        private Button _saveButton;
        private NotifyIcon _tray;
        private GlobalMouseHook _hook;
        private ClickWorker _leftWorker;
        private ClickWorker _rightWorker;
        private volatile int _lastLeftDelay;
        private volatile int _lastRightDelay;
        private string _settingsFile;
        private bool _reallyClose;

        private volatile int _minDelay = 10;
        private volatile int _maxDelay = 150;
        private volatile int _centerDelay = 45;
        private volatile int _smoothValue = 88;
        private volatile bool _isEnabled = true;
        private volatile bool _suppressButtons = true;
        private volatile ClickAction _button4 = ClickAction.Left;
        private volatile ClickAction _button5 = ClickAction.Right;

        public MainForm()
        {
            Text = "Neutron M693 Smooth Clicker 1.0.0";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 430);
            MinimumSize = new Size(576, 469);
            Font = new Font("Segoe UI", 9F);
            Icon = SystemIcons.Application;

            _settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NeutronM693SmoothClicker", "settings.ini");
            LoadSettings();
            BuildUi();
            ApplyFieldsToUi();
            WireUi();

            _leftWorker = new ClickWorker(ClickAction.Left, GetTimingSnapshot, delegate(int d) { _lastLeftDelay = d; }, Environment.TickCount ^ 0x51A2);
            _rightWorker = new ClickWorker(ClickAction.Right, GetTimingSnapshot, delegate(int d) { _lastRightDelay = d; }, Environment.TickCount ^ 0xA15C);

            _hook = new GlobalMouseHook();
            _hook.XButtonChanged += OnXButtonChanged;
            _hook.ShouldSuppress = ShouldSuppressXButtons;

            try
            {
                _hook.Install();
                _hookStatus.Text = "Hook: активний (кнопки 4/5 контролюються глобально)";
            }
            catch (Exception ex)
            {
                _hookStatus.Text = "Hook: ПОМИЛКА — " + ex.Message;
                _isEnabled = false;
                _enabled.Checked = false;
            }

            Timer timer = new Timer();
            timer.Interval = 250;
            timer.Tick += delegate
            {
                _leftStatus.Text = "Ліва: " + (_lastLeftDelay > 0 ? _lastLeftDelay.ToString() + " мс" : "очікує");
                _rightStatus.Text = "Права: " + (_lastRightDelay > 0 ? _lastRightDelay.ToString() + " мс" : "очікує");
            };
            timer.Start();

            FormClosing += OnFormClosing;
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    Hide();
                    _tray.Visible = true;
                }
            };
        }

        private void BuildUi()
        {
            Label title = new Label();
            title.Text = "Redragon TRIDENT M693 — утримання бокової кнопки = серія кліків";
            title.Font = new Font(Font, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(18, 15);
            Controls.Add(title);

            Label note = new Label();
            note.Text = "Затиснули кнопку 4/5 — цикл працює. Відпустили — зупинка відразу. Інтервал змінюється плавно.";
            note.AutoSize = true;
            note.Location = new Point(18, 42);
            Controls.Add(note);

            _enabled = new CheckBox();
            _enabled.Text = "Увімкнути обробку бокових кнопок";
            _enabled.AutoSize = true;
            _enabled.Location = new Point(20, 78);
            Controls.Add(_enabled);

            _suppress = new CheckBox();
            _suppress.Text = "Блокувати штатну дію Back/Forward кнопок 4/5";
            _suppress.AutoSize = true;
            _suppress.Location = new Point(280, 78);
            Controls.Add(_suppress);

            GroupBox buttons = new GroupBox();
            buttons.Text = "Кнопки";
            buttons.Location = new Point(18, 112);
            buttons.Size = new Size(524, 82);
            Controls.Add(buttons);

            Label l4 = new Label();
            l4.Text = "Кнопка 4 / XButton1:";
            l4.AutoSize = true;
            l4.Location = new Point(18, 35);
            buttons.Controls.Add(l4);

            _button4Action = new ComboBox();
            _button4Action.DropDownStyle = ComboBoxStyle.DropDownList;
            _button4Action.Items.AddRange(new object[] { "Вимкнено", "Ліва кнопка", "Права кнопка" });
            _button4Action.Location = new Point(160, 31);
            _button4Action.Width = 110;
            buttons.Controls.Add(_button4Action);

            Label l5 = new Label();
            l5.Text = "Кнопка 5 / XButton2:";
            l5.AutoSize = true;
            l5.Location = new Point(287, 35);
            buttons.Controls.Add(l5);

            _button5Action = new ComboBox();
            _button5Action.DropDownStyle = ComboBoxStyle.DropDownList;
            _button5Action.Items.AddRange(new object[] { "Вимкнено", "Ліва кнопка", "Права кнопка" });
            _button5Action.Location = new Point(430, 31);
            _button5Action.Width = 84;
            buttons.Controls.Add(_button5Action);

            GroupBox timing = new GroupBox();
            timing.Text = "Плавна зміна інтервалу";
            timing.Location = new Point(18, 204);
            timing.Size = new Size(524, 145);
            Controls.Add(timing);

            AddNumeric(timing, "Мінімум, мс", 18, 30, out _min, 1, 1000);
            AddNumeric(timing, "Типово, мс", 180, 30, out _center, 1, 1000);
            AddNumeric(timing, "Максимум, мс", 342, 30, out _max, 1, 1000);

            Label sl = new Label();
            sl.Text = "Плавність:";
            sl.AutoSize = true;
            sl.Location = new Point(18, 90);
            timing.Controls.Add(sl);

            _smooth = new TrackBar();
            _smooth.Minimum = 0;
            _smooth.Maximum = 99;
            _smooth.TickFrequency = 10;
            _smooth.Location = new Point(85, 78);
            _smooth.Width = 345;
            timing.Controls.Add(_smooth);

            _smoothLabel = new Label();
            _smoothLabel.AutoSize = true;
            _smoothLabel.Location = new Point(442, 90);
            timing.Controls.Add(_smoothLabel);

            _leftStatus = new Label();
            _leftStatus.AutoSize = true;
            _leftStatus.Location = new Point(20, 362);
            Controls.Add(_leftStatus);

            _rightStatus = new Label();
            _rightStatus.AutoSize = true;
            _rightStatus.Location = new Point(160, 362);
            Controls.Add(_rightStatus);

            _hookStatus = new Label();
            _hookStatus.AutoSize = true;
            _hookStatus.Location = new Point(20, 388);
            Controls.Add(_hookStatus);

            _saveButton = new Button();
            _saveButton.Text = "Зберегти";
            _saveButton.Location = new Point(446, 356);
            _saveButton.Size = new Size(96, 32);
            Controls.Add(_saveButton);

            _tray = new NotifyIcon();
            _tray.Text = "Neutron M693 Smooth Clicker";
            _tray.Icon = SystemIcons.Application;
            _tray.Visible = false;
            _tray.DoubleClick += delegate { ShowFromTray(); };

            ContextMenuStrip trayMenu = new ContextMenuStrip();
            ToolStripMenuItem open = new ToolStripMenuItem("Відкрити");
            open.Click += delegate { ShowFromTray(); };
            trayMenu.Items.Add(open);
            ToolStripMenuItem exit = new ToolStripMenuItem("Вийти");
            exit.Click += delegate { _reallyClose = true; Close(); };
            trayMenu.Items.Add(exit);
            _tray.ContextMenuStrip = trayMenu;
        }

        private static void AddNumeric(Control parent, string labelText, int x, int y, out NumericUpDown control, int min, int max)
        {
            Label l = new Label();
            l.Text = labelText;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            parent.Controls.Add(l);

            control = new NumericUpDown();
            control.Minimum = min;
            control.Maximum = max;
            control.Location = new Point(x, y + 24);
            control.Width = 100;
            parent.Controls.Add(control);
        }

        private void WireUi()
        {
            _enabled.CheckedChanged += delegate
            {
                _isEnabled = _enabled.Checked;
                if (!_isEnabled)
                {
                    _leftWorker.StopHold();
                    _rightWorker.StopHold();
                }
            };
            _suppress.CheckedChanged += delegate { _suppressButtons = _suppress.Checked; };
            _button4Action.SelectedIndexChanged += delegate { _button4 = IndexToAction(_button4Action.SelectedIndex); };
            _button5Action.SelectedIndexChanged += delegate { _button5 = IndexToAction(_button5Action.SelectedIndex); };

            _min.ValueChanged += delegate { UpdateTimingFromUi(); };
            _max.ValueChanged += delegate { UpdateTimingFromUi(); };
            _center.ValueChanged += delegate { UpdateTimingFromUi(); };
            _smooth.ValueChanged += delegate { UpdateTimingFromUi(); };

            _saveButton.Click += delegate
            {
                SaveSettings();
                _saveButton.Text = "Збережено";
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 900;
                t.Tick += delegate
                {
                    t.Stop();
                    t.Dispose();
                    if (!_saveButton.IsDisposed) _saveButton.Text = "Зберегти";
                };
                t.Start();
            };
        }

        private void ApplyFieldsToUi()
        {
            _enabled.Checked = _isEnabled;
            _suppress.Checked = _suppressButtons;
            _button4Action.SelectedIndex = ActionToIndex(_button4);
            _button5Action.SelectedIndex = ActionToIndex(_button5);
            _min.Value = ClampDecimal(_minDelay, _min.Minimum, _min.Maximum);
            _max.Value = ClampDecimal(_maxDelay, _max.Minimum, _max.Maximum);
            _center.Value = ClampDecimal(_centerDelay, _center.Minimum, _center.Maximum);
            _smooth.Value = Math.Max(_smooth.Minimum, Math.Min(_smooth.Maximum, _smoothValue));
            _smoothLabel.Text = _smooth.Value + "%";
        }

        private static decimal ClampDecimal(int value, decimal min, decimal max)
        {
            decimal v = value;
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private void UpdateTimingFromUi()
        {
            _minDelay = (int)_min.Value;
            _maxDelay = (int)_max.Value;
            _centerDelay = (int)_center.Value;
            _smoothValue = _smooth.Value;
            _smoothLabel.Text = _smoothValue + "%";

            if (_maxDelay < _minDelay)
            {
                _maxDelay = _minDelay;
                _max.Value = _maxDelay;
            }
            if (_centerDelay < _minDelay)
            {
                _centerDelay = _minDelay;
                _center.Value = _centerDelay;
            }
            if (_centerDelay > _maxDelay)
            {
                _centerDelay = _maxDelay;
                _center.Value = _centerDelay;
            }
        }

        private TimingSnapshot GetTimingSnapshot()
        {
            return new TimingSnapshot(_minDelay, _maxDelay, _centerDelay, _smoothValue);
        }

        private static ClickAction IndexToAction(int index)
        {
            if (index == 1) return ClickAction.Left;
            if (index == 2) return ClickAction.Right;
            return ClickAction.None;
        }

        private static int ActionToIndex(ClickAction action)
        {
            if (action == ClickAction.Left) return 1;
            if (action == ClickAction.Right) return 2;
            return 0;
        }

        private bool ShouldSuppressXButtons()
        {
            return _isEnabled && _suppressButtons;
        }

        private void OnXButtonChanged(int button, bool down)
        {
            if (!_isEnabled) return;

            ClickAction action = button == 4 ? _button4 : _button5;
            if (action == ClickAction.Left)
            {
                if (down) _leftWorker.StartHold(); else _leftWorker.StopHold();
            }
            else if (action == ClickAction.Right)
            {
                if (down) _rightWorker.StartHold(); else _rightWorker.StopHold();
            }
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            _tray.Visible = false;
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_reallyClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _tray.Visible = true;
                return;
            }

            SaveSettings();
            if (_leftWorker != null) _leftWorker.Dispose();
            if (_rightWorker != null) _rightWorker.Dispose();
            if (_hook != null) _hook.Dispose();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsFile)) return;
                string[] lines = File.ReadAllLines(_settingsFile);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int p = line.IndexOf('=');
                    if (p <= 0) continue;
                    string key = line.Substring(0, p).Trim();
                    string value = line.Substring(p + 1).Trim();

                    int iv;
                    bool bv;
                    if (key.Equals("Min", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out iv)) _minDelay = iv;
                    else if (key.Equals("Max", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out iv)) _maxDelay = iv;
                    else if (key.Equals("Center", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out iv)) _centerDelay = iv;
                    else if (key.Equals("Smooth", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out iv)) _smoothValue = iv;
                    else if (key.Equals("Enabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bv)) _isEnabled = bv;
                    else if (key.Equals("Suppress", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bv)) _suppressButtons = bv;
                    else if (key.Equals("Button4", StringComparison.OrdinalIgnoreCase)) _button4 = ParseAction(value, ClickAction.Left);
                    else if (key.Equals("Button5", StringComparison.OrdinalIgnoreCase)) _button5 = ParseAction(value, ClickAction.Right);
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                UpdateTimingFromUi();
                string dir = Path.GetDirectoryName(_settingsFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(_settingsFile, new string[]
                {
                    "# Neutron M693 Smooth Clicker",
                    "Min=" + _minDelay,
                    "Max=" + _maxDelay,
                    "Center=" + _centerDelay,
                    "Smooth=" + _smoothValue,
                    "Enabled=" + _isEnabled,
                    "Suppress=" + _suppressButtons,
                    "Button4=" + _button4.ToString(),
                    "Button5=" + _button5.ToString()
                });
            }
            catch { }
        }

        private static ClickAction ParseAction(string value, ClickAction fallback)
        {
            if (value.Equals("Left", StringComparison.OrdinalIgnoreCase)) return ClickAction.Left;
            if (value.Equals("Right", StringComparison.OrdinalIgnoreCase)) return ClickAction.Right;
            if (value.Equals("None", StringComparison.OrdinalIgnoreCase)) return ClickAction.None;
            return fallback;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
