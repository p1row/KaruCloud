# KaruCloud

A tiny native Windows player for SoundCloud (C# / WPF, no browser engine while you listen).
Unofficial: not affiliated with or endorsed by SoundCloud.

## What it does
- **Sign in on SoundCloud's own page** (e-mail, Google, Apple, Facebook, or create a new account there). No API keys, no Client ID.
  The sign-in window closes by itself when you're in, and the browser engine is released again. Several accounts at once; switch from the chip at the left of the header.
- SoundCloud-style layout: Home, Stations, Playlists, Albums, Following and artist profiles show as artwork cards (shelves with arrows, hover play button); song lists stay as lists.
- **Feed** like soundcloud.com: one block per post ("yashio posted a track 9 minutes ago") with cover, play button, genre tag, a clickable waveform that shows progress, like / repost / share / copy link / more, plus plays and comment counts. The Reposts switch hides or shows reposts.
- **Likes** as an artwork grid (switch to a list with the View buttons) with a live Filter box; the grid resizes with the window.
- **Song page** like soundcloud.com: big play button, title, upload date, clickable waveform (shows playback progress, click to seek), like / repost with live counts, add to queue, station, copy link,
  the artist card with a Follow button, plays / likes / reposts, description and related tracks.
- **Profile page** like soundcloud.com: banner, avatar, tabs (All, Popular tracks, Tracks, Albums, Playlists, Reposts), Follow, Station, followers / following / tracks and bio.
- Click a song's **title, cover or artist** anywhere (lists, cards, search, player bar) to open its page or the artist's profile. Hover a cover for the play button; double-click a row to play.
- **Like (heart) and Repost** buttons sit on every song row, on the song page and in the player bar; they show your current state. "Open on SoundCloud" is still in the right-click menu.
- Sidebar, grouped like soundcloud.com's menus: **Discover** (Home, Feed, Trending, Stations), **Library** (Likes, Playlists, Albums, Reposts, Following),
  **Listening** (History, Queue).
- Search has a scope chip (Everything / Tracks / Playlists / People). Double-click a person to open their profile (tracks, playlists, albums).
- Right-click anything: play next, add to queue, **start station**, like, repost, follow, go to artist, copy link, open on SoundCloud.
- **Autoplay**: when the queue ends, KaruCloud keeps playing related tracks (switch it off at the bottom of the sidebar).
- Tabs: every page and every opened playlist/profile/search is a tab. `+` adds a tab, `x` / middle-click closes,
  `Ctrl+T` new search, `Ctrl+W` close, `Ctrl+Tab` / `Ctrl+Shift+Tab` cycle, `Ctrl+1..9` jump, `Ctrl+L` search, `Space` play/pause.
- Player: play/pause, previous/next, seek, volume, shuffle, like/unlike.
- Tray: closes/minimizes to the tray; right-click it for the mini player (play/pause, next, volume, accounts).
- **Header like soundcloud.com**: your profile picture (opens the account menu), a bell for notifications, an envelope for messages (orange dot when something is new). Both open right inside KaruCloud: a notification opens the song or profile in a tab, a conversation opens as a chat in the dropdown. "Your profile" is in the account menu and a "..." menu (Settings, add account).
- **Live comments** on a song's page: the avatars of people who commented sit under the waveform (click one to jump there), a bubble shows each comment while the song passes its time,
  and the comment list below lets you write your own (posted at the second you're listening to). New comments are picked up every minute while the song plays.
- Discord Rich Presence (PreMiD-style options in Settings).
- Everything is held in memory while playing; nothing is downloaded or saved to disk. The session token is encrypted with Windows DPAPI.

## Changed in 1.6.4
- Discord Rich Presence uses one built-in Application ID shared by everyone (no Developer Portal, no pasting). The Rich Presence art asset
  named `soundcloud` must exist on that Discord application (used when a track has no cover).
- Quit is instant: windows are hidden first, at most 1.5 s is spent sending the last plays, then the process ends (this also removes the
  "ReleaseOnChannel" error that was logged on close).
- Bell: more notification routes are tried, and each 404 is written to crash.log with the route so the right one can be pinned down.

## Added in 1.6.2
- **Move tabs**: drag a tab left / right to put it where you want (the order is saved). Hover the tab strip and turn the mouse wheel to slide the tabs.
- **Middle-click auto-scroll**: middle-click empty page space, then move the mouse up / down to scroll (further = faster). Click again, press Esc or turn the wheel to stop. Hold the middle button and drag works too. Middle-click on a link / song title still opens a new tab, and on a tab still closes it.
- The close button's red hover now follows the window's rounded corner.

## Added in 1.5.4
- **Follow state now syncs.** KaruCloud reads your whole Following list from your account (the same way likes and reposts are synced) instead of asking about each person one by one, so the Follow buttons, the "Following" pills on pictures, the player-bar icon and the Following page all agree with soundcloud.com. A failed read is never taken as "not following", and a follow / unfollow you just made stays put even if SoundCloud's list lags a few seconds. A failed follow is rolled back (it wasn't before, from the player bar).
- **Bell and envelope are light now**: they open a plain list right under the icon, read straight from SoundCloud (no browser engine at all). Only if SoundCloud refuses that list does the web page open, and then as a compact window next to the header, trimmed to just the list (no side column, no trackers, no audio), shown only once it has been trimmed.
- **Sign-in window**: slim own title bar, smaller, shows only the sign-in dialog (the website behind it is hidden), trackers blocked, lighter browser flags. Google / Apple / Facebook sign-in still works.
- **Search results like soundcloud.com**: filter column (Everything / Tracks / People / Albums / Playlists), "Found 7 playlists, 33 tracks, 3 people", the best-matching person on top with a Follow button, tracks as blocks with a clickable waveform. The header scope menu has Albums too.
- **Station page like soundcloud.com**: banner with "Track station" / "Artist station", the track and time badge, the STATION card, Like / Share / Add to Next up / Shuffle, the numbered list, "Based on" and **Artists featured** with Follow buttons.

## Added in 1.5.2
- **Like/repost no longer pops a browser window** for an ordinary failed click. The hidden window is now only shown on purpose, from **"Fix likes & sync"** in the account menu (top left) - and it now closes itself after 8 seconds idle instead of 25.
- **Repeat** button next to Shuffle: off -> all -> one, cycling with a click; a song page's play button now also plays/pauses from the hero.
- **Follow, right in the player bar**: a person-icon button next to the volume slider follows/unfollows the artist of whatever is playing, and turns orange once you do.
- **"Next up" popup**: the list icon at the far right of the player bar opens a floating panel like soundcloud.com's - upcoming queue, the track playing now (like + "...") and an **Autoplay station** switch. The "..." on a queued track only has Add to Next up / Go to station / Report.
- **Tabs now carry a small icon** (song note, person, list, magnifier, or the same symbol as the matching sidebar entry) so two tabs with similar titles can be told apart.
- **"Following" pill** on a person's picture in grids (search results, shelves, Following list) when you already follow them.

## Added in 1.5.1
- **Song page redesigned like soundcloud.com**: the artist's own banner (the song's "visual") sits behind the play button, title and waveform; songs without one keep the gradient with the cover on the right.
  One action bar under the waveform: like (with count), a **Write a comment** box (posts at the second you are listening to), share, copy link, repost, add to queue and "...". The comment box moved out of the comment list.
- **Select and copy text**: song titles, descriptions, comments, profile bios and playlist descriptions can be selected with the mouse (Ctrl+C or right-click -> Copy).
- **Likes / reposts / comments**: a hard "access restricted" page no longer opens soundcloud.com's home page and waits for you to close it. KaruCloud goes straight to pressing the real button on the song's own page.
  The page-button route knows the newer soundcloud.com button labels, and when it can't find a button it writes what it saw to `crash.log` (line starting `page action`).

## Added in 1.5.0
- **Like / repost / follow / comment through the real page**: when the plain API route is refused (bot protection, or a 404 on a route SoundCloud moved), KaruCloud opens the song / profile page in its hidden browser and presses the real button, so SoundCloud's own scripts send the request. If a check is showing, the window opens so you can finish it by hand and closes by itself.
- Bridge messaging now uses WebView2's message channel with an embedded preload script and one answer shape (`ok` / `status` / `data` or `error`), instead of building script text per request.
- Notifications / messages window: own title bar, no website header, footer, player bar or cookie banner, dark thin scrollbars, no browser menus.
- Header: no "+" button; the gear opens Settings directly (Add account, Open SoundCloud, Fix likes & sync are in the account menu). Liked hearts are orange.
- Song page: Details (collapsed, Show more), Comments (count, Newest / Oldest, time chips), and a side column with artist, counters, Related tracks (See all) and In playlists.

## Added in 1.4.2
- **Bell / envelope open the real soundcloud.com pages** (notifications, messages) in a small WebView2 window. It is created on open and released on close (shared low-memory profile, one renderer, no background networking). `Esc` closes it.
- **Blocked requests fall back to the web page**: if SoundCloud's bot protection refuses the plain client, reads and writes (likes, reposts, follows, history) go through the hidden soundcloud.com page, and the plain client is left alone for 10 minutes so the connection isn't flagged further.
- **... menu -> "Fix likes & sync"** opens soundcloud.com once so you can pass the check by hand; the clearance is stored and reused.
- **Playlist page** in the soundcloud.com style: tinted banner, cover, track badge, tags, private notice, owner column, numbered list.
- **Mouse side buttons** (and `Alt+Left` / `Alt+Right`) go back / forward through the tabs you visited; inside the web panel they use the page history.

## Build
1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Run `build.bat` -> `dist\KaruCloud.exe` (needs the .NET 8 Desktop Runtime on the PC that runs it),
   or `build-selfcontained.bat` if the target PC has no runtime.
3. The sign-in page needs the Microsoft Edge **WebView2 Runtime** (already part of Windows 11 and most Windows 10 installs).
   If it's missing, KaruCloud tells you and links the installer.

## How sign-in works
The sign-in window shows soundcloud.com's normal login / sign-up page. After you sign in, KaruCloud reads the session token SoundCloud
sets in that window, checks it, stores it (encrypted) and uses it the same way the SoundCloud website does. Your password is typed into
SoundCloud's page only; KaruCloud never sees it. "Sign out" in Settings just forgets the account in KaruCloud.

**Heads-up:** this is not SoundCloud's official developer API, so it can break whenever SoundCloud changes its website,
and it is outside SoundCloud's API terms. Use it for your own account, as a personal player, at your own risk.
The KaruCloud logo is based on SoundCloud-style artwork; swap `icon.png` / `icon.ico` before publishing the app anywhere.

## If something goes wrong
- Errors are written to `%APPDATA%\KaruCloud\crash.log`. Send me that file (or its last lines) if the app closes or misbehaves.
- Signed-out / "session expired": Settings -> Add another account (signing in with the same account again keeps its tabs).
- Like / repost / follow say `SoundCloud refused this (api: 403, api-v2: 403)`: KaruCloud tries SoundCloud's `api` host first and `api-v2` second, with the same headers the website sends.
  If both still refuse, sign in again from Settings (the session token may have expired) and send me the line from `crash.log`.
- **"SoundCloud wants a quick security check"**: SoundCloud's bot protection (DataDome) sometimes asks for a human check before it accepts a like / repost / comment.
  KaruCloud opens that check in a normal window once; finish it and the like goes through (the answer is remembered, so it rarely comes back). If you close the window instead, press the button again.
- Likes / reposts / follows / listening history: if SoundCloud's bot protection refuses the plain request, KaruCloud repeats it from a hidden soundcloud.com window
  (started on demand, closed after 25 idle seconds; plays are sent in small batches). Every play you start is added to your SoundCloud History.
- Playback errors: KaruCloud asks SoundCloud for a fresh copy of the track, then tries the full MP3 stream, the MP3 HLS stream and the AAC stream
  (30 s previews last). If every option fails, the player bar shows which ones SoundCloud refused (for example `mp3_0_0: 404`)
  and the same line is written to `crash.log` - send me that line.
- Discord presence: uses KaruCloud's shared Discord application (ID built in), nothing to set up. Needs the Discord desktop app open.
