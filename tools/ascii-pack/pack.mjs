#!/usr/bin/env node
/*
 * MetroHub ASCII packer.
 *
 * Reads an ascii-magic piece (a JS function draw(t) -> { text, color }), samples
 * it at a fixed rate, and writes one compact .mhp file. MetroHub reads that file
 * with plain C#; no JavaScript ships with the app.
 *
 * Usage:
 *   node pack.mjs --list
 *   node pack.mjs --piece matrix-rain
 *   node pack.mjs --piece rainforest-falls-mist --seconds 6
 *   node pack.mjs --piece matrix-rain --fps 24 --out ..\..\src\MetroHub\Assets\AsciiArt
 *
 * Output goes to src/MetroHub/Assets/AsciiArt/<slug>.mhp by default.
 */

import { existsSync, mkdirSync, readdirSync, writeFileSync } from 'node:fs';
import { gzipSync, gunzipSync } from 'node:zlib';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, '..', '..');
const PKG = join(HERE, 'node_modules', 'ascii-magic-art');
const DEFAULT_OUT = join(REPO, 'src', 'MetroHub', 'Assets', 'AsciiArt');

const MAGIC = 'MHP1';
const VERSION = 1;
const TEXT_CELL_ASPECT = 1 / 0.602; // matches runtime/canvas.js CELL_W
const MIN_SECONDS = 4;
const MAX_SECONDS = 12;

// ---------------------------------------------------------------- arguments

function parseArgs(argv) {
  const a = { piece: null, seconds: 0, fps: 0, out: DEFAULT_OUT, list: false, help: false, preview: -1 };
  for (let i = 0; i < argv.length; i++) {
    const k = argv[i];
    if (k === '--piece' || k === '-p') a.piece = argv[++i];
    else if (k === '--seconds' || k === '-s') a.seconds = Number(argv[++i]);
    else if (k === '--fps') a.fps = Number(argv[++i]);
    else if (k === '--out' || k === '-o') a.out = resolve(argv[++i]);
    else if (k === '--list') a.list = true;
    else if (k === '--preview') a.preview = Number(argv[i + 1]) >= 0 && !String(argv[i + 1]).startsWith('--') ? Number(argv[++i]) : 0;
    else if (k === '--help' || k === '-h') a.help = true;
    else throw new Error(`Unknown argument: ${k}`);
  }
  return a;
}

// ---------------------------------------------------------------- byte writer

class ByteWriter {
  constructor(cap = 1 << 16) {
    this.buf = new Uint8Array(cap);
    this.len = 0;
  }

  ensure(n) {
    const need = this.len + n;
    if (need <= this.buf.length) return;
    let cap = this.buf.length;
    while (cap < need) cap *= 2;
    const next = new Uint8Array(cap);
    next.set(this.buf.subarray(0, this.len));
    this.buf = next;
  }

  u8(v) {
    this.ensure(1);
    this.buf[this.len++] = v & 0xff;
  }

  u16(v) {
    this.ensure(2);
    this.buf[this.len++] = v & 0xff;
    this.buf[this.len++] = (v >> 8) & 0xff;
  }

  u32(v) {
    this.ensure(4);
    this.buf[this.len++] = v & 0xff;
    this.buf[this.len++] = (v >> 8) & 0xff;
    this.buf[this.len++] = (v >> 16) & 0xff;
    this.buf[this.len++] = (v >>> 24) & 0xff;
  }

  bytes(src) {
    this.ensure(src.length);
    this.buf.set(src, this.len);
    this.len += src.length;
  }

  str(s) {
    const b = new TextEncoder().encode(s);
    if (b.length > 255) throw new Error(`Text too long for header: ${s}`);
    this.u8(b.length);
    this.bytes(b);
  }

  done() {
    return this.buf.subarray(0, this.len);
  }
}

// ---------------------------------------------------------------- piece loading

async function loadPiece(slug) {
  const file = join(PKG, 'pieces', `${slug}.js`);
  if (!existsSync(file)) throw new Error(`No piece "${slug}". Use --piece <slug> or --list.`);
  const mod = await import(pathToFileURL(file).href);
  const meta = mod.meta ?? {};
  if (!meta.cols || !meta.rows) throw new Error(`${slug}: meta.cols/meta.rows missing`);
  if (meta.cols * meta.rows > 65535) throw new Error(`${slug}: grid is too large for the format`);
  if (typeof mod.default !== 'function') throw new Error(`${slug}: no default create() export`);
  return { meta, draw: mod.default() };
}

// ---------------------------------------------------------------- frame to ids

function makeTables(meta) {
  return {
    meta,
    glyphIds: new Map(),
    glyphs: [],
    colorIds: new Map(),
    colorList: [],
  };
}

function frameToIds(frame, st, slug) {
  const { cols, rows } = st.meta;
  const text = typeof frame === 'string' ? frame : frame?.text;
  const color = typeof frame === 'string' ? null : frame?.color;
  if (typeof text !== 'string') throw new Error(`${slug}: frame has no text`);

  const T = text.split('\n');
  const C = typeof color === 'string' ? color.split('\n') : null;
  if (T.length !== rows) throw new Error(`${slug}: expected ${rows} rows, got ${T.length}`);
  if (C && C.length !== rows) throw new Error(`${slug}: expected ${rows} color rows, got ${C.length}`);

  const cells = cols * rows;
  const g = new Uint8Array(cells);
  const c = new Uint8Array(cells);
  let o = 0;

  for (let y = 0; y < rows; y++) {
    const chars = Array.from(T[y]);
    if (chars.length !== cols) {
      throw new Error(`${slug}: row ${y} has ${chars.length} cells, expected ${cols}`);
    }
    const crow = C ? C[y] : '';
    for (let x = 0; x < cols; x++) {
      const cp = chars[x].codePointAt(0);
      if (cp > 0xffff) throw new Error(`${slug}: non-BMP glyph at ${x},${y}`);

      let gi = st.glyphIds.get(cp);
      if (gi === undefined) {
        gi = st.glyphs.length;
        if (gi > 255) throw new Error(`${slug}: more than 256 different glyphs`);
        st.glyphIds.set(cp, gi);
        st.glyphs.push(cp);
      }
      g[o] = gi;

      let ci = 0;
      const key = crow ? crow[x] : ' ';
      if (key && key !== ' ') {
        const kp = key.codePointAt(0);
        let id = st.colorIds.get(kp);
        if (id === undefined) {
          id = st.colorList.length + 1; // 0 is the default ink
          if (id > 255) throw new Error(`${slug}: more than 255 palette colors`);
          st.colorIds.set(kp, id);
          st.colorList.push(kp);
        }
        ci = id;
      }
      c[o] = ci;
      o++;
    }
  }

  return { g, c };
}

// ---------------------------------------------------------------- loop choice

function seamScore(a, b) {
  const n = a.g.length;
  let same = 0;
  for (let i = 0; i < n; i++) if (a.g[i] === b.g[i] && a.c[i] === b.c[i]) same++;
  return same / n;
}

function chooseLoop(frames, fps, wantedSeconds) {
  if (wantedSeconds > 0) {
    const n = Math.max(1, Math.round(wantedSeconds * fps));
    if (n >= frames.length) throw new Error(`--seconds ${wantedSeconds} is longer than the sampled range`);
    return { count: n, score: seamScore(frames[n], frames[0]) };
  }

  const lo = Math.round(MIN_SECONDS * fps);
  const hi = Math.min(frames.length - 1, Math.round(MAX_SECONDS * fps));
  let best = { count: lo, score: -1 };
  for (let n = lo; n <= hi; n++) {
    const score = seamScore(frames[n], frames[0]);
    if (score > best.score + 1e-9) best = { count: n, score };
  }
  return best;
}

// ---------------------------------------------------------------- encode

function encodeBody(frames, cells) {
  const w = new ByteWriter(1 << 20);
  const maskLen = (cells + 7) >> 3;

  for (let f = 0; f < frames.length; f++) {
    const { g, c } = frames[f];

    if (f === 0) {
      w.u8(1); // full frame
      for (let i = 0; i < cells; i++) {
        w.u8(g[i]);
        w.u8(c[i]);
      }
      continue;
    }

    const pg = frames[f - 1].g;
    const pc = frames[f - 1].c;

    w.u8(0); // delta frame
    const maskStart = w.len;
    w.ensure(maskLen);
    w.buf.fill(0, maskStart, maskStart + maskLen);
    w.len = maskStart + maskLen;

    let changed = 0;
    for (let i = 0; i < cells; i++) {
      if (g[i] !== pg[i] || c[i] !== pc[i]) {
        w.buf[maskStart + (i >> 3)] |= 1 << (i & 7);
        changed++;
      }
    }

    for (let i = 0; i < cells; i++) {
      if (w.buf[maskStart + (i >> 3)] & (1 << (i & 7))) {
        w.u8(g[i]);
        w.u8(c[i]);
      }
    }

    if (process.env.MHP_DEBUG) console.error(`  enc frame ${f}: changed=${changed}`);

    if (changed === 0) {
      // all-zero mask: still a valid frame, costs only maskLen bytes
    }
  }

  return w.done();
}

function hexToRgb(hex, slug, key) {
  if (typeof hex !== 'string' || !/^#[0-9a-fA-F]{6}$/.test(hex)) {
    throw new Error(`${slug}: palette["${key}"] is not #rrggbb: ${hex}`);
  }
  return [parseInt(hex.slice(1, 3), 16), parseInt(hex.slice(3, 5), 16), parseInt(hex.slice(5, 7), 16)];
}

function buildFile(slug, name, st, fps, frames, bodyRaw) {
  const { meta } = st;
  const w = new ByteWriter(1024);

  for (const ch of MAGIC) w.u8(ch.charCodeAt(0));
  w.u16(VERSION);
  w.u16(meta.cols);
  w.u16(meta.rows);
  w.u16(fps);
  w.u16(frames.length);
  w.u16(meta.cell === 'square' ? 1000 : Math.round(TEXT_CELL_ASPECT * 1000));

  w.u16(st.colorList.length);
  for (const kp of st.colorList) {
    const key = String.fromCodePoint(kp);
    const hex = meta.palette?.[key];
    const [r, g, b] = hexToRgb(hex, slug, key);
    w.u8(r);
    w.u8(g);
    w.u8(b);
  }

  w.u16(st.glyphs.length);
  for (const cp of st.glyphs) w.u16(cp);

  w.str(slug);
  w.str(name);

  const gz = gzipSync(bodyRaw, { level: 9 });
  w.u32(bodyRaw.length);
  w.u32(gz.length);
  w.bytes(gz);

  return w.done();
}

// ---------------------------------------------------------------- verify

function readU16(b, o) {
  return b[o] | (b[o + 1] << 8);
}

function readU32(b, o) {
  return (b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)) >>> 0;
}

function decode(buf, gunzip) {
  if (String.fromCharCode(buf[0], buf[1], buf[2], buf[3]) !== MAGIC) throw new Error('bad magic');
  let o = 4;
  const version = readU16(buf, o); o += 2;
  if (version !== VERSION) throw new Error(`bad version ${version}`);
  const cols = readU16(buf, o); o += 2;
  const rows = readU16(buf, o); o += 2;
  const fps = readU16(buf, o); o += 2;
  const count = readU16(buf, o); o += 2;
  const aspect = readU16(buf, o); o += 2;
  const paletteCount = readU16(buf, o); o += 2;

  const palette = [];
  for (let i = 0; i < paletteCount; i++) {
    palette.push([buf[o], buf[o + 1], buf[o + 2]]);
    o += 3;
  }

  const glyphCount = readU16(buf, o); o += 2;
  const glyphs = [];
  for (let i = 0; i < glyphCount; i++) {
    glyphs.push(readU16(buf, o));
    o += 2;
  }

  const slugLen = buf[o++];
  const slug = new TextDecoder().decode(buf.subarray(o, o + slugLen)); o += slugLen;
  const nameLen = buf[o++];
  const name = new TextDecoder().decode(buf.subarray(o, o + nameLen)); o += nameLen;

  const rawLen = readU32(buf, o); o += 4;
  const gzLen = readU32(buf, o); o += 4;
  const body = gunzip(buf.subarray(o, o + gzLen));
  if (body.length !== rawLen) throw new Error(`body length ${body.length} != ${rawLen}`);
  if (o + gzLen !== buf.length) throw new Error('trailing bytes');

  const cells = cols * rows;
  const maskLen = (cells + 7) >> 3;
  const frames = [];
  let prev = null;
  let p = 0;

  for (let f = 0; f < count; f++) {
    const full = body[p++];
    const g = new Uint8Array(cells);
    const c = new Uint8Array(cells);

    if (full === 1) {
      for (let i = 0; i < cells; i++) {
        g[i] = body[p++];
        c[i] = body[p++];
      }
    } else if (full === 0) {
      if (!prev) throw new Error('first frame is a delta');
      g.set(prev.g);
      c.set(prev.c);
      const mask = body.subarray(p, p + maskLen);
      p += maskLen;
      for (let i = 0; i < cells; i++) {
        if (mask[i >> 3] & (1 << (i & 7))) {
          g[i] = body[p++];
          c[i] = body[p++];
        }
      }
    } else {
      throw new Error(`bad frame flag ${full} at frame ${f}`);
    }

    prev = { g, c };
    frames.push(prev);
  }
  if (p !== body.length) throw new Error(`body has ${body.length - p} unused bytes`);

  return { cols, rows, fps, count, aspect, palette, glyphs, slug, name, frames };
}

function verify(buf, source, st, raw, slug) {  const d = decode(buf, gunzipSync);
  if (d.cols !== st.meta.cols || d.rows !== st.meta.rows) throw new Error('grid mismatch');
  if (d.count !== source.length) throw new Error('frame count mismatch');
  if (d.glyphs.length !== st.glyphs.length) throw new Error('glyph table mismatch');
  for (let i = 0; i < d.glyphs.length; i++) {
    if (d.glyphs[i] !== st.glyphs[i]) throw new Error(`glyph ${i} mismatch`);
  }

  let cellsChecked = 0;
  for (let f = 0; f < d.count; f++) {
    const src = source[f];
    const got = d.frames[f];
    for (let i = 0; i < src.g.length; i++) {
      if (src.g[i] !== got.g[i] || src.c[i] !== got.c[i]) {
        throw new Error(`${slug}: cell ${i} differs in frame ${f}`);
      }
      cellsChecked++;
    }

    // Strongest check: rebuild the text and compare with the original string.
    const rows = [];
    const cro = [];
    for (let y = 0; y < d.rows; y++) {
      let tr = '';
      let cr = '';
      for (let x = 0; x < d.cols; x++) {
        const i = y * d.cols + x;
        tr += String.fromCodePoint(d.glyphs[got.g[i]]);
        cr += got.c[i] === 0 ? ' ' : String.fromCodePoint(st.colorList[got.c[i] - 1]);
      }
      rows.push(tr);
      cro.push(cr);
    }
    if (rows.join('\n') !== raw.text[f]) throw new Error(`${slug}: text differs in frame ${f}`);
    if (raw.color[f] !== null && cro.join('\n') !== raw.color[f]) {
      throw new Error(`${slug}: color differs in frame ${f}`);
    }
  }

  return { frames: d.count, cellsChecked, data: d };
}

// ---------------------------------------------------------------- main

function kb(n) {
  return `${(n / 1024).toFixed(1)} KB`;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));

  if (args.help) {
    console.log('node pack.mjs --piece <slug> [--seconds N] [--fps N] [--out DIR] [--preview [frame]]');
    console.log('node pack.mjs --list');
    return;
  }

  if (args.list) {
    const files = readdirSync(join(PKG, 'pieces')).filter((f) => f.endsWith('.js')).sort();
    console.log(`${files.length} pieces in ascii-magic-art:`);
    for (const f of files) console.log('  ' + f.replace(/\.js$/, ''));
    return;
  }

  if (!args.piece) throw new Error('Missing --piece <slug>. Use --list to see all pieces.');

  const { meta, draw } = await loadPiece(args.piece);
  const slug = meta.slug ?? args.piece;
  const name = meta.name ?? slug;
  const fps = args.fps > 0 ? args.fps : meta.fps || 24;
  const cells = meta.cols * meta.rows;

  const sampleCount = args.seconds > 0
    ? Math.round(args.seconds * fps) + 1
    : Math.round(MAX_SECONDS * fps) + 1;

  const st = makeTables(meta);
  const frames = [];
  const raw = { text: [], color: [] };

  for (let n = 0; n < sampleCount; n++) {
    const frame = draw(n / fps);
    const text = typeof frame === 'string' ? frame : frame?.text;
    const color = typeof frame === 'string' ? null : frame?.color;
    if (typeof text !== 'string') throw new Error(`${slug}: draw(${n / fps}) returned no text`);
    raw.text.push(text);
    raw.color.push(typeof color === 'string' ? color : null);
    frames.push(frameToIds(frame, st, slug));
  }

  const loop = chooseLoop(frames, fps, args.seconds);
  const kept = frames.slice(0, loop.count);

  const body = encodeBody(kept, cells);
  const file = buildFile(slug, name, st, fps, kept, body);
  const check = verify(file, kept, st, raw, slug);

  if (args.preview >= 0) {
    const d = check.data;
    const f = d.frames[Math.min(args.preview, d.frames.length - 1)];
    console.log('');
    for (let y = 0; y < d.rows; y++) {
      let row = '';
      for (let x = 0; x < d.cols; x++) row += String.fromCodePoint(d.glyphs[f.g[y * d.cols + x]]);
      console.log(row.replace(/\s+$/, ''));
    }
  }

  mkdirSync(args.out, { recursive: true });
  const outFile = join(args.out, `${slug}.mhp`);
  writeFileSync(outFile, file);

  const gz = gzipSync(body, { level: 9 });

  console.log('');
  console.log(`piece      ${slug} (${name})`);
  console.log(`grid       ${meta.cols} x ${meta.rows} = ${cells} cells, ${meta.cell === 'square' ? 'square' : 'tall'} cells`);
  console.log(`rate       ${fps} fps`);
  console.log(`frames     ${loop.count} (${(loop.count / fps).toFixed(2)} s), loop seam ${(loop.score * 100).toFixed(1)}% of cells identical`);
  console.log(`glyphs     ${st.glyphs.length}, colors ${st.colorList.length}`);
  console.log(`body       ${kb(body.length)} raw, ${kb(gz.length)} gzip`);
  console.log(`file       ${kb(file.length)}`);
  console.log(`output     ${outFile}`);
  console.log(`verify     OK: ${check.frames} frames, ${check.cellsChecked.toLocaleString('en-US')} cells, 0 mismatches`);
  console.log('');
}

main().catch((e) => {
  console.error(`\npack failed: ${e.message}\n`);
  process.exitCode = 1;
});
