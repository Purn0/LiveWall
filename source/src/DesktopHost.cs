using System;
using LiveWall.Interop;

namespace LiveWall
{
    // Finds where a window must live to be drawn *behind the desktop icons* but above Windows' wallpaper.
    //
    // Windows 11 24H2+ (this is what 25H2 uses): Progman (WS_EX_NOREDIRECTIONBITMAP) hosts SHELLDLL_DefView (the icons)
    //   and a WorkerW as direct children. Our surface is a *layered* child of Progman inserted right below DefView.
    // Older builds: sending 0x052C to Progman splits the desktop into a top-level WorkerW holding DefView plus a second
    //   WorkerW behind it; our surface becomes a child of that second WorkerW.
    internal sealed class DesktopHost
    {
        public IntPtr Progman { get; private set; }
        public IntPtr DefView { get; private set; }
        public IntPtr Parent { get; private set; }      // window our surfaces are children of
        public IntPtr InsertAfter { get; private set; } // z-order anchor for our surfaces inside Parent
        public bool ModernLayout { get; private set; }

        public bool IsValid
        {
            get
            {
                return Progman != IntPtr.Zero && Native.IsWindow(Progman) && Native.IsWindow(Parent)
                    && (DefView == IntPtr.Zero || Native.IsWindow(DefView));
            }
        }

        public bool Refresh()
        {
            Progman = Native.FindWindow("Progman", null);
            DefView = IntPtr.Zero; Parent = IntPtr.Zero; InsertAfter = Native.HWND_TOP;
            if (Progman == IntPtr.Zero) return false;

            // Ask Explorer to create the WorkerW used for wallpaper transitions (harmless if it exists already).
            IntPtr result;
            Native.SendMessageTimeout(Progman, 0x052C, new IntPtr(0xD), new IntPtr(1), Native.SMTO_ABORTIFHUNG, 1000, out result);

            IntPtr defInProgman = Native.FindWindowEx(Progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr workerInProgman = Native.FindWindowEx(Progman, IntPtr.Zero, "WorkerW", null);
            bool noRedirection = (Native.ExStyle(Progman) & Native.WS_EX_NOREDIRECTIONBITMAP) != 0;

            if (defInProgman != IntPtr.Zero && (workerInProgman != IntPtr.Zero || noRedirection))
            {
                ModernLayout = true;
                DefView = defInProgman;
                Parent = Progman;
                InsertAfter = DefView;
                return true;
            }

            // Legacy layout: find the top-level window hosting DefView; the WorkerW right after it is the target.
            IntPtr target = IntPtr.Zero, defView = IntPtr.Zero;
            Native.EnumWindows((h, l) =>
            {
                IntPtr dv = Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero)
                {
                    defView = dv;
                    target = Native.FindWindowEx(IntPtr.Zero, h, "WorkerW", null);
                }
                return true;
            }, IntPtr.Zero);

            if (target != IntPtr.Zero)
            {
                ModernLayout = false;
                DefView = defView;
                Parent = target;
                InsertAfter = Native.HWND_TOP;
                return true;
            }

            // Last resort: DefView still directly in Progman on an old build. Draw inside Progman under the icons.
            if (defInProgman != IntPtr.Zero)
            {
                ModernLayout = true;
                DefView = defInProgman;
                Parent = Progman;
                InsertAfter = defInProgman;
                return true;
            }
            return false;
        }

        // Converts screen (physical pixel) coordinates to the parent's client coordinates.
        public RECT ScreenToParent(RECT r)
        {
            Native.MapWindowPoints(IntPtr.Zero, Parent, ref r, 2);
            return r;
        }

        public override string ToString()
        {
            return string.Format("{0} layout, progman=0x{1:X}, parent=0x{2:X}, defview=0x{3:X}",
                ModernLayout ? "24H2+" : "legacy", Progman.ToInt64(), Parent.ToInt64(), DefView.ToInt64());
        }
    }
}
