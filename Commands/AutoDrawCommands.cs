using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using autodraw_plugin.Services;
using System;

using Exception = System.Exception;

namespace autodraw_plugin.Commands;

public class AutoDrawCommands
{
    [CommandMethod("ADSTART")]
    public async void StartAutoDraw()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        Database db = doc.Database;

        if (!autodraw.Auth.IsLoggedIn) 
        { 
             ed.WriteMessage("\nPlease login first (ADLOGIN)."); 
             return; 
        }

        try
        {
             PromptStringOptions idOpts = new PromptStringOptions("\nEnter Project ID: ");
             idOpts.AllowSpaces = false;
             PromptResult idRes = ed.GetString(idOpts);
             if (idRes.Status != PromptStatus.OK) return;

             if (!int.TryParse(idRes.StringResult, out int projectId))
             {
                 ed.WriteMessage("\nInvalid Project ID.");
                 return;
             }

             ed.WriteMessage($"\nFetching project {projectId}...");

             await autodraw.AutoDraw.StartProject(projectId);

             if (!autodraw.AutoDraw.HasActiveProject)
             {
                 ed.WriteMessage("\nFailed to load project data. Check API or ID.");
                 return;
             }

             var data = autodraw.AutoDraw.CurrentProjectData!;

             using (DocumentLock docLock = doc.LockDocument())
             using (Transaction tr = db.TransactionManager.StartTransaction())
             {
                 BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                 BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                 AutoDrawVisualizer.ClearAndPrepareLayer(tr, db);

                 AutoDrawVisualizer.DrawStatusBoard(tr, btr, data.autodraw_config, data.autodraw_meta, new Point3d(-15000, 0, 0));
                 
                 AutoDrawVisualizer.DrawDebugRow(tr, btr, data);
                 
                 AutoDrawVisualizer.DrawWorkflowBoxes(tr, btr, data.autodraw_config, data.autodraw_meta, data.autodraw_record);

                 tr.Commit();
             }
             ed.Regen();
             ed.WriteMessage("\nAutoDraw setup complete.");
        }
        catch (Exception ex)
        {
             ed.WriteMessage($"\nError: {ex.Message}");
        }
    }
}
