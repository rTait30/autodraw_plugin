using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace autodraw_plugin.Services;

/// <summary>How the open job relates to the drawing on screen.</summary>
public enum JobMode
{
    /// <summary>No job: ADRUN works on what is selected, answers asked.</summary>
    None,

    /// <summary>
    /// The job's own drawing is laid in, and runs are kept on its history -
    /// a job the server draws. ADBACK, ADFORWARD and ADGO work along it.
    /// </summary>
    Loaded,

    /// <summary>
    /// The drawing is the person's own; the job only answers what runs ask
    /// (its fabric, its quantities) and takes the log. ADRUN works on the
    /// selection and nothing of the drawing is replaced or recorded.
    /// </summary>
    Attached,
}

/// <summary>
/// The job open in this AutoCAD: everything the commands act on, in one place.
///
/// None of it is the plugin's own: each field is what the server last said,
/// and opening the job again (ADOPEN) fills it afresh - the waiting point
/// included, which the server keeps on the run. So nothing is lost when
/// AutoCAD closes, and two machines see the same job.
/// </summary>
public static class Job
{
    public static int? ProjectId { get; private set; }

    public static JobMode Mode { get; private set; } = JobMode.None;

    public static bool IsLoaded => Mode == JobMode.Loaded && ProjectId.HasValue;

    public static bool IsOpen => Mode != JobMode.None && ProjectId.HasValue;

    /// <summary>The project a free run takes its answers from: the attached job's.</summary>
    public static int? AnswersFrom => Mode == JobMode.Attached ? ProjectId : null;

    /// <summary>The job's drawing last laid in, which the next run starts from.</summary>
    public static int? CurrentArtifact { get; set; }

    /// <summary>The operation the laid-in drawing was made by, for the status line.</summary>
    public static string? At { get; set; }

    /// <summary>
    /// The run stopped for a person, which ADGO goes on with; null when none
    /// is. A free run's stop lives only here, since the server keeps nothing
    /// of a free run; a job's comes back from the server whenever it is laid in.
    /// </summary>
    public static PausedRun? Paused { get; set; }

    /// <summary>The job items its history has drawn, offered when a note is for one.</summary>
    public static List<int> Items { get; set; } = new();

    /// <summary>The last run, with the answers it was given, for ADRUN Again.</summary>
    public static LastRun? Last { get; set; }

    public static void Open(int projectId, JobMode mode)
    {
        Close();
        ProjectId = projectId;
        Mode = mode;
    }

    public static void Close()
    {
        ProjectId = null;
        Mode = JobMode.None;
        CurrentArtifact = null;
        At = null;
        Items = new List<int>();
        // A free run waiting stays: it is not the job's.
        if (Paused?.ProjectId != null) Paused = null;
    }

    /// <summary>The project's drawing was taken away from under the job (ADSTEPRESET).</summary>
    public static void Forget(int projectId)
    {
        if (ProjectId != projectId) return;
        CurrentArtifact = null;
        At = null;
        if (Mode == JobMode.Loaded) Mode = JobMode.Attached;
        if (Paused?.ProjectId == projectId) Paused = null;
    }

    /// <summary>One line saying where things stand and what to do next.</summary>
    public static string Status()
    {
        string job = Mode switch
        {
            JobMode.Loaded => $"Job {ProjectId}" + (At != null ? $" at {At}" : ", nothing drawn yet"),
            JobMode.Attached => $"Job {ProjectId} attached to your drawing",
            _ => "No job open - ADRUN works on what you select",
        };
        if (Paused == null) return job + ".";
        string next = Paused.ProjectId.HasValue && !IsLoaded
            ? $"ADOPEN {Paused.ProjectId} to go on with it"
            : "ADGO to go on";
        return $"{job}. {Paused.Name} waits at step {Paused.Next}: {next}.";
    }
}

/// <summary>A run stopped for a person, as ADGO sends it on.</summary>
public class PausedRun
{
    /// <summary>The job it ran on, or null for a free run.</summary>
    public int? ProjectId { get; init; }
    public string Name { get; init; } = "";
    public JObject Inputs { get; init; } = new();
    public int Next { get; init; }
    public int Step { get; init; }
    public int Steps { get; init; }
    public string? Message { get; init; }
    /// <summary>What a free run had settled, sent back going on; a job's run keeps its own.</summary>
    public JToken? Facts { get; init; }
}

/// <summary>What ADRUN last ran, to run again as it was.</summary>
public class LastRun
{
    public string Name { get; init; } = "";
    public JObject Inputs { get; init; } = new();
    public int Start { get; init; } = 1;
}
