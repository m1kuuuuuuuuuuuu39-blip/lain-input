// caret-probe.cs — UI Automation caret probe v2
// Reads "query" from stdin, writes "X,Y" (screen px) or "NONE"
// Diagnostics go to stderr
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

class CaretProbe {
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("imm32.dll")]
    static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll")]
    static extern bool ImmGetConversionStatus(IntPtr himc, out uint lpConversion, out uint lpSentence);
    [DllImport("imm32.dll")]
    static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr himc);
    const uint IME_CMODE_NATIVE = 0x0002;

    static bool IsChineseIME() {
        try {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            IntPtr himc = ImmGetContext(hwnd);
            if (himc == IntPtr.Zero) return false;
            uint conv, sent;
            bool ok = ImmGetConversionStatus(himc, out conv, out sent);
            ImmReleaseContext(hwnd, himc);
            return ok && (conv & IME_CMODE_NATIVE) != 0;
        } catch { return false; }
    }
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")]
    static extern bool GetGUIThreadInfo(uint idThread, out GUITHREADINFO lpgui);
    [DllImport("user32.dll")]
    static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO {
        public uint cbSize;
        public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    static void Log(string msg) {
        try { Console.Error.WriteLine("[probe] " + msg); Console.Error.Flush(); } catch { }
    }

    static int mouseX = -1, mouseY = -1;

    static string Probe() {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "NONE";

        try {
            AutomationElement el = AutomationElement.FromHandle(hwnd);

            // ---- 1) TextPattern2.GetCaretRange via reflection (runtime .NET 4.8) ----
            try {
                Type tp2Type = Type.GetType(
                    "System.Windows.Automation.Text.TextPattern2, UIAutomationClient, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
                if (tp2Type != null) {
                    object patternObj = null;
                    // TextPattern2.Pattern static field
                    object patternField = tp2Type.GetField("Pattern").GetValue(null);
                    if (patternObj == null && patternField != null) {
                        // TryGetCurrentPattern needs AutomationPattern arg
                        AutomationPattern ap = patternField as AutomationPattern;
                        if (ap != null && el.TryGetCurrentPattern(ap, out patternObj)) {
                            bool isActive;
                            object caretRange = tp2Type.GetMethod("GetCaretRange").Invoke(patternObj, new object[] { });
                            // GetCaretRange(out bool isActive) — get isActive from ref arg
                            MethodInfo mi = tp2Type.GetMethod("GetCaretRange");
                            object[] args = new object[] { false };
                            caretRange = mi.Invoke(patternObj, args);
                            isActive = (bool)args[0];
                            if (isActive && caretRange != null) {
                                System.Windows.Rect[] rects = (System.Windows.Rect[])caretRange.GetType()
                                    .GetMethod("GetBoundingRectangles").Invoke(caretRange, null);
                                if (rects.Length > 0 && !rects[0].IsEmpty) {
                                    return (int)rects[0].X + "," + (int)rects[0].Y;
                                }
                            }
                        }
                    }
                } else {
                    Log("TextPattern2 type not found");
                }
            } catch (Exception ex) {
                Log("TP2 failed: " + ex.Message);
            }

            // ---- 2) TextPattern ----
            try {
                object patternObj = null;
                if (el.TryGetCurrentPattern(TextPattern.Pattern, out patternObj)) {
                    TextPattern tp = (TextPattern)patternObj;
                    TextPatternRange[] sel = tp.GetSelection();
                    if (sel.Length > 0) {
                        System.Windows.Rect[] rects = sel[0].GetBoundingRectangles();
                        if (rects.Length > 0 && !rects[0].IsEmpty) {
                            return (int)rects[0].X + "," + (int)rects[0].Y;
                        }
                    }
                    // No selection (Chrome/Chromium): estimate caret from document text length
                    TextPatternRange doc = null;
                    try { doc = tp.DocumentRange; } catch { }
                    if (doc == null) {
                        try {
                            TextPatternRange[] visible = tp.GetVisibleRanges();
                            if (visible.Length > 0) doc = visible[0];
                        } catch { }
                    }
                    if (doc != null) {
                        string text = "";
                        try { text = doc.GetText(1024); } catch { }
                        System.Windows.Rect ctrl = el.Current.BoundingRectangle;
                        if (!ctrl.IsEmpty && ctrl.Width > 0 && ctrl.Height > 0) {
                            int charCount = Math.Max(0, text.Length);
                            // Approximate: ~8px per char for typical UI fonts
                            int estX = (int)ctrl.X + Math.Min(charCount * 8, (int)ctrl.Width - 4);
                            int estY = (int)(ctrl.Y + ctrl.Height / 2);
                            Log("est via doc text: len=" + charCount + " ctrl=[" + (int)ctrl.X + "," + (int)ctrl.Y + "," + (int)ctrl.Width + "]");
                            return estX + "," + estY;
                        }
                    }
                }
            } catch (Exception ex) {
                Log("TP1 failed: " + ex.Message);
            }

            // ---- 3) Focused element bounding rect (any control) ----
            try {
                AutomationElement focused = AutomationElement.FocusedElement;
                if (focused != null) {
                    System.Windows.Rect r = focused.Current.BoundingRectangle;
                    Log("focused rect=[" + (int)r.X + "," + (int)r.Y + "," + (int)r.Width + "," + (int)r.Height + "]");
                    if (!r.IsEmpty && r.Width > 0 && r.Height > 0 && r.Width < 500 && r.Height < 120) {
                        if (r.Width <= 50 && r.Height <= 40) {
                            // Character-level element: use its center
                            return (int)(r.X + r.Width / 2) + "," + (int)(r.Y + r.Height / 2);
                        }
                        // Control-level: left-center
                        return (int)r.X + "," + (int)(r.Y + r.Height / 2);
                    }
                } else {
                    Log("no focused element");
                }
            } catch (Exception ex) {
                Log("Focused failed: " + ex.Message);
            }

            // ---- 4) Drill-down from window root ----
            // Prefer Document controls (VS Code editor) with TextPattern,
            // then Edit controls, then mouse-position ElementFromPoint
            try {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root != null) {
                    // 4a: Document control (VS Code editor, browsers) with TextPattern
                    // Retry a few times: VS Code needs a moment to activate accessibility mode
                    AutomationElement doc = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                    if (doc != null) {
                        System.Windows.Rect dr = doc.Current.BoundingRectangle;
                        Log("drill doc=[" + (int)dr.X + "," + (int)dr.Y + "," + (int)dr.Width + "," + (int)dr.Height + "]");
                        object dp = null;
                        if (doc.TryGetCurrentPattern(TextPattern.Pattern, out dp)) {
                            TextPattern dtp = (TextPattern)dp;
                            for (int attempt = 0; attempt < 3; attempt++) {
                                try {
                                    TextPatternRange[] dsel = dtp.GetSelection();
                                    if (dsel.Length > 0) {
                                        System.Windows.Rect[] rects = dsel[0].GetBoundingRectangles();
                                        if (rects.Length > 0 && !rects[0].IsEmpty) {
                                            Log("doc sel rect=[" + (int)rects[0].X + "," + (int)rects[0].Y + "] attempt=" + attempt);
                                            return (int)rects[0].X + "," + (int)rects[0].Y;
                                        }
                                    }
                                } catch { }
                                if (attempt < 2) System.Threading.Thread.Sleep(50);
                            }
                            Log("doc GetSelection empty after retries");
                        }
                        // Document without selection: use its center as fallback
                        if (!dr.IsEmpty && dr.Width > 0 && dr.Height > 0) {
                            return (int)dr.X + "," + (int)(dr.Y + dr.Height / 2);
                        }
                    }

                    // 4b: ElementFromPoint at mouse position (user usually clicked the field)
                    if (mouseX >= 0 && mouseY >= 0) {
                        try {
                            System.Windows.Point pt = new System.Windows.Point(mouseX, mouseY);
                            AutomationElement under = AutomationElement.FromPoint(pt);
                            if (under != null) {
                                System.Windows.Rect ur = under.Current.BoundingRectangle;
                                Log("mouse el=[" + (int)ur.X + "," + (int)ur.Y + "," + (int)ur.Width + "," + (int)ur.Height + "]");
                                if (!ur.IsEmpty && ur.Width > 0 && ur.Height > 0 && ur.Width < 500 && ur.Height < 120) {
                                    return (int)ur.X + "," + (int)(ur.Y + ur.Height / 2);
                                }
                                // TextPattern on element under mouse
                                object mp = null;
                                if (under.TryGetCurrentPattern(TextPattern.Pattern, out mp)) {
                                    try {
                                        TextPattern mtp = (TextPattern)mp;
                                        TextPatternRange[] msel = mtp.GetSelection();
                                        if (msel.Length > 0) {
                                            System.Windows.Rect[] mrects = msel[0].GetBoundingRectangles();
                                            if (mrects.Length > 0 && !mrects[0].IsEmpty) {
                                                return (int)mrects[0].X + "," + (int)mrects[0].Y;
                                            }
                                        }
                                    } catch { }
                                }
                            }
                        } catch (Exception ex) {
                            Log("mouse el failed: " + ex.Message);
                        }
                    }

                    // 4c: Edit controls — reject very wide ones (address bars)
                    AutomationElement edit = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                    if (edit != null) {
                        System.Windows.Rect er = edit.Current.BoundingRectangle;
                        Log("drill edit=[" + (int)er.X + "," + (int)er.Y + "," + (int)er.Width + "," + (int)er.Height + "]");
                        if (!er.IsEmpty && er.Width > 0 && er.Height > 0 && er.Width < 600) {
                            return (int)er.X + "," + (int)(er.Y + er.Height / 2);
                        }
                    }
                }
                Log("drill failed");
            } catch (Exception ex) {
                Log("Drill failed: " + ex.Message);
            }
        } catch (Exception ex) {
            Log("Probe outer: " + ex.Message);
        }

        // ---- 4) Classic Win32 caret ----
        try {
            uint pid;
            uint tid = GetWindowThreadProcessId(hwnd, out pid);
            if (tid != 0) {
                GUITHREADINFO info = new GUITHREADINFO();
                info.cbSize = (uint)Marshal.SizeOf(typeof(GUITHREADINFO));
                if (GetGUIThreadInfo(tid, out info) && info.hwndCaret != IntPtr.Zero) {
                    POINT p = new POINT();
                    p.X = info.rcCaret.Left; p.Y = info.rcCaret.Top;
                    if (ClientToScreen(info.hwndCaret, ref p)) {
                        return p.X + "," + p.Y;
                    }
                }
            }
        } catch { }

        return "NONE";
    }

    static void Main() {
        Log("caret-probe v3 started");
        string line;
        while ((line = Console.ReadLine()) != null) {
            if (line == "quit") break;
            if (line == "query") {
                Console.WriteLine(Probe());
                Console.Out.Flush();
            } else if (line == "ime") {
                Console.WriteLine(IsChineseIME() ? "CN" : "EN");
                Console.Out.Flush();
            } else if (line.StartsWith("mouse ")) {
                string[] parts = line.Split(' ');
                if (parts.Length >= 3) {
                    int.TryParse(parts[1], out mouseX);
                    int.TryParse(parts[2], out mouseY);
                }
            }
        }
    }
}
