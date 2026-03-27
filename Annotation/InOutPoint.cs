namespace Mesen.Annotation;

/// <summary>
/// Control-flow role flags for a ROM byte. Numeric values are identical to
/// DiztinGUIsh's InOutPoint enum and must never change — they are the wire
/// format for .diz/.dizraw import/export.
/// </summary>
[Flags]
public enum InOutPoint : byte
{
    None      = 0x00,
    InPoint   = 0x01,  // branch/jump target — something jumps here
    OutPoint  = 0x02,  // branch/jump source — this instruction jumps elsewhere
    EndPoint  = 0x04,  // unconditional exit (RTS, RTL, JMP, BRA, etc.)
    ReadPoint = 0x08,  // data read site — some instruction loads data from here
}
