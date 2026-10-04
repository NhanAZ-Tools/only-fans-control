using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace OnlyFansControl
{
    public static class LucideSvg
    {
        private static double Number(XElement element, string name, double fallback = 0)
        {
            XAttribute value = element.Attribute(name);
            return value == null ? fallback : double.Parse(value.Value, CultureInfo.InvariantCulture);
        }

        public static Geometry Read(Stream source)
        {
            XDocument document;
            using (XmlReader reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                document = XDocument.Load(reader);
            if (document.Root == null || document.Root.Name.LocalName != "svg" || (string)document.Root.Attribute("viewBox") != "0 0 24 24")
                throw new InvalidDataException("Expected a Lucide SVG with a 24 by 24 viewBox.");
            GeometryGroup group = new GeometryGroup();
            foreach (XElement element in document.Root.Elements())
            {
                Geometry shape;
                switch (element.Name.LocalName)
                {
                    case "path": shape = Geometry.Parse((string)element.Attribute("d")); break;
                    case "circle": shape = new EllipseGeometry(new Point(Number(element, "cx"), Number(element, "cy")), Number(element, "r"), Number(element, "r")); break;
                    case "ellipse": shape = new EllipseGeometry(new Point(Number(element, "cx"), Number(element, "cy")), Number(element, "rx"), Number(element, "ry")); break;
                    case "line": shape = new LineGeometry(new Point(Number(element, "x1"), Number(element, "y1")), new Point(Number(element, "x2"), Number(element, "y2"))); break;
                    case "rect":
                        double radiusX = Number(element, "rx", Number(element, "ry"));
                        shape = new RectangleGeometry(new Rect(Number(element, "x"), Number(element, "y"), Number(element, "width"), Number(element, "height")), radiusX, Number(element, "ry", radiusX)); break;
                    case "polyline":
                    case "polygon":
                        string[] values = Regex.Split(((string)element.Attribute("points") ?? "").Trim(), @"[\s,]+");
                        if (values.Length < 4 || values.Length % 2 != 0) throw new InvalidDataException("Invalid SVG points.");
                        List<Point> points = new List<Point>();
                        for (int i = 0; i < values.Length; i += 2) points.Add(new Point(double.Parse(values[i], CultureInfo.InvariantCulture), double.Parse(values[i + 1], CultureInfo.InvariantCulture)));
                        StreamGeometry lines = new StreamGeometry();
                        using (StreamGeometryContext context = lines.Open()) { context.BeginFigure(points[0], false, element.Name.LocalName == "polygon"); context.PolyLineTo(points.Skip(1).ToList(), true, false); }
                        shape = lines; break;
                    default: throw new InvalidDataException("Unsupported Lucide SVG element: " + element.Name.LocalName);
                }
                group.Children.Add(shape);
            }
            if (group.Children.Count == 0) throw new InvalidDataException("The Lucide SVG is empty.");
            group.Freeze(); return group;
        }

        public static void Draw(DrawingContext drawing, Geometry geometry, Brush foreground, double width, double height)
        {
            if (width <= 0 || height <= 0) return;
            double scale = Math.Min(width, height) / 24;
            drawing.PushTransform(new TranslateTransform((width - 24 * scale) / 2, (height - 24 * scale) / 2));
            drawing.PushTransform(new ScaleTransform(scale, scale));
            Pen pen = new Pen(foreground ?? Brushes.Black, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            drawing.DrawGeometry(null, pen, geometry); drawing.Pop(); drawing.Pop();
        }
    }

    public sealed class LucideIcon : FrameworkElement
    {
        public static readonly DependencyProperty IconProperty = DependencyProperty.Register("Icon", typeof(string), typeof(LucideIcon), new FrameworkPropertyMetadata("fan", FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(LucideIcon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
        public string Icon { get { return (string)GetValue(IconProperty); } set { SetValue(IconProperty, value); } }
        public Brush Foreground { get { return (Brush)GetValue(ForegroundProperty); } set { SetValue(ForegroundProperty, value); } }
        public LucideIcon() { Width = 18; Height = 18; IsHitTestVisible = false; Focusable = false; }
        protected override void OnRender(DrawingContext drawing) { base.OnRender(drawing); LucideSvg.Draw(drawing, LucideAssets.Geometry(Icon), Foreground, ActualWidth, ActualHeight); }
    }

    internal static class LucideAssets
    {
        private static readonly Dictionary<string, Geometry> geometries = new Dictionary<string, Geometry>();
        private static readonly Assembly assembly = typeof(LucideIcon).Assembly;
        private static ImageSource applicationIcon;

        internal static Geometry Geometry(string name)
        {
            lock (geometries)
            {
                Geometry value;
                if (!geometries.TryGetValue(name, out value))
                {
                    using (Stream source = assembly.GetManifestResourceStream("Lucide." + name + ".svg"))
                    { if (source == null) throw new InvalidDataException("Missing embedded Lucide icon: " + name); value = LucideSvg.Read(source); }
                    geometries.Add(name, value);
                }
                return value;
            }
        }

        internal static ImageSource ApplicationIcon
        {
            get
            {
                if (applicationIcon == null)
                {
                    using (Stream source = assembly.GetManifestResourceStream("AppIcon.ico"))
                    {
                        if (source == null) throw new InvalidDataException("Missing embedded app icon.");
                        BitmapDecoder decoder = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        applicationIcon = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First(); applicationIcon.Freeze();
                    }
                }
                return applicationIcon;
            }
        }

        internal static System.Drawing.Icon TrayIcon()
        {
            using (Stream source = assembly.GetManifestResourceStream("AppIcon.ico"))
            {
                if (source == null) throw new InvalidDataException("Missing embedded tray icon.");
                using (System.Drawing.Icon icon = new System.Drawing.Icon(source, 32, 32)) return (System.Drawing.Icon)icon.Clone();
            }
        }

        internal static System.Drawing.Bitmap MenuImage(string name)
        {
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen()) LucideSvg.Draw(drawing, Geometry(name), (Brush)new BrushConverter().ConvertFromString("#23354B"), 16, 16);
            RenderTargetBitmap bitmap = new RenderTargetBitmap(16,16,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (MemoryStream stream = new MemoryStream())
            {
                encoder.Save(stream); stream.Position = 0;
                using (System.Drawing.Bitmap decoded = new System.Drawing.Bitmap(stream)) return new System.Drawing.Bitmap(decoded);
            }
        }
    }
}
