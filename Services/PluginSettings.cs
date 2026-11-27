using System;
using System.IO;
using Newtonsoft.Json;

namespace autodraw_plugin.Services;

/// <summary>
/// The designer's own preferences, kept between AutoCAD sessions.
///
/// Per user rather than per drawing: how fast a job plays back is a habit of
/// the person at the keyboard, not a property of the job. Stored as a small
/// JSON file under %LOCALAPPDATA%, and never sent to the server.
/// </summary>
public class PluginSettings
{
    /// <summary>
    /// ADSTEPCONTINUE and ADSTEPFORWARD keep going on their own, one substep per tick,
    /// instead of taking a single substep.
    /// </summary>
    public bool Auto { get; set; }

    /// <summary>
    /// The least time between two substeps while playing, in seconds. A step
    /// that takes longer than this simply goes when it is done.
    /// </summary>
    public double TickSeconds { get; set; } = 1.0;

    /// <summary>
    /// "Replace" keeps one cell per item and draws each state over the last;
    /// "Stack" gives every substep a cell of its own and keeps the earlier
    /// states beside the one being worked on, as a drawing done by hand does.
    /// </summary>
    public string Layout { get; set; } = "Replace";

    /// <summary>Which way the substeps run when stacked: "Right" or "Down".</summary>
    public string Direction { get; set; } = "Right";

    /// <summary>
    /// Which cells of the grid are outlined: "All" of them, only those of the
    /// "Live" state, or "Off".
    /// </summary>
    public string Gridlines { get; set; } = "All";

    /// <summary>
    /// How far into a free ADRUN of several steps - a list typed in or a recipe
    /// - its states are drawn, each beside the last with a caption, in Stack
    /// layout: 0 only the final one, 1 one per step, 2 opening the recipes
    /// inside it one level, -1 after every operation however deep.
    /// </summary>
    public int StepsDepth { get; set; }

    /// <summary>
    /// Write every substep's description under it on the INFO board. The
    /// substep waiting on a person always shows its own, whatever this says.
    /// </summary>
    public bool Descriptions { get; set; }

    /// <summary>
    /// A recipe run with ADRUN stops after every operation (`pause=each`), for
    /// the designer to look and ADGO, instead of only after a step
    /// marked for review (`pause=reviews`). Free runs and project runs alike.
    /// </summary>
    public bool StepThrough { get; set; }

    /// <summary>
    /// A project's operation history is laid out as a grid of pictures,
    /// operations one way and the job's items the other, beside the drawing
    /// in hand - instead of following Layout. Project runs only: a free run
    /// keeps no history to lay out.
    /// </summary>
    public bool OperationsGrid { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "autodraw", "settings.json");

    private static PluginSettings? _current;

    public static PluginSettings Current => _current ??= Load();

    private static PluginSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonConvert.DeserializeObject<PluginSettings>(File.ReadAllText(FilePath))
                       ?? new PluginSettings();
            }
        }
        catch (Exception)
        {
            // A damaged file is not worth stopping anybody for: start from the
            // defaults, and the next save writes a good one over it.
        }
        return new PluginSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
    }
}
