using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OnlyFansControl
{
    internal static class CurveEditorTests
    {
        internal static object Run(string output)
        {
            List<string> checks = new List<string>();
            Action<bool,string> check = delegate(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); };
            string directory = Path.Combine(Path.GetTempPath(), "onlyfans-curves-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); string path = Path.Combine(directory, "config.json");
            try
            {
                string original = File.ReadAllText(FanConfig.FilePath); File.WriteAllText(path, original);
                CurveDocument document = new CurveDocument(path, EcProfiles.T495);
                List<CurvePoint> points = document.Settings.curve.Select(p => new CurvePoint { temperature_c = p.temperature_c, level = p.level }).ToList();
                points[0].level = "2"; points[1].level = "2";
                document.Save(points);
                check(FanConfig.Load(path, EcProfiles.T495).LevelAt(0) == 2, "Saving updates the T495 override");
                check(FanConfig.Load(path).LevelAt(0) == 1, "Saving preserves the shared curve");
                check(FanConfig.Load(path).critical_temperature_c == 90, "Saving preserves thermal protection");
                bool rejected = false;
                try { document.Save(points); } catch (IOException) { rejected = true; }
                check(rejected, "Stale editor cannot overwrite an external change");
                document = new CurveDocument(path, EcProfiles.T495); string before = File.ReadAllText(path);
                points[1].temperature_c = 0; rejected = false;
                try { document.Save(points); } catch (ArgumentException) { rejected = true; }
                check(rejected && File.ReadAllText(path) == before, "Invalid thresholds leave the file untouched");
                string legacy = "{\"extra\":\"keep\",\"fan_policy\":{\"poll_ms\":2000,\"hysteresis_c\":3,\"failsafe_temp_c\":92,\"smart_curve\":[{\"temp_c\":0,\"level\":1},{\"temp_c\":62,\"level\":2}]}}";
                File.WriteAllText(path, legacy); document = new CurveDocument(path, EcProfiles.T495);
                document.Settings.curve[1].level = "Max"; document.Save(document.Settings.curve);
                check(FanConfig.Load(path).LevelAt(70) == 8 && File.ReadAllText(path).Contains("\"extra\":\"keep\""), "Legacy curves round-trip with unrelated fields preserved");
                FanConfig shared = FanConfig.Parse(original); shared.profile_curves = null;
                File.WriteAllText(path, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(shared));
                document = new CurveDocument(path, EcProfiles.T495); document.Save(document.Settings.curve);
                check(FanConfig.Load(path).profile_curves.ContainsKey(EcProfiles.T495.Id), "Shared fallback becomes a device override when saved");
                File.WriteAllText(path, original);
                using (MainWindow main = new MainWindow(new DeviceProfile(), false))
                {
                    new System.Windows.Interop.WindowInteropHelper(main.Window).EnsureHandle();
                    CurveEditor editor = new CurveEditor(main.Window, new CurveDocument(path, EcProfiles.T495));
                    check(editor.CanSave, "Editor accepts the original curve");
                    editor.Temperatures[1].Text = "bad";
                    check(!editor.CanSave, "Editor blocks nonnumeric temperatures");
                    editor.Temperatures[1].Text = "0";
                    check(!editor.CanSave, "Editor blocks duplicate thresholds");
                    editor.Temperatures[1].Text = "55"; editor.Levels[1].SelectedItem = "7";
                    check(!editor.CanSave, "Editor blocks decreasing fan levels");
                    editor.Levels[1].SelectedItem = "1"; editor.AddPoint();
                    check(editor.CanSave && editor.Temperatures.Count == 8, "Adding a valid point refreshes the editor");
                    FrameworkElement content = (FrameworkElement)editor.Content;
                    content.Margin = new Thickness(0);
                    content.Width = 556; content.Height = 650;
                    content.Measure(new Size(556,650)); content.Arrange(new Rect(0,0,556,650)); content.UpdateLayout(); editor.Refresh(); content.UpdateLayout();
                    editor.MovePoint(2, editor.GraphPosition(65,3));
                    check(editor.Temperatures[2].Text == "65" && (string)editor.Levels[2].SelectedItem == "3" && editor.CanSave, "Graph movement updates both table values");
                    Point fractional = editor.GraphPosition(65,3); fractional.X += 0.2; fractional.Y += 0.2;
                    editor.MovePoint(2,fractional);
                    check(editor.Temperatures[2].Text == "65" && (string)editor.Levels[2].SelectedItem == "3", "Graph movement snaps to whole degrees and discrete levels");
                    editor.MovePoint(2,new Point(-10000,10000));
                    check(editor.Temperatures[2].Text == "56" && (string)editor.Levels[2].SelectedItem == "1" && editor.CanSave, "Dragging clamps to the preceding point");
                    editor.MovePoint(2,new Point(10000,-10000));
                    check(editor.Temperatures[2].Text == "67" && (string)editor.Levels[2].SelectedItem == "3" && editor.CanSave, "Dragging clamps to the following point");
                    editor.MovePoint(0,editor.GraphPosition(40,8));
                    check(editor.Temperatures[0].Text == "0" && (string)editor.Levels[0].SelectedItem == "1", "First point remains at zero and respects the next level");
                    editor.MovePoint(7,new Point(10000,-10000));
                    check(editor.Temperatures[7].Text == "90" && (string)editor.Levels[7].SelectedItem == "Max", "Final point cannot exceed critical temperature or Max");
                    editor.MovePoint(7,new Point(-10000,10000));
                    check(editor.Temperatures[7].Text == "87" && (string)editor.Levels[7].SelectedItem == "7" && editor.CanSave, "Final point cannot cross its preceding threshold or level");
                    editor.MovePoint(2,editor.GraphPosition(62,2)); editor.MovePoint(7,editor.GraphPosition(88,8));
                    check(File.ReadAllText(path) == original, "Dragging leaves the on-disk configuration untouched until Save");
                    content.UpdateLayout();
                    RenderTargetBitmap bitmap = new RenderTargetBitmap(556,650,96,96,PixelFormats.Pbgra32);
                    DrawingVisual background = new DrawingVisual(); using (DrawingContext drawing = background.RenderOpen()) drawing.DrawRectangle(editor.Background,null,new Rect(0,0,556,650));
                    bitmap.Render(background); bitmap.Render(content);
                    PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (Stream stream = File.Create(Path.ChangeExtension(output,".png"))) encoder.Save(stream);
                    editor.Close(); check(File.ReadAllText(path) == original, "Closing without saving preserves the configuration");
                    editor = new CurveEditor(main.Window, new CurveDocument(path, EcProfiles.T495));
                    content = (FrameworkElement)editor.Content; content.Width = 556; content.Height = 650;
                    content.Measure(new Size(604,698)); content.Arrange(new Rect(0,0,604,698)); content.UpdateLayout();
                    editor.MovePoint(2,editor.GraphPosition(65,3));
                    editor.Levels[0].SelectedItem = "2"; editor.Levels[1].SelectedItem = "2"; editor.SaveCurve();
                    check(editor.Saved && FanConfig.Load(path, EcProfiles.T495).LevelAt(0) == 2, "Editor Save writes the edited curve");
                    check(FanConfig.Load(path, EcProfiles.T495).LevelAt(65) == 3, "Editor Save persists a dragged point");
                }
                return new { success = true, passed = checks.Count, hardware_register_writes = 0, checks = checks };
            }
            finally { File.Delete(path); Directory.Delete(directory); }
        }
    }
}
