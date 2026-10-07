using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ClaudeRevit.Tools;

public class ExportImage : IRevitTool
{
    public string Name => "export_image";

    public string Description =>
        "Exports a view (or the active view) to a PNG image file. If output_path is omitted, " +
        "the image is saved to the user's Pictures folder with a timestamp filename. Returns verified dimensions/path plus a native MCP image attachment for visual inspection. Uses Revit export, not screen capture.";

    public InputSchema InputSchema => new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["view_id"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "View to export (defaults to active view)." }),
            ["output_path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Absolute output path (default: Pictures folder, timestamped)." }),
            ["pixel_size"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "Largest dimension in pixels (default 1600).", minimum = 256, maximum = 8192 })
        },
        Required = []
    };

    public bool RequiresTransaction => false;

    public string Execute(IReadOnlyDictionary<string, JsonElement> input, UIApplication app)
    {
        var doc = ToolContext.UiDocument(app)?.Document
            ?? throw new InvalidOperationException("No document is open.");

        View view;
        if (input.TryGetValue("view_id", out var vid) && vid.ValueKind == JsonValueKind.Number)
            view = doc.GetElement(new ElementId(vid.GetInt64())) as View
                ?? throw new InvalidOperationException("view_id is not a view.");
        else
            view = doc.ActiveView ?? throw new InvalidOperationException("No active view.");

        var pixels = input.TryGetValue("pixel_size", out var ps) ? ps.GetInt32() : 1600;
        if (pixels < 256 || pixels > 8192) pixels = 1600;

        string outPath;
        if (input.TryGetValue("output_path", out var op) && op.ValueKind == JsonValueKind.String)
        {
            outPath = op.GetString()!;
        }
        else
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var safe = string.Join("_", view.Name.Split(Path.GetInvalidFileNameChars()));
            outPath = Path.Combine(pictures, $"Revit_{safe}_{DateTime.Now:yyyyMMdd-HHmmss}.png");
        }

        // A bare filename yields an empty directory string, and Directory.CreateDirectory("")
        // throws — default to the Pictures folder in that case.
        var outDir = Path.GetDirectoryName(outPath);
        if (string.IsNullOrEmpty(outDir))
        {
            outDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            outPath = Path.Combine(outDir, Path.GetFileName(outPath));
        }
        outPath=Path.GetFullPath(outPath);
        if(!string.Equals(Path.GetExtension(outPath),".png",StringComparison.OrdinalIgnoreCase))throw new ToolInputException("output_path must use .png.");
        outDir=Path.GetDirectoryName(outPath)!;
        Directory.CreateDirectory(outDir);

        var stage=Path.Combine(outDir,"ClaudeRevit-export-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var options = new ImageExportOptions
        {
            ExportRange = ExportRange.SetOfViews,
            FilePath = Path.Combine(stage,"view.png"),
            ZoomType = ZoomFitType.FitToPage,
            PixelSize = pixels,
            ImageResolution = ImageResolution.DPI_150,
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
            FitDirection = FitDirectionType.Horizontal
        };
        options.SetViewsAndSheets(new List<ElementId> { view.Id });

        try
        {
            doc.ExportImage(options);
            var files=Directory.GetFiles(stage,"*.png");
            if(files.Length!=1)throw new InvalidOperationException("Native view export produced no unique PNG.");
            var bytes=File.ReadAllBytes(files[0]);
            using var stream=new MemoryStream(bytes);
            var decoder=new System.Windows.Media.Imaging.PngBitmapDecoder(stream,System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame=decoder.Frames[0];
            if(frame.PixelWidth<1||frame.PixelHeight<1)throw new InvalidOperationException("Native export has no pixels.");
            var scaled=new System.Windows.Media.Imaging.TransformedBitmap(frame,new System.Windows.Media.ScaleTransform(64.0/frame.PixelWidth,64.0/frame.PixelHeight));
            var rgb=new System.Windows.Media.Imaging.FormatConvertedBitmap(scaled,System.Windows.Media.PixelFormats.Bgra32,null,0);
            var pixelsCheck=new byte[rgb.PixelWidth*rgb.PixelHeight*4];rgb.CopyPixels(pixelsCheck,rgb.PixelWidth*4,0);
            var colors=Enumerable.Range(0,pixelsCheck.Length/4).Select(i=>(pixelsCheck[i*4]<<16)+(pixelsCheck[i*4+1]<<8)+pixelsCheck[i*4+2]).Distinct().Take(2).ToArray();
            if(colors.Length==1&&colors[0]==0)throw new InvalidOperationException("Revit exported an all-black image. Select another view or display style and retry.");
            File.Move(files[0],outPath,true);
            var imageId=Services.ViewImageStore.Register(bytes,Services.DocumentSessions.Key(doc),Services.McpSession.Executing?.ChannelId);
            return Services.Json.Serialize(new { view=view.Name,view_id=view.Id.Value,path=outPath,width=frame.PixelWidth,height=frame.PixelHeight,
                verified=true,image_id=imageId,mime_type="image/png",
                warnings=new[]{
                    colors.Length==1?"Export has uniform colour; visual content may be empty.":null,
                    imageId==null?$"The PNG was exported ({bytes.Length/1_000_000.0:0.0} MB) but is too large to attach for viewing; re-export with a smaller pixel_size to inspect it.":null
                }.Where(w=>w!=null).ToArray() });
        }
        finally
        {
            var owned=new DirectoryInfo(Path.GetFullPath(stage));
            if(owned.Exists && owned.LinkTarget==null && string.Equals(owned.Parent?.FullName,Path.GetFullPath(outDir),StringComparison.OrdinalIgnoreCase)) owned.Delete(true);
        }
    }
}
