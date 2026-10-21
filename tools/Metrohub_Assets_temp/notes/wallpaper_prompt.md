Convert this image into a 192x108 animated halftone wallpaper in JavaScript for our desktop engine.

### Strict Rules:
- Grid: 192 cols x 108 rows (`W = 192, H = 108`), `cell: "square"`, `look: "dots"`, `fps: 15`.
- Duration: Exactly 6.0 seconds (`PERIOD = 6.0`) with 100.0% seamless harmonic loop.
- Halftone Rule: The background is dark. Daytime skies and bright areas must use LUMINOUS, vibrant colors (luminance > 0.55) so the halftone dots stay large and fill the space.
- Motion: Use only pure integer harmonic sines `Math.sin(omega * t)`. No Math.random() in draw(t).

### Skeleton:
import { fbm, noise2, clamp, lerp, smoothstep, rng, KEYS } from "./kit.js";

const W = 192, H = 108;
const PERIOD = 6.0;
const hex = (h) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16));
const mix = (a, b, t) => [lerp(a[0], b[0], t), lerp(a[1], b[1], t), lerp(a[2], b[2], t)];
const toHex = (c) => "#" + c.map((v) => Math.round(clamp(v, 0, 255)).toString(16).padStart(2, "0")).join("");

const PALETTE_HEX = [ /* 24-36 vibrant sampled hex colors */ ];
const PAL = PALETTE_HEX.map(hex);
const LEVELS = "▁▂▃▄▅▆▇█";
const LVK = [0.135, 0.257, 0.378, 0.51, 0.635, 0.757, 0.878, 1];

function encoder(PAL) {
  const LUM = PAL.map((c) => (0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]) / 255);
  const cache = new Map();
  const nearest = (r, g, b) => {
    const k = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
    let hit = cache.get(k);
    if (hit !== undefined) return hit;
    let best = 0, bd = 1e9;
    for (let i = 0; i < PAL.length; i++) {
      const p = PAL[i], d = (p[0] - r) ** 2 * 0.3 + (p[1] - g) ** 2 * 0.59 + (p[2] - b) ** 2 * 0.11;
      if (d < bd) { bd = d; best = i; }
    }
    cache.set(k, best);
    return best;
  };
  return (out) => {
    const text = [], color = [];
    for (let y = 0; y < H; y++) {
      let tr = "", cr = "";
      for (let x = 0; x < W; x++) {
        const i = (y * W + x) * 3, r = out[i], g = out[i + 1], b = out[i + 2];
        const k = nearest(r | 0, g | 0, b | 0), Y = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
        const want = LUM[k] > 0 ? Y / LUM[k] : 0;
        let lv = 7, bd = Infinity;
        for (let l = 2; l < 8; l++) { const d = Math.abs(LVK[l] - want); if (d < bd) { bd = d; lv = l; } }
        tr += LEVELS[lv]; cr += KEYS[k];
      }
      text.push(tr); color.push(cr);
    }
    return { text: text.join("\n"), color: color.join("\n") };
  };
}

function canvas() {
  const rgb = new Float32Array(W * H * 3);
  const put = (x, y, c) => {
    x |= 0; y |= 0; if (x < 0 || y < 0 || x >= W || y >= H) return;
    const i = (y * W + x) * 3; rgb[i] = c[0]; rgb[i + 1] = c[1]; rgb[i + 2] = c[2];
  };
  const add = (x, y, c, a) => {
    x |= 0; y |= 0; if (x < 0 || y < 0 || x >= W || y >= H || a <= 0) return;
    const i = (y * W + x) * 3;
    rgb[i] += (c[0] - rgb[i]) * a; rgb[i + 1] += (c[1] - rgb[i + 1]) * a; rgb[i + 2] += (c[2] - rgb[i + 2]) * a;
  };
  return { rgb, put, add };
}

export const meta = {
  slug: "<kebab-name>",
  name: "<Title>",
  cols: W, rows: H, fps: 15, cell: "square", look: "dots",
  dots: { tone: { gamma: 0.62, floor: 0.03, sat: 0.85 } },
  palette: Object.fromEntries(PAL.map((c, i) => [KEYS[i], toHex(c)])),
};

export default function create() {
  const C = canvas();
  // Paint static scene layers into C.rgb
  const base = C.rgb.slice();
  const out = C.rgb;
  const enc = encoder(PAL);
  const omega = (2 * Math.PI) / PERIOD;

  return (t) => {
    out.set(base);
    // Draw harmonic motion
    return enc(out);
  };
}