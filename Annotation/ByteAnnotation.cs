namespace Mesen.Annotation;

/// <summary>
/// Immutable per-byte annotation record for a single ROM address.
/// Stored as a value type so large arrays of these are cache-friendly
/// and allocation-free to read.
///
/// DirectPage is ushort here (matching the 16-bit SNES D register).
/// DiztinGUIsh stores it as int — we cast on import/export with no data loss
/// since valid values are 0x0000–0xFFFF.
/// </summary>
public readonly struct ByteAnnotation : IEquatable<ByteAnnotation>
{
    public ByteType   Type        { get; init; }
    public bool       MFlag       { get; init; }  // accumulator 8-bit mode
    public bool       XFlag       { get; init; }  // index register 8-bit mode
    public byte       DataBank    { get; init; }  // B register
    public ushort     DirectPage  { get; init; }  // D register
    public InOutPoint Flow        { get; init; }
    public CpuArch    Arch        { get; init; }

    /// <summary>All-zero / all-default annotation: Unreached, no flags, Cpu65C816.</summary>
    public static readonly ByteAnnotation Default = default;

    // ── With* mutators ────────────────────────────────────────────────────────

    public ByteAnnotation WithType(ByteType value)           => this with { Type       = value };
    public ByteAnnotation WithMFlag(bool value)              => this with { MFlag      = value };
    public ByteAnnotation WithXFlag(bool value)              => this with { XFlag      = value };
    public ByteAnnotation WithDataBank(byte value)           => this with { DataBank   = value };
    public ByteAnnotation WithDirectPage(ushort value)       => this with { DirectPage = value };
    public ByteAnnotation WithFlow(InOutPoint value)         => this with { Flow       = value };
    public ByteAnnotation WithArch(CpuArch value)            => this with { Arch       = value };

    // ── Equality ──────────────────────────────────────────────────────────────

    public bool Equals(ByteAnnotation other) =>
        Type       == other.Type       &&
        MFlag      == other.MFlag      &&
        XFlag      == other.XFlag      &&
        DataBank   == other.DataBank   &&
        DirectPage == other.DirectPage &&
        Flow       == other.Flow       &&
        Arch       == other.Arch;

    public override bool Equals(object? obj) =>
        obj is ByteAnnotation other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine((int)Type, MFlag, XFlag, DataBank, DirectPage, (int)Flow, (int)Arch);

    public static bool operator ==(ByteAnnotation left, ByteAnnotation right) => left.Equals(right);
    public static bool operator !=(ByteAnnotation left, ByteAnnotation right) => !left.Equals(right);
}
