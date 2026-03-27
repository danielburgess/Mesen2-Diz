using System.Xml.Linq;

namespace Mesen.Annotation.Diz;

/// <summary>
/// Parses a DiztinGUIsh .dizraw XML string into a <see cref="RomAnnotationStore"/>.
/// Uses System.Xml.Linq — no third-party dependencies.
/// </summary>
public static class DizProjectImporter
{
    // Namespace URIs embedded in every .dizraw file by ExtendedXmlSerializer.
    private static readonly XNamespace SysNs =
        "https://extendedxmlserializer.github.io/system";
    private static readonly XName SysItem = SysNs + "Item";

    private const int MinSaveVersion = 100;
    private const int MaxSaveVersion = 104;

    /// <summary>
    /// Parse a .dizraw XML string and return its annotation data.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Thrown for invalid watermark, out-of-range SaveVersion, or missing
    /// required elements/attributes.
    /// </exception>
    public static RomAnnotationStore Import(string dizRawXml)
    {
        var doc  = XDocument.Parse(dizRawXml);
        var root = doc.Root ?? throw new InvalidDataException("XML document has no root element.");

        ValidateRoot(root);

        var saveVersion = (int)root.Attribute("SaveVersion")!;
        var project     = RequireChild(root, "Project");
        var data        = RequireChild(project, "Data");

        var romGameName = ((string?)project.Attribute("InternalRomGameName") ?? "").Trim();
        var romChecksum = uint.Parse((string?)project.Attribute("InternalCheckSum")
                          ?? throw new InvalidDataException("Missing InternalCheckSum attribute."));

        var mapMode = ParseEnum<RomMapMode>(data, "RomMapMode");
        var speed   = ParseEnum<RomSpeed>(data, "RomSpeed");

        var comments     = ParseComments(data.Elements().FirstOrDefault(e => e.Name.LocalName == "Comments"));
        var (labels, labelComments) =
                          ParseLabels(data.Elements().FirstOrDefault(e => e.Name.LocalName == "Labels"));

        var romBytesEl   = RequireChild(data, "RomBytes");
        var bytes        = DizRomBytesDecoder.Decode(romBytesEl.Value);

        return new RomAnnotationStore
        {
            RomGameName   = romGameName,
            RomChecksum   = romChecksum,
            MapMode       = mapMode,
            Speed         = speed,
            SaveVersion   = saveVersion,
            Bytes         = bytes,
            Labels        = labels,
            LabelComments = labelComments,
            Comments      = comments,
        };
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private static void ValidateRoot(XElement root)
    {
        var watermark   = (string?)root.Attribute("Watermark");
        if (watermark != "DiztinGUIsh")
            throw new InvalidDataException(
                $"Invalid or missing Watermark (got '{watermark}'). " +
                "This does not appear to be a DiztinGUIsh project file.");

        var versionAttr = root.Attribute("SaveVersion");
        if (versionAttr is null || !int.TryParse((string)versionAttr, out var version))
            throw new InvalidDataException("SaveVersion attribute is missing or not a valid integer.");

        if (version < MinSaveVersion || version > MaxSaveVersion)
            throw new InvalidDataException(
                $"SaveVersion {version} is outside the supported range " +
                $"[{MinSaveVersion}, {MaxSaveVersion}].");
    }

    // ── Comment parsing ───────────────────────────────────────────────────────

    private static Dictionary<int, string> ParseComments(XElement? container)
    {
        var result = new Dictionary<int, string>();
        if (container is null) return result;

        foreach (var item in container.Elements(SysItem))
        {
            var keyAttr   = item.Attribute("Key");
            var valueAttr = item.Attribute("Value");
            if (keyAttr is null || valueAttr is null) continue;

            result[int.Parse((string)keyAttr)] = (string)valueAttr;
        }
        return result;
    }

    // ── Label parsing ─────────────────────────────────────────────────────────

    private static (Dictionary<int, string> names, Dictionary<int, string> comments)
        ParseLabels(XElement? container)
    {
        var names    = new Dictionary<int, string>();
        var comments = new Dictionary<int, string>();
        if (container is null) return (names, comments);

        foreach (var item in container.Elements(SysItem))
        {
            var keyAttr = item.Attribute("Key");
            if (keyAttr is null) continue;
            var key = int.Parse((string)keyAttr);

            // The <Value> child element carries Name and Comment as attributes.
            // Match by local name to be namespace-agnostic.
            var valueEl = item.Elements().FirstOrDefault(e => e.Name.LocalName == "Value");
            if (valueEl is null) continue;

            var name    = (string?)valueEl.Attribute("Name")    ?? "";
            var comment = (string?)valueEl.Attribute("Comment") ?? "";

            names[key] = name;
            if (comment.Length > 0)
                comments[key] = comment;
        }
        return (names, comments);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static XElement RequireChild(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)
        ?? throw new InvalidDataException(
            $"Required element <{localName}> not found inside <{parent.Name.LocalName}>.");

    private static T ParseEnum<T>(XElement element, string attributeName) where T : struct, Enum
    {
        var raw = (string?)element.Attribute(attributeName)
                  ?? throw new InvalidDataException(
                      $"Required attribute '{attributeName}' is missing from <{element.Name.LocalName}>.");

        if (!Enum.TryParse<T>(raw, out var value))
            throw new InvalidDataException(
                $"'{raw}' is not a valid {typeof(T).Name} value.");

        return value;
    }
}
