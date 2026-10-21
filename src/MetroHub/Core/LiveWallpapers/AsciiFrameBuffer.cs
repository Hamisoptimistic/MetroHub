using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace MetroHub.Core.LiveWallpapers;

public interface IAsciiWallpaperScene : IDisposable
{
    string SceneId { get; }
    string DisplayName { get; }
    void Initialize(int columns, int rows, AsciiGlyphLut glyphLut);
    void Update(float dt);
    void Render(AsciiFrameBuffer buffer);
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
public readonly record struct AsciiCell(ushort GlyphIndex, byte Shade, byte Palette)
{
    public bool IsVisible => Shade > 0 && GlyphIndex != 0;
}

public sealed class AsciiGlyphLut
{
    private readonly ushort[] _asciiToGlyph = new ushort[128];
    public GlyphTypeface Typeface { get; }
    public ushort SpaceGlyph { get; }
    public ushort[] LuminanceRampGlyphs { get; }

    // Fast pre-resolved Unicode Glyphs
    public ushort LightShade { get; }  // ░ (U+2591)
    public ushort MediumShade { get; } // ▒ (U+2592)
    public ushort DarkShade { get; }   // ▓ (U+2593)
    public ushort FullBlock { get; }   // █ (U+2588)
    public ushort LowerHalf { get; }   // ▄ (U+2584)
    public ushort UpperHalf { get; }   // ▀ (U+2580)
    public ushort BoxDiagUp { get; }   // ╱ (U+2571)
    public ushort BoxDiagDown { get; } // ╲ (U+2572)
    public ushort BoxVert { get; }     // │ (U+2502)
    public ushort BoxHoriz { get; }    // ─ (U+2500)
    public ushort DotMid { get; }      // · (U+00B7)
    public ushort Bullet { get; }      // • (U+2022)
    public ushort Circle { get; }      // ● (U+25CF)
    public ushort TriangleUp { get; }  // ▲ (U+25B2)

    private static ReadOnlySpan<char> RampChars => " .:-=+*#%@$&8BMW";

    public AsciiGlyphLut(GlyphTypeface typeface)
    {
        Typeface = typeface;
        typeface.CharacterToGlyphMap.TryGetValue(' ', out ushort space);
        SpaceGlyph = space;

        for (int c = 32; c < 127; c++)
        {
            _asciiToGlyph[c] = typeface.CharacterToGlyphMap.TryGetValue(c, out ushort g) && c != ' '
                ? g
                : (ushort)0;
        }

        LightShade  = ResolveUnicode('░', ':');
        MediumShade = ResolveUnicode('▒', '%');
        DarkShade   = ResolveUnicode('▓', '#');
        FullBlock   = ResolveUnicode('█', '@');
        LowerHalf   = ResolveUnicode('▄', '_');
        UpperHalf   = ResolveUnicode('▀', '^');
        BoxDiagUp   = ResolveUnicode('╱', '/');
        BoxDiagDown = ResolveUnicode('╲', '\\');
        BoxVert     = ResolveUnicode('│', '|');
        BoxHoriz    = ResolveUnicode('─', '-');
        DotMid      = ResolveUnicode('·', '.');
        Bullet      = ResolveUnicode('•', '*');
        Circle      = ResolveUnicode('●', 'O');
        TriangleUp  = ResolveUnicode('▲', '^');

        LuminanceRampGlyphs = new ushort[16];
        for (int i = 0; i < 16; i++)
        {
            LuminanceRampGlyphs[i] = this[RampChars[i]];
        }
    }

    private ushort ResolveUnicode(char unicodeChar, char asciiFallback)
    {
        if (Typeface.CharacterToGlyphMap.TryGetValue(unicodeChar, out ushort g) && g != 0)
            return g;
        return _asciiToGlyph[asciiFallback];
    }

    public ushort this[char c]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)c < 128u) return _asciiToGlyph[c];
            return c switch
            {
                '░' => LightShade,
                '▒' => MediumShade,
                '▓' => DarkShade,
                '█' => FullBlock,
                '▄' => LowerHalf,
                '▀' => UpperHalf,
                '╱' => BoxDiagUp,
                '╲' => BoxDiagDown,
                '│' => BoxVert,
                '─' => BoxHoriz,
                '·' => DotMid,
                '•' => Bullet,
                '●' => Circle,
                '▲' => TriangleUp,
                '・' => DotMid,
                '｜' => BoxVert,
                '¦' => BoxVert,
                _   => _asciiToGlyph['*']
            };
        }
    }
}

public sealed class AsciiFrameBuffer
{
    public const int DefaultColumns = 160;
    public const int DefaultRows = 60;
    public const int ShadeLevels = 16; // 0 = transparent, 1..15 = active shades
    public const int MaxPalettes = 4;

    public int Columns { get; }
    public int Rows { get; }
    private readonly AsciiCell[] _frontCells;
    private readonly AsciiCell[] _backCells;
    private readonly ulong[] _rowDirtyBits;

    public AsciiFrameBuffer(int columns = DefaultColumns, int rows = DefaultRows)
    {
        Columns = columns;
        Rows = rows;
        _frontCells = GC.AllocateUninitializedArray<AsciiCell>(columns * rows, pinned: true);
        _backCells = GC.AllocateUninitializedArray<AsciiCell>(columns * rows, pinned: true);
        _rowDirtyBits = new ulong[(rows + 63) >> 6];
        Array.Clear(_frontCells);
        Array.Clear(_backCells);
        MarkAllDirty();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear() => Array.Clear(_backCells);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetCell(int x, int y, ushort glyphIndex, byte shade, byte palette = 0)
    {
        if ((uint)x >= (uint)Columns || (uint)y >= (uint)Rows) return;

        byte clampedShade = (byte)Math.Clamp((int)shade, 0, ShadeLevels - 1);
        if (clampedShade == 0) glyphIndex = 0;

        _backCells[(y * Columns) + x] = new AsciiCell(glyphIndex, clampedShade, (byte)(palette & 3));
    }

    /// <summary>
    /// Diffs the back buffer against the front buffer per row using vectorized Span comparison.
    /// Marks a row dirty ONLY if its final rendered state differs from the previous frame.
    /// </summary>
    public bool CommitFrame()
    {
        bool anyDirty = false;
        Span<AsciiCell> front = _frontCells.AsSpan();
        ReadOnlySpan<AsciiCell> back = _backCells.AsSpan();

        for (int r = 0; r < Rows; r++)
        {
            int offset = r * Columns;
            Span<AsciiCell> frontRow = front.Slice(offset, Columns);
            ReadOnlySpan<AsciiCell> backRow = back.Slice(offset, Columns);

            if (!MemoryMarshal.Cast<AsciiCell, uint>(frontRow)
                .SequenceEqual(MemoryMarshal.Cast<AsciiCell, uint>(backRow)))
            {
                backRow.CopyTo(frontRow);
                _rowDirtyBits[r >> 6] |= 1UL << (r & 63);
                anyDirty = true;
            }
        }

        return anyDirty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<AsciiCell> GetRowSpan(int y) => _frontCells.AsSpan(y * Columns, Columns);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsRowDirty(int y) => (_rowDirtyBits[y >> 6] & (1UL << (y & 63))) != 0;

    public void ClearDirtyFlags() => Array.Clear(_rowDirtyBits);

    public void MarkAllDirty() => Array.Fill(_rowDirtyBits, ulong.MaxValue);
}
