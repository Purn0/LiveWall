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
- Ctrl+Alt+B: today's board becomes the wallpaper; press again to go back to your wallpaper. Each day starts a
  fresh board (earlier days are kept: tray icon > Board > Earlier days). There is also a Permanent board that
  never clears.
- Ctrl+Alt+D: draw - on the board if one is shown, otherwise on the wallpaper itself (pictures, GIFs and videos).
  Drawings on a wallpaper are kept per wallpaper and shown above it, below the desktop icons. Tray icon >
  Drawings on wallpapers hides or removes them. Esc (or Ctrl+Alt+D again) when done.
- Tools: P pen, H highlighter, E eraser (or the pen's eraser end / right mouse button; it erases only what it
  touches - press E again, or click the eraser button, for "whole strokes"), shapes (L line, A arrow,
  R rectangle, O ellipse; Shift = straight / square / circle; the shapes button also switches filled shapes),
  F fill (click inside a closed area; click a line or shape to recolor it), T text with emoji / kaomoji /
  symbols tabs (or Windows' own panel, Win + .), I eyedropper. Click text again to change it.
- Colors: 1-9, or the rainbow button for any color (color square, RGB, HSV, hex, recent colors). Size: the
  slider, [ and ], or the mouse wheel over the slider. Ctrl+Z / Ctrl+Y undo/redo, Delete clears (undoable),
  B board background, Tab or the Today | Permanent switch changes board, Ctrl+S saves a copy as a picture.
- Board pictures: every board is also saved as a PNG in Pictures\LiveWall Boards (tray icon > Board > Open board
  pictures), updated whenever it changes.
- Start menu also gets "LiveWall Board" and "LiveWall Draw": pin them to the taskbar for one-click buttons.
- Shortcuts, the look of new boards (whiteboard, blackboard, grid, dots) and more are in Settings.
- A board costs nothing while shown: it is handed to Windows as a normal picture. Drawings on a video are one
  still, transparent layer the graphics chip blends in. The drawing window uses memory only while it is open.
- Drawing data: %APPDATA%\LiveWall\boards and %APPDATA%\LiveWall\ink (plain text, one line per element).

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
