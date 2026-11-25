using System.Windows;
using System.Windows.Media;

namespace SeaView;

sealed class Ocean : FrameworkElement
{
    const int WidthUnits = 1280;
    const int HeightUnits = 720;
    readonly DrawingGroup background;
    readonly DrawingGroup[] fishShapes;
    readonly Fish[] fish;
    readonly (double X, double Y, double Height, double Phase)[] plants;
    readonly (double X, double Y, double Radius)[] sand;
    readonly Brush sandBrush = Ink("#80E4C690");
    readonly Pen[] seaweed =
    [
        new(Ink("#43846F"), 3), new(Ink("#376C61"), 5), new(Ink("#24554E"), 7)
    ];
    double time, drift, response, tailPhase;
    Settings settings = new();

    record Fish(double Y, double Size, double Speed, double Phase, int Species)
    {
        public double X { get; set; }
    }

    public Ocean()
    {
        ClipToBounds = true;
        var random = Random.Shared;
        background = MakeBackground();
        fishShapes = [MakeFish("#FFB44B", "#AD561E", "#FFE7A5"),
            MakeFish("#80D4D3", "#2A7295", "#C0F8EC"),
            MakeFish("#EAC6A5", "#AD654D", "#FCE1C7")];
        fish = Enumerable.Range(0, 60).Select(i => new Fish(random.Next(115, 575),
            random.NextDouble() * 0.85 + 0.35, (i % 2 == 0 ? 1 : -1) * random.Next(20, 62),
            random.NextDouble() * Math.Tau, i % fishShapes.Length)
            { X = random.Next(WidthUnits) }).ToArray();
        plants = Enumerable.Range(0, 25).Select(i =>
        {
            double x = i % 2 == 0 ? random.Next(10, 380) : random.Next(900, 1280);
            return (x, (double)random.Next(605, 710), (double)random.Next(38, 180),
                random.NextDouble() * Math.Tau);
        }).OrderBy(plant => plant.Item2).ToArray();
        sand = Enumerable.Range(0, 200).Select(_ => (random.NextDouble() * WidthUnits,
            (double)random.Next(575, HeightUnits), random.NextDouble() * 1.3 + 0.35)).ToArray();
        foreach (var pen in seaweed) pen.Freeze();
    }

    public void Advance(double dt, float audio, Settings updated)
    {
        settings = updated;
        time += dt;
        drift += dt * 13 * settings.Current;
        double target = Math.Clamp(audio * 4 * settings.Sensitivity, 0, 2);
        response += (target - response) * (1 - Math.Exp(-dt * 5));
        tailPhase += dt * (0.9 + response);
        int count = Math.Clamp(settings.Fish, 0, fish.Length);
        for (int i = 0; i < count; i++)
        {
            var swimmer = fish[i];
            swimmer.X = Wrap(swimmer.X + 120 + swimmer.Speed * dt * (1 + response), WidthUnits + 240) - 120;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        double scale = Math.Max(ActualWidth / WidthUnits, ActualHeight / HeightUnits);
        dc.PushTransform(new TranslateTransform((ActualWidth - WidthUnits * scale) / 2,
            (ActualHeight - HeightUnits * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawDrawing(background);
        for (int i = 0; i < Math.Clamp(settings.Fish, 0, fish.Length); i++)
        {
            var swimmer = fish[i];
            double wave = tailPhase + swimmer.Phase;
            double y = swimmer.Y + Math.Sin(time * 0.7 + swimmer.Phase) * (8 + response * 7);
            double horizontal = Math.Sign(swimmer.Speed) * swimmer.Size * (0.94 + Math.Sin(wave * 3) * 0.06);
            dc.PushTransform(new TranslateTransform(swimmer.X, y));
            dc.PushTransform(new RotateTransform(Math.Cos(time * 0.7 + swimmer.Phase) * 5));
            dc.PushTransform(new ScaleTransform(horizontal, swimmer.Size));
            dc.DrawDrawing(fishShapes[swimmer.Species]);
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }

        for (int i = 0; i < plants.Length; i++)
        {
            var plant = plants[i];
            double bend = 15 + Math.Sin(time * 0.7 + plant.Phase) * 14 * settings.Current;
            var blade = new StreamGeometry();
            using (var path = blade.Open())
            {
                path.BeginFigure(new Point(plant.X, plant.Y), false, false);
                path.BezierTo(new Point(plant.X - 13, plant.Y - plant.Height * 0.4),
                    new Point(plant.X + bend * 0.3, plant.Y - plant.Height * 0.7),
                    new Point(plant.X + bend, plant.Y - plant.Height), true, true);
            }
            blade.Freeze();
            dc.DrawGeometry(null, seaweed[i % seaweed.Length], blade);
        }

        for (int i = 0; i < Math.Clamp(settings.Sand, 0, sand.Length); i++)
        {
            var grain = sand[i];
            double x = Wrap(grain.X + drift * (0.7 + grain.Radius * 0.4), WidthUnits);
            double y = grain.Y + Math.Sin(time * 0.5 + i) * 3 * settings.Current;
            dc.DrawEllipse(sandBrush, null, new Point(x, y), grain.Radius, grain.Radius * 0.6);
        }
        dc.Pop();
        dc.Pop();
    }

    static double Wrap(double position, double span) => (position % span + span) % span;

    static DrawingGroup MakeBackground()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(Gradient("#19788B", "#071F31"), null, new Rect(0, 0, WidthUnits, HeightUnits));
            dc.DrawGeometry(Ink("#224BB2AF"), null, Geometry.Parse(
                "M 215,0 L 280,0 790,572 552,550 Z M 355,0 L 386,0 1020,560 861,540 Z M 110,0 L 143,0 475,565 362,535 Z"));
            dc.DrawGeometry(Gradient("#32636A", "#102B39"), null, Geometry.Parse(
                "M 0,448 Q 170,387 310,497 T 650,486 Q 990,398 1280,438 L 1280,720 0,720 Z"));
            dc.DrawGeometry(Gradient("#809579", "#536959"), null, Geometry.Parse(
                "M 0,565 Q 213,525 450,568 T 890,552 Q 1093,523 1280,560 L 1280,720 0,720 Z"));
            var random = new Random(17);
            var pebble = Ink("#45364C46");
            for (int i = 0; i < 170; i++)
            {
                double radius = random.NextDouble() * 4 + 0.6;
                dc.DrawEllipse(pebble, null, new Point(random.Next(WidthUnits), random.Next(575, HeightUnits)),
                    radius * 1.9, radius);
            }
        }
        drawing.Freeze();
        return drawing;
    }

    static DrawingGroup MakeFish(string light, string dark, string accent)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawGeometry(Ink(dark), null, Geometry.Parse(
                "M -24,-2 Q -43,-18 -62,-22 L -56,0 -62,21 Q -40,14 -24,4 Z M -21,-12 Q -24,-29 8,-23 L 22,-10 Z"));
            dc.DrawGeometry(Gradient(light, dark), null, Geometry.Parse(
                "M -43,0 Q -11,-29 17,-16 Q 35,-12 42,0 Q 30,15 9,17 Q -14,20 -43,0 Z"));
            dc.DrawGeometry(Ink(accent), null, Geometry.Parse(
                "M -24,-7 Q -15,-16 -7,-16 Q -14,-3 -4,15 L -12,13 Q -22,3 -24,-7 Z M 10,1 Q 3,14 -12,16 L -1,2 Z"));
            dc.DrawEllipse(Ink("#101E27"), null, new Point(26, -4), 3.6, 3.6);
            dc.DrawEllipse(Ink("#FFF6DA"), null, new Point(27, -5), 1.2, 1.2);
        }
        drawing.Freeze();
        return drawing;
    }

    static Brush Ink(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    static Brush Gradient(string top, string bottom)
    {
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top),
            (Color)ColorConverter.ConvertFromString(bottom), 90);
        brush.Freeze();
        return brush;
    }
}
