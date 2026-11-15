using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OnlyFansControl
{
    internal sealed class CurveEditor : Window
    {
        private readonly CurveDocument document;
        private readonly StackPanel rows = new StackPanel();
        private readonly Canvas chart = new Canvas { Height = 180, ClipToBounds = true };
        private readonly TextBlock message = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 38, Margin = new Thickness(0,10,0,8) };
        private readonly Button save = new Button { Content = "Save curve", MinWidth = 110, IsDefault = true };
        private readonly Button add = new Button { Content = "Add point", Margin = new Thickness(0,8,0,0), HorizontalAlignment = HorizontalAlignment.Left };
        internal readonly List<TextBox> Temperatures = new List<TextBox>();
        internal readonly List<ComboBox> Levels = new List<ComboBox>();
        internal bool CanSave { get { return save.IsEnabled; } }
        internal bool Saved;
        private int draggedPoint = -1;
        private Vector dragOffset;
        private bool updatingPoint;

        internal CurveEditor(Window owner, CurveDocument document)
        {
            this.document = document; Owner = owner; Resources = owner.Resources;
            Title = "Edit fan curve"; Width = 620; Height = 740; MinWidth = 540; MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brush("AppBackgroundBrush"); Foreground = Brush("TextBrush"); FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
            DockPanel layout = new DockPanel { Margin = new Thickness(24) }; Content = layout;
            StackPanel footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
            footer.Children.Add(message);
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0,0,8,0) };
            cancel.Click += delegate { Close(); }; save.Click += delegate { SaveCurve(); };
            actions.Children.Add(cancel); actions.Children.Add(save); footer.Children.Add(actions);
            StackPanel header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
            header.Children.Add(new TextBlock { Text = document.Scope, FontSize = 22, FontWeight = FontWeights.SemiBold });
            header.Children.Add(new TextBlock { Text = "Drag a dot sideways to change temperature, or up/down to change fan level. Choose Save curve to apply your edits.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,6,0,10), Foreground = Brush("MutedTextBrush") });
            header.Children.Add(chart);
            header.Children.Add(new TextBlock { Text = "Temperature (°C) → fan level · Max protection at " + document.Settings.critical_temperature_c + " °C\nCooling hysteresis: " + document.Settings.hysteresis_c + " °C. Saving keeps the current fan mode.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,12), Foreground = Brush("MutedTextBrush") });
            Grid labels = RowGrid(); labels.Margin = new Thickness(0,0,0,6);
            labels.Children.Add(new TextBlock { Text = "Temperature ≥ °C", FontWeight = FontWeights.SemiBold });
            TextBlock levelLabel = new TextBlock { Text = "Fan level", FontWeight = FontWeights.SemiBold }; Grid.SetColumn(levelLabel,1); labels.Children.Add(levelLabel); header.Children.Add(labels);
            DockPanel editor = new DockPanel(); layout.Children.Add(editor);
            DockPanel.SetDock(add, Dock.Bottom); editor.Children.Add(add);
            editor.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            add.Click += delegate { AddPoint(); };
            foreach (CurvePoint point in document.Settings.curve) AppendRow(point.temperature_c.ToString(CultureInfo.InvariantCulture), point.NumericLevel == 8 ? "Max" : point.level);
            // Capture the canvas, not a dot: dots are recreated as the curve is redrawn.
            chart.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (draggedPoint < 0) return;
                if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
                MovePoint(draggedPoint, e.GetPosition(chart) - dragOffset); e.Handled = true;
            };
            chart.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            { if (draggedPoint >= 0) { EndDrag(); e.Handled = true; } };
            chart.LostMouseCapture += delegate { draggedPoint = -1; chart.Cursor = null; };
            Deactivated += delegate { EndDrag(); };
            Closed += delegate { EndDrag(); };
            chart.SizeChanged += delegate { EndDrag(); Refresh(); }; Refresh();
        }

        private Brush Brush(string name) { return (Brush)Resources[name]; }
        private static Grid RowGrid()
        {
            Grid grid = new Grid { Margin = new Thickness(0,0,0,6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) }); return grid;
        }
        private void AppendRow(string temperature, string level)
        {
            Grid row = RowGrid();
            TextBox input = new TextBox { Text = temperature, Padding = new Thickness(8,6,8,6), Margin = new Thickness(0,0,10,0), IsReadOnly = Temperatures.Count == 0 };
            ComboBox choice = new ComboBox { ItemsSource = new[] { "1", "2", "3", "4", "5", "6", "7", "Max" }, SelectedItem = level, Padding = new Thickness(8,6,8,6), Margin = new Thickness(0,0,10,0) };
            Button remove = new Button { Content = "Remove", Padding = new Thickness(8,6,8,6), IsEnabled = Temperatures.Count != 0 };
            System.Windows.Automation.AutomationProperties.SetName(input, "Temperature threshold");
            System.Windows.Automation.AutomationProperties.SetName(choice, "Fan level");
            row.Children.Add(input); Grid.SetColumn(choice,1); row.Children.Add(choice); Grid.SetColumn(remove,2); row.Children.Add(remove);
            Temperatures.Add(input); Levels.Add(choice); rows.Children.Add(row);
            input.TextChanged += delegate { Refresh(); }; choice.SelectionChanged += delegate { Refresh(); };
            remove.Click += delegate { Temperatures.Remove(input); Levels.Remove(choice); rows.Children.Remove(row); Refresh(); };
        }

        internal void AddPoint()
        {
            List<CurvePoint> points;
            if (!TryPoints(out points)) return;
            int next = points[points.Count - 1].temperature_c + 1;
            if (points.Count >= 16 || next > document.Settings.critical_temperature_c) return;
            AppendRow(next.ToString(CultureInfo.InvariantCulture), "Max"); Refresh();
        }

        private bool TryPoints(out List<CurvePoint> points)
        {
            points = new List<CurvePoint>();
            try
            {
                for (int i = 0; i < Temperatures.Count; i++)
                {
                    int temperature;
                    if (!int.TryParse(Temperatures[i].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out temperature)) throw new ArgumentException("Enter a whole-number temperature in every row.");
                    points.Add(new CurvePoint { temperature_c = temperature, level = Levels[i].SelectedItem as string });
                }
                document.Serialize(points); return true;
            }
            catch (Exception error) { message.Text = error.Message; return false; }
        }

        internal void Refresh()
        {
            if (updatingPoint) return;
            for (int i = 0; i < rows.Children.Count; i++) ((Button)((Grid)rows.Children[i]).Children[2]).IsEnabled = i > 0 && rows.Children.Count > 2;
            List<CurvePoint> points; bool valid = TryPoints(out points); save.IsEnabled = valid;
            add.IsEnabled = valid && points.Count < 16 && points[points.Count - 1].temperature_c < document.Settings.critical_temperature_c;
            message.Foreground = Brush(valid ? "MutedTextBrush" : "ErrorBrush");
            if (valid) message.Text = "Save updates " + document.Scope.ToLowerInvariant() + ". Smart auto reloads it on the next poll.";
            Draw(valid ? points : null);
        }

        internal Point GraphPosition(int temperature, int level)
        { return new Point(34 + (chart.ActualWidth - 52) * temperature / 100.0, 150 - (level - 1) * 18); }

        internal void MovePoint(int index, Point position)
        {
            List<CurvePoint> points;
            double width = chart.ActualWidth - 52;
            if (width <= 0 || index < 0 || index >= Temperatures.Count ||
                double.IsNaN(position.X) || double.IsNaN(position.Y) || !TryPoints(out points)) return;
            // Clamp before conversion, including when the mouse leaves the graph while captured.
            int temperature = (int)Math.Round(Math.Max(0, Math.Min(100, (position.X - 34) * 100 / width)));
            int level = (int)Math.Round(Math.Max(1, Math.Min(8, 1 + (150 - position.Y) / 18)));
            int minimumTemperature = index == 0 ? 0 : points[index - 1].temperature_c + 1;
            int maximumTemperature = index == 0 ? 0 : index + 1 < points.Count ? points[index + 1].temperature_c - 1 : document.Settings.critical_temperature_c;
            int minimumLevel = index == 0 ? 1 : points[index - 1].NumericLevel;
            int maximumLevel = index + 1 < points.Count ? points[index + 1].NumericLevel : 8;
            temperature = Math.Max(minimumTemperature, Math.Min(maximumTemperature, temperature));
            level = Math.Max(minimumLevel, Math.Min(maximumLevel, level));
            updatingPoint = true;
            try
            {
                Temperatures[index].Text = temperature.ToString(CultureInfo.InvariantCulture);
                Levels[index].SelectedItem = level == 8 ? "Max" : level.ToString(CultureInfo.InvariantCulture);
            }
            finally { updatingPoint = false; }
            Refresh();
            message.Text = "Point " + (index + 1) + ": " + temperature + " °C → " + (level == 8 ? "Max" : "level " + level) + ". Choose Save curve to apply.";
        }

        private void EndDrag()
        {
            draggedPoint = -1; chart.Cursor = null;
            if (chart.IsMouseCaptured) chart.ReleaseMouseCapture();
        }

        private void Draw(List<CurvePoint> points)
        {
            chart.Children.Clear(); double width = chart.ActualWidth - 52; if (width <= 0) return;
            Func<int,double> x = t => 34 + width * t / 100.0;
            Func<int,double> y = level => 150 - (level - 1) * 18;
            for (int level = 1; level <= 8; level++)
            {
                chart.Children.Add(new Line { X1 = x(0), X2 = x(100), Y1 = y(level), Y2 = y(level), Stroke = Brush("ChartGridBrush") });
                LabelAt(level == 8 ? "Max" : level.ToString(), 0, y(level)-9);
            }
            for (int temp = 0; temp <= 100; temp += 20) LabelAt(temp.ToString(), x(temp)-9, 157);
            double critical = x(document.Settings.critical_temperature_c);
            chart.Children.Add(new Line { X1 = critical, X2 = critical, Y1 = 12, Y2 = 150, Stroke = Brush("ErrorBrush"), StrokeDashArray = new DoubleCollection { 3,3 } });
            if (points == null) return;
            Polyline line = new Polyline { Stroke = Brush("AccentBrush"), StrokeThickness = 2.5 };
            int previous = points[0].NumericLevel;
            foreach (CurvePoint point in points)
            {
                line.Points.Add(new Point(x(point.temperature_c), y(previous)));
                line.Points.Add(new Point(x(point.temperature_c), y(point.NumericLevel))); previous = point.NumericLevel;
            }
            line.Points.Add(new Point(critical,y(previous))); line.Points.Add(new Point(critical,y(8))); line.Points.Add(new Point(x(100),y(8))); chart.Children.Add(line);
            for (int i = 0; i < points.Count; i++)
            {
                CurvePoint point = points[i];
                // Transparent padding makes the handle easy to grab without hiding nearby points.
                Grid handle = new Grid { Width = 22, Height = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll,
                    ToolTip = point.temperature_c + " °C → " + point.level + (i == 0 ? " · drag vertically (0 °C fixed)" : " · drag to edit") };
                handle.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = Brush("SurfaceBrush"), Stroke = Brush("AccentBrush"), StrokeThickness = 2 });
                handle.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
                {
                    Point mouse = e.GetPosition(chart);
                    int index = Enumerable.Range(0, points.Count).OrderBy(n => (mouse - GraphPosition(points[n].temperature_c, points[n].NumericLevel)).LengthSquared).First();
                    dragOffset = mouse - GraphPosition(points[index].temperature_c, points[index].NumericLevel);
                    if (chart.CaptureMouse()) { draggedPoint = index; chart.Cursor = Cursors.SizeAll; e.Handled = true; }
                };
                Canvas.SetLeft(handle,x(point.temperature_c)-11); Canvas.SetTop(handle,y(point.NumericLevel)-11); chart.Children.Add(handle);
            }
        }
        private void LabelAt(string text, double left, double top)
        { TextBlock label = new TextBlock { Text = text, FontSize = 11, Foreground = Brush("MutedTextBrush") }; Canvas.SetLeft(label,left); Canvas.SetTop(label,top); chart.Children.Add(label); }

        internal void SaveCurve()
        {
            List<CurvePoint> points; if (!TryPoints(out points)) { Refresh(); return; }
            try { document.Save(points); Saved = true; Close(); }
            catch (Exception error) { message.Text = "Unable to save: " + error.Message; message.Foreground = Brush("ErrorBrush"); }
        }
    }
}
