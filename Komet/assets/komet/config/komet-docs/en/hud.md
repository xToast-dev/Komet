# HUD and measuring

The HUD is Komet's measuring display. It shows how smoothly the game runs and tells you what slows it down. It only measures while the overlay is on, the HUD window is open, or a capture or a benchmark is running. The page "Glossary" explains words such as frame time, spike or GC.

## The three windows

| Key | What opens |
|---|---|
| F7 | The overlay: a small display in one corner of the screen |
| Ctrl+F7 | The HUD window, with tabs for frames, system, render and more |
| Ctrl+F8 | The debug window: captures and the debug protocol |

F7 turns the overlay on and off, and the chat confirms it. You change the keys under Vintage Story › Controls. There they are called "Komet HUD", "Komet HUD window" and "Komet debug window".

You drag a window by its title bar. Komet remembers its position and the tab you last had open. Komet › Tools › **Panel positions** resets the positions (click twice).

## The overlay

The overlay shows:

- **FPS**: frames per second.
- **1% low**: the FPS of the slowest 1 % of frames. This number shows how bad the slow moments are.
- **ms**: how long a frame takes on average (the frame time).
- a graph: the frame time of the last 240 frames. Each peak is a slow frame.
- **Mods total**: how much time the code of all mods takes per frame, not counting the game itself.

FPS and frame time cover the last short stretch (Komet › HUD › **Refresh interval**, 250 ms by default). Komet works out the 1 % low from the last 2000 counted frames. It appears after 100 frames, and the 0.1 % low on the **Frames** tab after 1000.

While a debug capture runs, the overlay shows **REC** with the seconds at its top right. While a capture waits for a spike, it shows **armed** there.

### The stutter hint

When a frame is a spike and reaches the value of Komet › HUD › **Stutter hint from** (50 ms by default), a card appears for 5 seconds below the overlay (above it in a bottom corner). It names the length, the likely cause and the spike's number. So that nothing flickers, a card comes at most every 3 seconds. The card only appears while the overlay is on.

### Which frames count

Komet only counts frames in which you play normally. These are not counted:

- the first 5 seconds in a world,
- the time while the first chunks still load (30 seconds at most),
- frames while the game is paused,
- about one second after you switch the overlay on, or open, close or switch pages in the options menu or the HUD window.

This way, loading and menus do not spoil the lows.

## Spikes and causes

A spike is a frame that takes much longer than the others. Komet counts a frame as a spike when it takes longer than 25 ms and also longer than 2.5 times the running average. At 60 FPS (16.7 ms) the limit is therefore about 42 ms, at 120 FPS it is 25 ms.

For each spike, Komet looks for the cause:

| Cause | What it means |
|---|---|
| GC pause | Garbage collection paused the game. |
| JIT compilation | The game translated code it needed for the first time. |
| waiting for a CPU core | Other work kept the cores busy, so the game had to wait. Komet measures this on Linux only. |
| between frames (input, events) | Work outside drawing, such as input or events. |
| another name | The game's most expensive step in that frame, as the game's frame profiler names it. |
| not profiled | Komet could not tie the frame to any step. |

GC pause, JIT and waiting for a core win when one of them alone explains at least half of the time above the average. Otherwise Komet names the most expensive step, or "between frames" when that time was larger.

### Tracking down a stutter

```flow
F7: overlay on, play normally
A stutter: the card names the cause
Ctrl+F7 › Frames: the latest spikes
Ctrl+F8 › On a spike: capture the next one
Read the protocol or report it
```

## The HUD window

The tabs of the HUD window (Ctrl+F7):

- **Overview**: FPS, 1 % low, frame time and GPU time, a graph and the hints (see below).
- **Frames**: also p99 (the frame time that 99 % of frames stay under) and the 0.1 % low, a large graph with lines at 60 and 30 FPS, how the frame times are spread, and the last five spikes with their cause.
- **System**: CPU of the game and of the whole computer, RAM, video memory, GC pauses, loaded chunks and entities (creatures, players, dropped items), view distance.
- **Render**: which renderer draws, where the time of a frame goes, GPU time, draw calls, triangles and how much is left out as hidden. Under Vulkan also what Vulkan does not draw.
- **Threads**: how busy the main thread and Komet's worker threads are, plus thread pool and lock contentions. Some values exist on Linux only.
- **Log**: the newest entries from `client-main.log`, which you can filter to warnings or errors. A click opens an entry in full.

At the top right, **Debug** opens the debug window. The gear shows the HUD settings: what the overlay shows, position, opacity, font size, spike hints and your keys. A click on the version badge next to the title shows your version and the checksum, with the button **Check now**.

### Hints and what helps

On the overview, "Hints and what helps" lists what stands out right now. If there were spikes, the latest one is at the top; **look** leads to the **Frames** tab. Many hints have a button that helps with one click. A hint goes away as soon as its cause has gone. When nothing stands out, it says "All in the green".

| Hint | Button | What the button does |
|---|---|---|
| Full collections may stop the game | Apply | switches on Komet › Other › **Clean up in the background** |
| The garbage collector holds the game X % of the time (from 5 %) | Record | starts a 10 s recording |
| The main thread waits X ms per frame for a core (from 0.5 ms, Linux only, with priority above 25 %) | Apply | sets Komet › Chunks › **Priority: loading chunks** to 25 % |
| X risky mod conflicts | none | the hint names the first method and its mods; the debug protocol lists them all |
| A mod costs X ms per frame (from 2 ms) | Record | starts a recording of 10 s; the protocol shows which of the mod's calls take the time |
| Video memory X % full (from 90 %) | none | a lower view distance helps |
| 1% low at X fps (below 30) | Arm | waits for the next spike and captures it |
| X Komet features stood down or failed | look | makes a debug protocol (see "Troubleshooting") |
| Newer build on GitHub | look | shows the version and checksum |
| The installed file differs from the published checksum | look | shows the version and checksum |

Below the hints, **Make a debug protocol** opens the debug window.

## The debug window

The debug window (Ctrl+F8) writes the debug protocol: a plain-text report of everything Komet measures. During a capture every measurement is on, even when the HUD is hidden.

| Kind | How long | What for |
|---|---|---|
| Now | 3 s | a quick look at the state right now |
| Recording | 10 s, 30 s, 60 s or 10 min | problems that keep coming back |
| On a spike | waits, then keeps 20 s before and 2 s after | rare stutters |

- For **Recording**, 30 s is preselected. **Stop** ends it early.
- For **On a spike**, you pick the limit: over 25, 50 or 100 ms (50 ms preselected). **Cancel** ends the wait; the protocol then says that no frame crossed the limit.
- **Record each join** records the first minute of every world you join.
- The button on the right starts the chosen kind: **Make protocol**, **Start recording** or **Arm**.

### The protocol

When the capture is done, the protocol appears in the window and is in your clipboard. The chat says which folder it is in.

- At the top you pick the language: **English** or **Deutsch**. English is the default for bug reports. Switching shows the last protocol in the other language and copies it again. The file stays in the old language.
- **Copy all** copies it once more.
- **Jump to** takes you to a section. After its head lines, the protocol starts with the **Findings**: what stands out, with the level HIGH, MEDIUM, LOW or INFO.
- If there was a protocol before, Komet compares the two: frame time, 1 % low, worst frame, share over 50 ms, GC and allocation. What got better shows in green, what got worse in red.

A comparison is only fair with:

- the same kind, view distance and window size, and the same renderer,
- captures of 10 s or more (a **Now** is too short),
- a similar view (about as many triangles drawn),
- a world where no chunks are loading any more.

When something does not match, Komet writes "Not comparable" and gives the reason. How to report a protocol is on the page "Troubleshooting".

## HUD settings

The page Komet › HUD:

| Row | What it does |
|---|---|
| **Show HUD (F7)** | overlay on or off |
| **Layout** | the corner: Top left, Top right, Bottom left or Bottom right |
| **Opacity** | how solid the overlay's background is, 0 to 100 % (60 % by default) |
| **Font size** | 50 to 200 % (100 % by default), for the overlay and the windows |
| **Refresh interval** | how often the numbers update, 100 to 1000 ms (250 ms by default) |
| **FPS in overlay** to **Mods in overlay** | what the overlay shows, one switch per row |
| **Stutter hint from** | the frame time from which the card appears, 0 to 200 ms (50 ms by default, 0 turns it off) |
| **HUD window (Ctrl+F7)**, **Debug window (Ctrl+F8)** | **Open** opens the window |

The most important of these are also in the HUD window, behind the gear.

## Benchmark

Under Komet › Tools:

- **Benchmark length**: 5 to 120 s, 30 s by default.
- **Benchmark**: the button (for example "Run 30 s") measures everything the HUD measures for that length of time, even with the HUD hidden. Afterwards, the averages and the latest spikes are in your clipboard and in `client-main.log`. The chat tells you.
- **Current values**: **Copy to clipboard** makes a debug protocol of the kind **Now**.

A benchmark and a debug capture never run at the same time. Only compare measurements taken at the same spot, with the same view distance and the same renderer.
