using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using autodraw_plugin.Models.AutoDraw;

namespace autodraw_plugin.Services;

/// <summary>
/// Every state of a job the server has sent, kept on disk so a finished job
/// can be played back without asking for each state again.
///
/// A saved state never changes once made, so nothing here ever goes stale and
/// nothing is ever evicted. The same state laid out differently is a
/// different drawing, so the drawing is kept per layout and direction; what
/// the state is - its record, its position, its notes - is kept once.
///
/// Under %LOCALAPPDATA%\autodraw\cache\&lt;project&gt;\:
///   &lt;artifact&gt;.json                       the state: record, meta, notes, branch
///   &lt;artifact&gt;_&lt;layout&gt;_&lt;direction&gt;.json  its drawing and grid, as a reply laid it out
///   &lt;artifact&gt;_&lt;layout&gt;_&lt;direction&gt;.snap  its picture, from the earlier-state endpoint
///
/// The picture is not the drawing: the server leaves the project's band out of
/// it and draws the cell labels in, so one cannot stand in for the other.
///
/// A cache that cannot be read or written is only a slower playback, so every
/// failure here is a miss and never an error.
/// </summary>
public static class DrawingCache
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "autodraw", "cache");

    /// <summary>What a state is, the same in every layout.</summary>
    public class State
    {
        public int ArtifactId { get; set; }
        public string? Branch { get; set; }
        public AutoDrawMetaDTO? Meta { get; set; }
        public AutoDrawRecordDTO? Record { get; set; }

        /// <summary>
        /// The notes as they stood when the state was sent. A note written
        /// since is missing here; the server call that ends a playback brings
        /// the panel up to date.
        /// </summary>
        public List<NoteDTO>? Notes { get; set; }
    }

    /// <summary>A state's drawing in one layout, with the grid it was laid out on.</summary>
    public class Drawing
    {
        public string? Dxf { get; set; }
        public GridDTO? Grid { get; set; }
    }

    /// <summary>
    /// Keep a reply that carries the whole of a state's drawing and says which
    /// state it is. An owned-only drawing is not the whole state - MPanel's
    /// geometry is missing from it - so playing it back would lose that.
    /// </summary>
    public static void Keep(int projectId, string? dxf, string? scope, GridDTO? grid,
                            AutoDrawMetaDTO? meta, AutoDrawRecordDTO? record,
                            List<NoteDTO>? notes, string? branch)
    {
        int? artifact = record?.CurrentArtifactId;
        if (artifact == null || string.IsNullOrEmpty(dxf) || scope != "full" || grid == null) return;

        Write(StatePath(projectId, artifact.Value), JsonConvert.SerializeObject(new State
        {
            ArtifactId = artifact.Value,
            Branch = branch,
            Meta = meta,
            Record = record,
            Notes = notes,
        }));

        string laid = DrawingPath(projectId, artifact.Value, grid.Mode, grid.Direction, ".json");
        if (!File.Exists(laid))
            Write(laid, JsonConvert.SerializeObject(new Drawing { Dxf = dxf, Grid = grid }));
    }

    /// <summary>A state and its drawing in this layout, or null if either is missing.</summary>
    public static (State state, Drawing drawing)? Find(int projectId, int artifactId,
                                                       string layout, string direction)
    {
        State? state = Read<State>(StatePath(projectId, artifactId));
        Drawing? drawing = Read<Drawing>(DrawingPath(projectId, artifactId, layout, direction, ".json"));
        if (state == null || string.IsNullOrEmpty(drawing?.Dxf)) return null;
        return (state, drawing);
    }

    public static string? FindSnapshot(int projectId, int artifactId, string layout, string direction)
    {
        try
        {
            string path = DrawingPath(projectId, artifactId, layout, direction, ".snap");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void KeepSnapshot(int projectId, int artifactId, string layout, string direction, string dxf)
    {
        Write(DrawingPath(projectId, artifactId, layout, direction, ".snap"), dxf);
    }

    /// <summary>
    /// The states ahead of this one on the line being followed, as far as this
    /// machine has seen them, each as its step.substep key and artifact.
    ///
    /// After a back the record no longer lists them, but every state kept here
    /// carries the record of its own line, which lists the state each
    /// completed substep produced. So this is the server's own walk
    /// (lineage._toward) done over the cache: the newest state kept on the
    /// branch, if the job now is on its way to it. Failing that, where every
    /// state kept ahead lies on one line, that line; where they fork and the
    /// branch does not settle it, nothing.
    ///
    /// It is a guess about a record held elsewhere - a line extended from
    /// another machine is not here - so whoever plays it asks the server to
    /// follow at the end and believes the answer.
    /// </summary>
    public static List<(string key, int artifact)> Ahead(int projectId, AutoDrawRecordDTO? here,
                                                         string? branch, AutoDrawConfigDTO? config)
    {
        var none = new List<(string, int)>();
        if (config?.Steps == null) return none;

        List<(string key, int artifact)> mine = Line(here, config);
        var kept = new List<(State state, List<(string key, int artifact)> line)>();
        try
        {
            string folder = Path.Combine(Root, projectId.ToString());
            if (!Directory.Exists(folder)) return none;
            foreach (string path in Directory.GetFiles(folder, "*.json"))
            {
                // The state files alone: the drawings are named with their layout.
                if (Path.GetFileNameWithoutExtension(path).Contains('_')) continue;
                State? state = Read<State>(path);
                if (state?.Record != null) kept.Add((state, Line(state.Record, config)));
            }
        }
        catch (Exception)
        {
            return none;
        }

        var onTheWay = kept.Where(k => k.line.Count > mine.Count && StartsWith(k.line, mine)).ToList();
        if (onTheWay.Count == 0) return none;

        List<(string key, int artifact)>? chosen = null;
        if (!string.IsNullOrWhiteSpace(branch))
        {
            var tip = kept.Where(k => (k.state.Branch ?? "main") == branch)
                          .OrderByDescending(k => k.state.ArtifactId)
                          .FirstOrDefault();
            if (tip.line != null && onTheWay.Any(k => k.state.ArtifactId == tip.state.ArtifactId))
                chosen = tip.line;
        }
        if (chosen == null)
        {
            var longest = onTheWay.OrderByDescending(k => k.line.Count).First().line;
            if (onTheWay.All(k => StartsWith(longest, k.line))) chosen = longest;
        }
        return chosen == null ? none : chosen.Skip(mine.Count).ToList();
    }

    /// <summary>Each completed substep of a record's line and the state it made, in config order.</summary>
    private static List<(string key, int artifact)> Line(AutoDrawRecordDTO? record, AutoDrawConfigDTO config)
    {
        var line = new List<(string, int)>();
        if (record?.Steps == null) return line;
        foreach (ConfigStepDTO step in config.Steps)
        {
            if (!record.Steps.TryGetValue(step.Key, out AutoDrawStepStatusDTO? done) || done?.Substeps == null) continue;
            foreach (ConfigSubstepDTO substep in step.Substeps)
            {
                if (done.Substeps.TryGetValue(substep.Key, out AutoDrawSubstepStatusDTO? state)
                    && state?.ArtifactId != null)
                    line.Add((step.Key + "." + substep.Key, state.ArtifactId.Value));
            }
        }
        return line;
    }

    private static bool StartsWith(List<(string key, int artifact)> line, List<(string key, int artifact)> start)
    {
        if (start.Count > line.Count) return false;
        for (int i = 0; i < start.Count; i++)
            if (line[i] != start[i]) return false;
        return true;
    }

    private static string StatePath(int projectId, int artifactId) =>
        Path.Combine(Root, projectId.ToString(), artifactId + ".json");

    private static string DrawingPath(int projectId, int artifactId, string layout, string direction, string ext) =>
        Path.Combine(Root, projectId.ToString(),
                     $"{artifactId}_{layout.ToLowerInvariant()}_{direction.ToLowerInvariant()}{ext}");

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonConvert.DeserializeObject<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Written aside and moved in, so a crash never leaves half a file to be read back.</summary>
    private static void Write(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string aside = path + ".tmp";
            File.WriteAllText(aside, text);
            File.Move(aside, path, overwrite: true);
        }
        catch (Exception)
        {
        }
    }
}
