using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace Wendel;

internal enum BarPosition
{
    Left,
    Right,
    Top,
    BottomRightCorner,
}

/// <summary>
/// User settings, stored as JSON in %APPDATA%\wendel\settings.json.
/// </summary>
[DataContract]
internal sealed class Settings
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "wendel", "settings.json");

    /// <summary>Name of a <see cref="BarPosition"/> value.</summary>
    [DataMember(Order = 0)]
    public string Position { get; set; } = nameof(BarPosition.Left);

    /// <summary>Opacity of the collapsed bar, 0.1 to 1.</summary>
    [DataMember(Order = 1)]
    public double Opacity { get; set; } = 0.5;

    /// <summary>Thickness of the collapsed bar in pixels at 100 % scaling.</summary>
    [DataMember(Order = 2)]
    public int CollapsedThickness { get; set; } = 8;

    /// <summary>Percentage points per notch of the mouse wheel.</summary>
    [DataMember(Order = 3)]
    public int WheelStep { get; set; } = 5;

    public BarPosition BarPosition
    {
        get => Enum.TryParse(Position, true, out BarPosition p) && Enum.IsDefined(typeof(BarPosition), p)
            ? p
            : BarPosition.Left;
        set => Position = value.ToString();
    }

    public double EffectiveOpacity => Math.Max(0.1, Math.Min(1.0, Opacity));

    public int EffectiveThickness => Math.Max(2, Math.Min(64, CollapsedThickness));

    public int EffectiveWheelStep => Math.Max(1, Math.Min(50, WheelStep));

    [OnDeserializing]
    private void OnDeserializing(StreamingContext context)
    {
        // The serializer doesn't run constructors or initializers; keep defaults for
        // members missing from the file.
        Position = nameof(BarPosition.Left);
        Opacity = 0.5;
        CollapsedThickness = 8;
        WheelStep = 5;
    }

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        Position ??= nameof(BarPosition.Left);
    }

    /// <summary>
    /// Loads the settings file. A missing file gives defaults; an unreadable one gives
    /// defaults and an error message.
    /// </summary>
    public static Settings Load(out string? error)
    {
        error = null;
        if (!File.Exists(FilePath))
            return new Settings();
        try
        {
            return Deserialize(File.ReadAllBytes(FilePath));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException
                                  || e is SerializationException || e is XmlException)
        {
            error = $"Could not read {FilePath}, using defaults: {e.Message}";
            return new Settings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, Serialize());
    }

    internal static Settings Deserialize(byte[] json)
    {
        using var stream = new MemoryStream(json);
        return (Settings?)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream)
            ?? throw new SerializationException("The file is empty.");
    }

    internal byte[] Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, new UTF8Encoding(false), false, true))
        {
            new DataContractJsonSerializer(typeof(Settings)).WriteObject(writer, this);
        }
        return stream.ToArray();
    }
}
