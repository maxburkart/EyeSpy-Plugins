// Settings bound to json/config_en.json via each item's "bindto".
// PluginBase.ConfigObject is an instance of this class.
public partial class configuration
{
    // --- General ---
    public bool Enabled { get; set; } = true;

    /// <summary>auto | copy | encode</summary>
    public string Mode { get; set; } = "auto";

    /// <summary>Read-only status text shown in the config dialog (refreshed each time it opens).</summary>
    public string Status { get; set; } = "";

    // --- Source (copy mode) ---
    /// <summary>Low-res (sub) stream URL. Blank = try to detect it from Agent.</summary>
    public string SourceUrl { get; set; } = "";

    /// <summary>tcp | udp | auto</summary>
    public string RtspTransport { get; set; } = "tcp";

    /// <summary>none | copy | aac</summary>
    public string Audio { get; set; } = "none";

    // --- Encode mode (records the frames Agent already decodes from the Live URL) ---
    public int EncodeHeight { get; set; } = 480;
    public int EncodeFps { get; set; } = 10;
    public int EncodeQuality { get; set; } = 28;
    public string EncodeCodec { get; set; } = "libx264";
    public int EncodeBitrateKbps { get; set; } = 512;

    // --- Output ---
    public int SegmentMinutes { get; set; } = 15;

    /// <summary>fmp4 | mp4 | mkv</summary>
    public string Container { get; set; } = "fmp4";

    /// <summary>Hand finished segments to Agent (timeline, playback, storage management).</summary>
    public bool RegisterInAgent { get; set; } = true;

    public string Tags { get; set; } = "continuous";
    public bool TagEventSegments { get; set; } = true;

    /// <summary>Blank = [Agent data path]/ContinuousLowRes</summary>
    public string OutputFolder { get; set; } = "";

    public int RetentionDays { get; set; } = 7;
    public int MaxStorageGB { get; set; } = 0;

    // --- Advanced ---
    /// <summary>Blank = look next to Agent, then on PATH.</summary>
    public string FfmpegPath { get; set; } = "";
    public string ExtraInputArgs { get; set; } = "";
    public string ExtraOutputArgs { get; set; } = "";
}
