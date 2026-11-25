using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SeaView;

record Settings(int Fps = 30, int Fish = 18, int Sand = 80, double Current = 1,
    double Sensitivity = 1, string? Device = null)
{
    static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SeaView", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path)) ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path + ".tmp", JsonSerializer.Serialize(this));
        File.Move(Path + ".tmp", Path, true);
    }
}

class Program
{
    [STAThread]
    static void Main() => new Application().Run(new Viewer());
}

sealed class Viewer : Window
{
    readonly Ocean ocean = new();
    readonly DispatcherTimer timer = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly TextBlock status = new() { Margin = new Thickness(12, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel toolbar = new() { Orientation = Orientation.Horizontal };
    Settings settings = Settings.Load();
    WasapiLoopbackCapture? capture;
    MMDevice? endpoint;
    float level;
    long lastAudio;
    double previous;
    bool fullScreen;
    WindowState previousState;
    ResizeMode previousResize;

    public Viewer()
    {
        Title = "SeaView";
        Width = 1080;
        Height = 680;
        MinWidth = 440;
        MinHeight = 300;
        var layout = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(toolbar);
        AddButton(toolbar, "設定", Configure);
        AddButton(toolbar, "全画面  F11", ToggleFullscreen);
        AddButton(toolbar, "終了", Close);
        toolbar.Children.Add(status);
        layout.Children.Add(ocean);
        Content = layout;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F11) ToggleFullscreen();
            if (e.Key == Key.Escape && fullScreen) ToggleFullscreen();
            if (e.Key == Key.F2) Configure();
        };
        timer.Tick += (_, _) =>
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = Math.Min(now - previous, 0.15);
            previous = now;
            if (WindowState == WindowState.Minimized) return;
            float audio = Environment.TickCount64 - Interlocked.Read(ref lastAudio) < 250
                ? Volatile.Read(ref level) : 0;
            ocean.Advance(dt, audio, settings);
        };
        Loaded += (_, _) => { ApplyFrameRate(); StartAudio(); timer.Start(); };
        Closed += (_, _) => { timer.Stop(); StopAudio(); };
    }

    void ApplyFrameRate() =>
        timer.Interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(settings.Fps, 15, 60));

    static void AddButton(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(4) };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    void ToggleFullscreen()
    {
        if (!fullScreen)
        {
            previousState = WindowState;
            previousResize = ResizeMode;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = previousResize;
            WindowState = previousState;
        }
        fullScreen = !fullScreen;
        toolbar.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        Focus();
    }

    void StopAudio()
    {
        var oldCapture = capture;
        capture = null;
        oldCapture?.Dispose();
        endpoint?.Dispose();
        endpoint = null;
        Volatile.Write(ref level, 0);
        Interlocked.Exchange(ref lastAudio, 0);
    }

    void StartAudio()
    {
        StopAudio();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            endpoint = settings.Device is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(settings.Device);
            var current = new WasapiLoopbackCapture(endpoint);
            capture = current;
            var format = current.WaveFormat;
            int bytesPerSample = format.BitsPerSample / 8;
            Func<byte[], int, float> readSample = (format.Encoding, format.BitsPerSample) switch
            {
                (WaveFormatEncoding.IeeeFloat, 32) => BitConverter.ToSingle,
                (WaveFormatEncoding.Pcm, 8) => (buffer, offset) => (buffer[offset] - 128) / 128f,
                (WaveFormatEncoding.Pcm, 16) => (buffer, offset) => BitConverter.ToInt16(buffer, offset) / 32768f,
                (WaveFormatEncoding.Pcm, 24) => (buffer, offset) =>
                    ((buffer[offset] << 8) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 24))
                    / 2147483648f,
                (WaveFormatEncoding.Pcm, 32) => (buffer, offset) => BitConverter.ToInt32(buffer, offset) / 2147483648f,
                _ => throw new NotSupportedException($"Unsupported audio format: {format}")
            };
            current.DataAvailable += (_, e) =>
            {
                double squares = 0;
                int count = e.BytesRecorded / bytesPerSample;
                for (int i = 0; i < count; i++)
                {
                    float sample = readSample(e.Buffer, i * bytesPerSample);
                    squares += sample * sample;
                }
                float rms = count == 0 ? 0 : (float)Math.Sqrt(squares / count);
                Volatile.Write(ref level, rms);
                Interlocked.Exchange(ref lastAudio, Environment.TickCount64);
            };
            current.RecordingStopped += (_, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (capture != current) return;
                StopAudio();
                status.Text = "音声取得が停止しました。設定から再接続できます。";
            }));
            current.StartRecording();
            status.Text = endpoint.FriendlyName;
        }
        catch (Exception error)
        {
            StopAudio();
            status.Text = "音声に接続できません。設定で出力先を選び直してください。";
            Debug.WriteLine(error);
        }
    }

    void Configure()
    {
        var dialog = new Window { Title = "SeaView の設定", Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize };
        var controls = new StackPanel { Margin = new Thickness(18), Width = 360 };
        dialog.Content = controls;
        var devices = new List<(string? Id, string Name)> { (null, "Windows の既定の再生デバイス") };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device) devices.Add((device.ID, device.FriendlyName));
            }
        }
        catch (Exception error) { Debug.WriteLine(error); }
        controls.Children.Add(new TextBlock { Text = "再生デバイス" });
        var devicePicker = new ComboBox { ItemsSource = devices.Select(d => d.Name).ToArray(),
            SelectedIndex = Math.Max(0, devices.FindIndex(d => d.Id == settings.Device)),
            Margin = new Thickness(0, 5, 0, 10) };
        controls.Children.Add(devicePicker);
        var fps = SliderFor("フレームレート上限", 15, 60, settings.Fps, 1);
        var fish = SliderFor("魚の数", 0, 60, settings.Fish, 1);
        var sand = SliderFor("砂の粒子数", 0, 200, settings.Sand, 1);
        var current = SliderFor("海流の強さ", 0, 2, settings.Current, 0.1);
        var sensitivity = SliderFor("音への反応", 0, 3, settings.Sensitivity, 0.1);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        controls.Children.Add(buttons);
        AddButton(buttons, "キャンセル", dialog.Close);
        AddButton(buttons, "適用", () =>
        {
            var updated = new Settings((int)fps.Value, (int)fish.Value, (int)sand.Value,
                current.Value, sensitivity.Value, devices[Math.Max(0, devicePicker.SelectedIndex)].Id);
            try { updated.Save(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(dialog, "設定を保存できませんでした。", "SeaView");
                return;
            }
            settings = updated;
            ApplyFrameRate();
            StartAudio();
            dialog.Close();
        });
        dialog.ShowDialog();

        Slider SliderFor(string name, double min, double max, double value, double step)
        {
            var label = new TextBlock();
            var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = step,
                IsSnapToTickEnabled = true, Value = double.IsFinite(value) ? value : min,
                Margin = new Thickness(0, 3, 0, 8) };
            label.Text = $"{name}   {slider.Value:0.#}";
            slider.ValueChanged += (_, _) => label.Text = $"{name}   {slider.Value:0.#}";
            controls.Children.Add(label);
            controls.Children.Add(slider);
            return slider;
        }
    }
}
