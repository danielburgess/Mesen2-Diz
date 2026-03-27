using System.Xml.Linq;

namespace Mesen.Annotation.Diz;

/// <summary>
/// Serializes a <see cref="RomAnnotationStore"/> back to a DiztinGUIsh
/// .dizraw XML string that can be re-imported by <see cref="DizProjectImporter"/>
/// or by DiztinGUIsh itself.
///
/// The output format mirrors the ExtendedXmlSerializer XML structure that
/// DiztinGUIsh writes: same element names, same attribute names, same
/// sys-namespace for Item elements, same RomBytes compression pipeline.
/// </summary>
public static class DizProjectExporter
{
    private const int MinSaveVersion = 100;
    private const int MaxSaveVersion = 104;

    private static readonly XNamespace SysNs =
        "https://extendedxmlserializer.github.io/system";

    // Default namespace DiztinGUIsh places on the Labels container.
    private static readonly XNamespace ClrNs =
        "clr-namespace:Diz.Core.serialization.xml_serializer;assembly=Diz.Core";

    /// <summary>
    /// Serialize <paramref name="store"/> to a .dizraw XML string.
    /// </summary>
    /// <param name="store">Annotation data to export.</param>
    /// <param name="saveVersion">
    /// Value written to the SaveVersion attribute. Must be in [100, 104].
    /// Defaults to 104 (latest).
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="saveVersion"/> is out of range.
    /// </exception>
    public static string Export(RomAnnotationStore store, int saveVersion = 104)
    {
        if (saveVersion < MinSaveVersion || saveVersion > MaxSaveVersion)
            throw new ArgumentException(
                $"saveVersion {saveVersion} is outside the supported range " +
                $"[{MinSaveVersion}, {MaxSaveVersion}].",
                nameof(saveVersion));

        var romBytesContent = DizRomBytesSectionEncoder.Encode(store.Bytes);

        var dataEl = new XElement("Data",
            new XAttribute("RomMapMode", store.MapMode.ToString()),
            new XAttribute("RomSpeed",   store.Speed.ToString()),
            new XElement("RomBytes", romBytesContent));

        if (store.Comments.Count > 0)
            dataEl.Add(BuildCommentsElement(store.Comments));

        if (store.Labels.Count > 0)
            dataEl.Add(BuildLabelsElement(store.Labels, store.LabelComments));

        var projectEl = new XElement("Project",
            new XAttribute("InternalRomGameName", store.RomGameName),
            new XAttribute("InternalCheckSum",    store.RomChecksum.ToString()),
            dataEl);

        var root = new XElement("ProjectXmlSerializer-Root",
            new XAttribute("SaveVersion", saveVersion),
            new XAttribute("Watermark",   "DiztinGUIsh"),
            new XAttribute(XNamespace.Xmlns + "sys", SysNs.NamespaceName),
            projectEl);

        return root.ToString(SaveOptions.None);
    }

    // ── Comments ──────────────────────────────────────────────────────────────

    private static XElement BuildCommentsElement(IReadOnlyDictionary<int, string> comments)
    {
        var el = new XElement("Comments");
        foreach (var (key, value) in comments.OrderBy(kvp => kvp.Key))
        {
            el.Add(new XElement(SysNs + "Item",
                new XAttribute("Key",   key),
                new XAttribute("Value", value)));
        }
        return el;
    }

    // ── Labels ────────────────────────────────────────────────────────────────

    private static XElement BuildLabelsElement(
        IReadOnlyDictionary<int, string>  labels,
        IReadOnlyDictionary<int, string>  labelComments)
    {
        // Declare the clr default namespace on the Labels element so that
        // child Value elements are placed in the same namespace as DiztinGUIsh
        // writes them (matched by local name in the importer).
        var el = new XElement(ClrNs + "Labels");
        foreach (var (key, name) in labels.OrderBy(kvp => kvp.Key))
        {
            var comment = labelComments.TryGetValue(key, out var c) ? c : "";
            el.Add(new XElement(SysNs + "Item",
                new XAttribute("Key", key),
                new XElement(ClrNs + "Value",
                    new XAttribute("Name",    name),
                    new XAttribute("Comment", comment))));
        }
        return el;
    }
}
