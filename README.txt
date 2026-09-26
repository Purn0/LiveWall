LiveWall - live wallpapers for Windows 10/11
============================================

INSTALL:   double-click "Install LiveWall.cmd"  (no administrator rights needed)
UNINSTALL: double-click "Uninstall LiveWall.cmd" (also puts your old wallpaper back)

Using it
- Settings opens after installing. Add videos (MP4, MOV, MKV, WMV...), GIFs or pictures, or whole folders.
  You can also drag files onto the list.
- "Change wallpaper every" sets the slideshow interval; "Shuffle" randomises the order.
- The notification-area (tray) icon: left-click = Settings, right-click = Next / Previous / Pause / interval / scaling.
- If you hide the tray icon, start LiveWall from the Start menu to open Settings (it never runs twice).

Boards and drawing
- Ctrl+Alt+B: today's board replaces the wallpaper and you can write or draw on it right away. Each day starts a
  fresh board (earlier days are kept: tray icon > Board > Earlier days). There is also a Permanent board that
  never clears. Press Ctrl+Alt+B again when done; once more to go back to your wallpaper.
- Ctrl+Alt+D: draw on the wallpaper itself (pictures, GIFs and videos). Drawings are kept per wallpaper and shown
  above it, below the desktop icons. Tray icon > Drawings on wallpapers hides or removes them.
- While drawing: P pen, H highlighter, E eraser (or the pen's eraser end / right mouse button), 1-9 colors,
  [ ] size, Ctrl+Z / Ctrl+Y undo/redo, Delete clears (undoable), B board background, Tab daily/permanent board,
  Esc done. Pen pressure and touch are supported.
- Start menu also gets "LiveWall Board" and "LiveWall Draw": pin them to the taskbar for one-click buttons.
- Shortcuts, the look of new boards (whiteboard, blackboard, grid, dots) and more are in Settings.
- A board costs nothing while shown: it is handed to Windows as a normal picture. Drawings on a video are one
  still, transparent layer the graphics chip blends in. The drawing window uses memory only while it is open.
- Boards and drawings: %APPDATA%\LiveWall\boards and %APPDATA%\LiveWall\ink (plain text, one line per stroke).

What it does to save battery
- Pictures are handed to Windows as a normal wallpaper: no cost at all while shown.
- Videos are decoded and scaled by the graphics chip (hardware decoding), always muted, with no per-frame
  work in LiveWall itself. About 0.5% CPU for 1080p on an i7-1260P.
- GIFs are converted once into hardware-decoded H.264 video (cached), which is far cheaper than drawing GIFs.
- Playback pauses when you cannot see it: desktop covered by windows, fullscreen apps/games, locked PC,
  screen off, and optionally on battery or with Energy Saver on.
- When it pauses, the video player process is closed completely (after a minute when covered, a few seconds
  when the screen is off or you paused it): 0% CPU, no memory, and nothing that stops the PC from sleeping.
  The Windows wallpaper is set to the video's first frame, so this is invisible.

Good to know
- Windows cannot animate a wallpaper by itself, so a small program has to run; LiveWall starts with Windows
  and manages itself. It never needs administrator rights.
- HEVC/H.265, VP9 or AV1 videos need the matching (free or cheap) "Video Extension" from the Microsoft Store;
  H.264 MP4 always works. LiveWall tells you if a file cannot be played and skips it.
- Settings: %APPDATA%\LiveWall\settings.ini    Log and cache: %LOCALAPPDATA%\LiveWall
- Source code and build script are in the "source" folder (build with build.ps1; no SDK needed).
