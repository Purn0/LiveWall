using System;
using System.Drawing;
using System.Windows.Forms;
using LiveWall.Interop;

namespace LiveWall
{
    // A short note at the bottom of the screen under the mouse (works with the tray icon hidden). Never takes focus;
    // gone, window and all, after a couple of seconds.
    internal sealed class Toast : Form
    {
        static Toast shown;
        readonly Timer timer = new Timer { Interval = 2200 };

        public static void Show(string text)
        {
            if (shown != null && !shown.IsDisposed) shown.Close();
            shown = new Toast(text);
            shown.Show();
        }

        Toast(string text)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(32, 33, 36);
            ForeColor = Color.White;
            Font = new Font("Segoe UI Semibold", 11f);
            var label = new Label { Text = text, AutoSize = true, Location = new Point(18, 10), BackColor = BackColor, ForeColor = ForeColor, UseMnemonic = false };
            Controls.Add(label);
            Size sz = TextRenderer.MeasureText(text, Font);
            ClientSize = new Size(sz.Width + 36, sz.Height + 20);
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - 48);
            timer.Tick += (s, e) => Close();
            timer.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= (int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST | Native.WS_EX_TRANSPARENT);
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;
            try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); Font.Dispose(); }
            if (shown == this) shown = null;
            base.Dispose(disposing);
        }
    }
}
