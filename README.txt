LiveWall - live wallpapers for Windows 10/11
============================================

INSTALL:   double-click "Install LiveWall.cmd"  (no administrator rights needed)
UNINSTALL: double-click "Uninstall LiveWall.cmd" (also puts your old wallpaper back)

Using it
- Settings opens after installing. It has tabs: Wallpapers, Slideshow & scaling, Boards & drawing, Music,
  Battery & performance and General. It remembers its size and the tab you used last.
- Wallpapers tab: add videos (MP4, MOV, MKV, WMV...), GIFs or pictures, or whole folders. You can also drag files
  onto the list. The Music column shows each wallpaper's music; right-click a wallpaper (or double-click it, or
  Music...) to give it its own music (a folder: every wallpaper in it).
- Slideshow & scaling tab: "Change wallpaper every" sets the slideshow interval; "Shuffle" randomises the order.
  The slideshow waits while you draw on the wallpaper (and starts counting again when you are done) and while the
  desktop is covered.
- The notification-area (tray) icon: left-click = Settings, right-click = Next / Previous / Pause, Collection,
  Board / Draw / Drawings on wallpapers, Music, Change every / Shuffle / Scaling, Add wallpapers, Settings, Exit.
- If you hide the tray icon, start LiveWall from the Start menu to open Settings (it never runs twice).
- From a shortcut or script: LiveWall.exe --settings=<wallpapers|slideshow|boards|music|battery|general> opens
  Settings on that tab.

Collections (sets of wallpapers for a theme or mood)
- Settings > Collections... : New, give it a name, add files, folders or the current wallpaper. The list in
  Settings itself is "All wallpapers".
- Ctrl+Alt+W switches to the next collection (a short note at the bottom of the screen says which); the tray
  icon > Collection menu picks one, and can add the current wallpaper to a collection.
- Hours: tick "Play by itself every day from ... to ..." (e.g. Day 07:00-19:00, Night 19:00-07:00). During its
  hours that collection plays; picking another one yourself holds until the next start or end of any hours.
- A board you are showing stays when the hours change: the new collection plays once you go back to the wallpaper.
  Nothing switches while you are drawing on the wallpaper either.

Boards and drawing
- Ctrl+Alt+B: the board you used last (today's or the Permanent one) becomes the wallpaper; press again to go
  back to your wallpaper. Each day starts a fresh daily board (earlier days are kept: tray icon > Board > Earlier
  days). The Permanent board never clears.
- Ctrl+Alt+D: draw - on the board if one is shown, otherwise on the wallpaper itself (pictures, GIFs and videos).
  Drawings on a wallpaper are kept per wallpaper and shown above it, below the desktop icons. Tray icon >
  Drawings on wallpapers hides or removes them. Esc (or Ctrl+Alt+D again) when done.
- Tools: P pen, H highlighter, E eraser (or the pen's eraser end / right mouse button; it erases only what it
  touches - press E again, or click the eraser button, for "whole strokes"), shapes (L line, A arrow,
  R rectangle, O ellipse; Shift = straight / square / circle; the shapes button also switches filled shapes),
  F fill (click inside a closed area; click a line or shape to recolor it), T text with emoji / kaomoji /
  symbols tabs (or Windows' own panel, Win + .), I eyedropper. Click text again to change it.
- Select: V (press again, or click the select button, for a lasso to draw around any shape). Drag inside it to
  move, the square handles to resize (corners keep the proportions; Shift stretches), the round handle to rotate
  (Shift snaps). The bar under it: cut, copy, delete, rotate left/right, flip. Ctrl+C / Ctrl+X / Ctrl+V,
  Delete, arrow keys nudge, Ctrl+A selects everything. Paste also takes pictures copied in other apps
  (e.g. a screenshot). Enter, Esc or clicking outside puts it down; Ctrl+Z before that puts it back.
- Colors: 1-9, or the rainbow button for any color (color square, RGB, HSV, hex, recent colors). Size: the
  slider, [ and ], or the mouse wheel over the slider. Ctrl+Z / Ctrl+Y undo/redo, Delete clears (undoable),
  B board background, Tab or the Today | Permanent switch changes board, Ctrl+S saves a copy as a picture.
- Board pictures: every board is also saved as a PNG in Pictures\LiveWall Boards (tray icon > Board > Open board
  pictures), updated whenever it changes.
- Start menu also gets "LiveWall Board" (show / hide the board) and "LiveWall Draw": pin them to the taskbar for
  one-click buttons.
- Shortcuts, the look of new boards (whiteboard, blackboard, grid, dots) and more are in Settings.
- A board costs nothing while shown: it is handed to Windows as a normal picture. Drawings on a video are one
  still, transparent layer the graphics chip blends in. The drawing window uses memory only while it is open.
- Drawing data: %APPDATA%\LiveWall\boards and %APPDATA%\LiveWall\ink (plain text, one line per element).

Music
- Settings > Music tab (or tray icon > Music > Music settings...): music for all wallpapers, and for the one shown:
  None, Random (shuffles your music folder), By theme (the picture's mood: calm, energetic, dark, happy, dreamy or
  cozy - put music in subfolders with those names; otherwise the whole folder plays), The video's own sound (the
  video wallpaper's soundtrack), or Custom files / a folder. Off (None) until you choose.
- It fades out when another app plays sound (a video, a call, a game), when the PC is locked, the screen is off
  or asleep, and (settings) while a fullscreen or maximized app is in front (File Explorer, Settings and the desktop
  don't count), on battery or with Energy Saver. A few seconds later the music player closes completely, so the
  sound card and the PC can go idle. When things are quiet again it comes back from the same place with a fade-in.
  Changing to a wallpaper with other music fades over to it. The fade time, the volume and how long to wait are on
  the Music tab, with Pause / Next song for what is playing now.
- Music for other wallpapers: Settings > Wallpapers tab > right-click a wallpaper > Music... (Default, None,
  Random, By theme, The video's own sound, or files / a folder of your own).
- Ctrl+Alt+M pauses / plays the music and shows the song's name (change the shortcut on the Music tab). Quiet only because
  of an app in front? Ctrl+Alt+M plays it anyway while that window stays in front. The Start menu also gets
  "LiveWall Music" (same thing): pin it to the taskbar for a one-click music button.
- With 3 or more wallpapers in a slideshow, each wallpaper gets its own song, repeated (changing every 15 minutes or
  less), or one song for the first half of its time and another for the second half (longer). The song fades out
  just before the wallpaper changes, and the new wallpaper fades in with its new song. With one or two wallpapers,
  or on a board, the songs just play on.
- Tray icon > Music: Play/Pause, Next track, Volume, "Silence while other apps play sound", For this wallpaper.
- Optional: "Ask AI (Claude) for the mood" sends each wallpaper's picture once to Anthropic with your own API key
  (stored encrypted for your Windows account). Off by default; without it the mood comes from the colors.

What it does to save battery
- Pictures are handed to Windows as a normal wallpaper: no cost at all while shown.
- Videos are decoded and scaled by the graphics chip (hardware decoding), with no per-frame work in LiveWall
  itself. About 0.5% CPU for 1080p on an i7-1260P. A video's soundtrack is not decoded and no audio stream is
  opened, so the sound card can sleep while a video plays. Changing wallpapers fades (about a second).
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
