// Draws captures on a mock monitor at the exact place they were on screen.
//   <div class="monitor" data-shots="edge-left-open" data-view="0,0,1920,1080" style="left;top;width"></div>
// data-shots: names from ../out/raw/screens.js (whole-window 3x renders), drawn in order; "name@0.4" sets opacity.
// data-view:  the part of the 1920x1080 screen to show (x,y,w,h in DIPs), so a corner can be shown up close.
// data-cursor: x,y (screen DIPs) for a pointer arrow.
// <script src="monitor.js" data-base="..."> sets where the captures are (the social posters live elsewhere).
const MONITOR_BASE = document.currentScript?.dataset.base || "../out/raw/";
document.addEventListener("DOMContentLoaded", () => {
  for (const m of document.querySelectorAll(".monitor")) {
    const [vx, vy, vw, vh] = (m.dataset.view || "0,0,1920,1080").split(",").map(Number);
    const scale = m.clientWidth / vw;
    m.style.height = vh * scale + "px";
    const d = document.createElement("div");
    d.className = "display";
    m.appendChild(d);
    const place = (el, x, y) => { el.style.left = (x - vx) * scale + "px"; el.style.top = (y - vy) * scale + "px"; };
    if (vy + vh > 1032) { // the taskbar, when the view reaches the bottom of the screen
      const t = document.createElement("div");
      t.className = "taskbar";
      place(t, 0, 1032);
      t.style.width = 1920 * scale + "px";
      t.style.height = 48 * scale + "px";
      d.appendChild(t);
    }
    for (const spec of (m.dataset.shots || "").split(",").filter(Boolean)) {
      const [name, opacity] = spec.trim().split("@");
      const s = (window.SCREENS || {})[name];
      if (!s) { console.warn("no capture", name); continue; }
      const img = document.createElement("img");
      img.src = `${MONITOR_BASE}${name}.png`;
      img.style.width = s[2] * scale + "px";
      if (opacity) img.style.opacity = opacity;
      place(img, s[0], s[1]);
      d.appendChild(img);
    }
    if (m.dataset.cursor) {
      const [cx, cy] = m.dataset.cursor.split(",").map(Number);
      const c = document.createElement("i");
      c.className = "cursor";
      place(c, cx, cy);
      c.style.width = Math.max(14, 22 * scale) + "px";
      c.style.height = Math.max(20, 32 * scale) + "px";
      d.appendChild(c);
    }
  }
});
