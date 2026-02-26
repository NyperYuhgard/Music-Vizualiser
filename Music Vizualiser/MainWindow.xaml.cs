using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Diagnostics; // Necesario para Stopwatch
using NAudio.Wave;
using NAudio.Dsp;
using NAudio.CoreAudioApi;
using Newtonsoft.Json;
using System.Runtime.InteropServices;

namespace Music_Vizualiser
{
    public class ConfigData
    {
        public double Sensibilidad { get; set; }
        public double BarWidth { get; set; }
        public double BarRadius { get; set; }
        public double BarsX { get; set; }
        public double BarsY { get; set; }
        public double BarsScale { get; set; }
        public int VisualMode { get; set; }
        public byte BarR { get; set; }
        public byte BarG { get; set; }
        public byte BarB { get; set; }
        public double LogoSize { get; set; }
        public double BgZoom { get; set; }
        public double BgScaleY { get; set; }
        public double BgPosX { get; set; }
        public double BgPosY { get; set; }
        public double BgBlur { get; set; }
        public bool LinkProportions { get; set; }
        public bool BumpBackground { get; set; }
        public bool ShowFPS { get; set; }
        public int MaxFPS { get; set; } // Nueva propiedad en config
        public double SensibilidadBg { get; set; }
        public string BackgroundPath { get; set; }
        public string LogoPath { get; set; }
        public List<TextConfig> Textos { get; set; } = new List<TextConfig>();
    }

    public class TextConfig
    {
        public string Content { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Size { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
    }

    public partial class MainWindow : Window
    {
        // Audio & Engine
        private WasapiLoopbackCapture capture;
        private float[] fftBuffer = new float[1024];
        private Complex[] fftComplex = new Complex[1024];
        private int sampleIndex = 0;
        private Rectangle[] bars = new Rectangle[64];

        // Performance & FPS Engine (Alta Precisión)
        private int actualFrameCount = 0;
        private double lastRenderTime = 0;
        private DateTime lastRealUpdate = DateTime.Now;
        private Stopwatch stopWatch = Stopwatch.StartNew();
        private string baseTitle = "Music Visualizer PRO v2.0";
        private int maxFPS = 60;

        // UI State
        private TextBox selectedTextBox = null;
        private bool isUpdatingUI = false;
        private string configPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.cfg");

        //Wine Check
        [DllImport("ntdll.dll", EntryPoint = "wine_get_version", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr wine_get_version();

        public static bool IsRunningOnWine()
        {
            try
            {
                return wine_get_version() != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            if (IsRunningOnWine())
            {
                MessageBox.Show(
                    "ADVERTENCIA: Se ha detectado que se está ejecutando este programa en una instalación de Linux con Wine/Proton. " +
                    "Es probable que el programa tenga errores o falle inesperadamente. " +
                    "No haga reportes de bugs si está ejecutando el programa bajo estas condiciones.",
                    "Compatibilidad Wine/Proton",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
            // Optimización de renderizado
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

            // Motor principal: Sincronizado con la tasa de refresco de la GPU
            CompositionTarget.Rendering += OnCompositionTargetRendering;

            this.Loaded += (s, e) => {
                SetupBars();
                RefreshAudioDevices();
                StartListening();
                LoadConfiguration();
            };
        }

        private bool IsUiReady => IsLoaded && sldSens != null && sldLogoSize != null && cmbAudioDevices != null && sldMaxFps != null;

        // --- MOTOR DE RENDERIZADO CON LÍMITE DE FPS ---
        private void OnCompositionTargetRendering(object sender, EventArgs e)
        {
            if (!IsUiReady) return;

            // --- PARTE 1: ACTUALIZACIÓN DEL CONTADOR (Siempre se ejecuta) ---
            var now = DateTime.Now;
            var totalElapsed = (now - lastRealUpdate).TotalSeconds;

            if (totalElapsed >= 1.0)
            {
                if (chkShowFPS != null && chkShowFPS.IsChecked == true)
                {
                    this.Title = $"{baseTitle} | REAL FPS: {actualFrameCount} | Limit: {maxFPS}";
                }
                else if (chkShowFPS != null && chkShowFPS.IsChecked == false)
                {
                    this.Title = baseTitle; // Restaurar título original si se apaga el check
                }

                actualFrameCount = 0;
                lastRealUpdate = now;
            }

            // --- PARTE 2: LÍMITE DE FPS (Decide si dibuja o no) ---
            double currentTime = stopWatch.Elapsed.TotalMilliseconds;
            double elapsedSinceLastFrame = currentTime - lastRenderTime;

            // Aplicamos el margen de tolerancia de 1.5ms para sincronizar con el monitor
            double minPeriod = (1000.0 / maxFPS) - 1.5;

            if (elapsedSinceLastFrame < minPeriod) return;

            // --- PARTE 3: RENDERIZADO (Solo si pasó el filtro del límite) ---
            lastRenderTime = currentTime;
            actualFrameCount++; // Solo contamos los frames que realmente se dibujaron

            UpdateVisualizerLogic();
        }

        private void SetupBars()
        {
            if (mainCanvas == null) return;
            mainCanvas.Children.Clear();
            for (int i = 0; i < bars.Length; i++)
            {
                bars[i] = new Rectangle
                {
                    Width = 15,
                    Height = 10,
                    RadiusX = 5,
                    RadiusY = 5,
                    Fill = Brushes.DeepSkyBlue
                };
                mainCanvas.Children.Add(bars[i]);
            }
        }

        // Renombrado para diferenciar de la llamada del timer antiguo
        private void UpdateVisualizerLogic()
        {
            if (bars[0] == null) return;

            float sensGral = (float)sldSens.Value;
            float bass = (fftBuffer[1] + fftBuffer[2] + fftBuffer[3]);
            int mode = cmbVisualMode.SelectedIndex;

            // --- ANIMACIÓN LOGO ---
            double targetLogo = 1.0 + Math.Min((bass * sensGral) / 30000, 0.35);
            logoScale.ScaleX += (targetLogo - logoScale.ScaleX) * 0.2;
            logoScale.ScaleY += (targetLogo - logoScale.ScaleY) * 0.2;

            // --- ANIMACIÓN FONDO ---
            double baseScaleX = sldZoom.Value;
            double baseScaleY = (chkVinculado.IsChecked == true) ? sldZoom.Value : sldScaleY.Value;
            double boost = (chkBumpBg.IsChecked == true) ? Math.Min((bass * (float)sldSensBg.Value) / 50000, 0.1) : 0;
            bgScale.ScaleX += ((baseScaleX + boost) - bgScale.ScaleX) * 0.15;
            bgScale.ScaleY += ((baseScaleY + boost) - bgScale.ScaleY) * 0.15;

            // --- ANIMACIÓN BARRAS ---
            for (int i = 0; i < bars.Length; i++)
            {
                double targetH = Math.Max(5, fftBuffer[i + 2] * sensGral);
                // Ajustamos el suavizado dinámicamente según los FPS actuales para que no se vea lento a 30fps
                double smoothFactor = (targetH > bars[i].Height) ? 0.35 : 0.15;
                bars[i].Height += (targetH - bars[i].Height) * smoothFactor;

                if (mode == 1) // CÍRCULO
                {
                    double angle = (i * 360.0 / bars.Length);
                    double radius = 150 * sldBarsScale.Value;
                    double angleRad = angle * Math.PI / 180.0;

                    double x = Math.Cos(angleRad) * radius;
                    double y = Math.Sin(angleRad) * radius;

                    Canvas.SetLeft(bars[i], x - (bars[i].Width / 2));
                    Canvas.SetTop(bars[i], y - (bars[i].Height / 2));
                    bars[i].RenderTransformOrigin = new Point(0.5, 0.5);
                    bars[i].RenderTransform = new RotateTransform(angle + 90);
                }
                else // LINEAL / ONDA
                {
                    bars[i].RenderTransformOrigin = new Point(0.5, 0.5);
                    bars[i].RenderTransform = Transform.Identity;
                    double totalW = bars.Length * (sldBarWidth.Value + 2);
                    double posX = (i * (sldBarWidth.Value + 2)) - (totalW / 2);
                    Canvas.SetLeft(bars[i], posX);

                    if (mode == 0) Canvas.SetTop(bars[i], -bars[i].Height);
                    else Canvas.SetTop(bars[i], -bars[i].Height / 2);
                }
            }
        }

        private void AnalyzeSample(float sample)
        {
            if (Math.Abs(sample) < 0.0001f) sample = 0;
            fftComplex[sampleIndex].X = (float)(sample * FastFourierTransform.HammingWindow(sampleIndex, 1024));
            fftComplex[sampleIndex].Y = 0;
            if (++sampleIndex >= 1024)
            {
                FastFourierTransform.FFT(true, 10, fftComplex);
                for (int i = 0; i < 1024; i++)
                    fftBuffer[i] = (float)Math.Sqrt(fftComplex[i].X * fftComplex[i].X + fftComplex[i].Y * fftComplex[i].Y);
                sampleIndex = 0;
            }
        }

        // --- MANEJADORES DE EVENTOS ---
        private void OnFpsLimitChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            maxFPS = (int)e.NewValue;
            if (txtMaxFps != null) txtMaxFps.Text = maxFPS.ToString();
        }

        private void OnAudioDeviceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isUpdatingUI || !IsUiReady) return;
            StartListening(cmbAudioDevices.SelectedItem as string);
        }

        private void OnRefreshAudioClick(object sender, RoutedEventArgs e) => RefreshAudioDevices();
        private void OnBackgroundAdjustChanged(object sender, EventArgs e) { if (IsUiReady && bgMove != null) { bgMove.X = sldPosX.Value; bgMove.Y = sldPosY.Value; } }
        private void OnBarsTransformChanged(object sender, EventArgs e)
        {
            if (!IsUiReady || barsMove == null) return;
            barsMove.X = sldBarsX.Value; barsMove.Y = sldBarsY.Value;
            barsScale.ScaleX = barsScale.ScaleY = (cmbVisualMode.SelectedIndex != 1) ? sldBarsScale.Value : 1.0;
        }

        private void OnBarStyleChanged(object sender, EventArgs e)
        {
            if (!IsUiReady) return;
            Color c = Color.FromRgb((byte)sldBarR.Value, (byte)sldBarG.Value, (byte)sldBarB.Value);
            Brush barBrush = new SolidColorBrush(c);
            foreach (var b in bars)
            {
                if (b == null) continue;
                b.Fill = barBrush;
                b.Width = sldBarWidth.Value;
                b.RadiusX = b.RadiusY = sldBarRadius.Value;
                if (chkEnableShadows.IsChecked == true)
                {
                    if (b.Effect == null) b.Effect = new DropShadowEffect { Color = c, BlurRadius = 15, ShadowDepth = 0, RenderingBias = RenderingBias.Performance };
                    else if (b.Effect is DropShadowEffect dse) dse.Color = c;
                }
                else b.Effect = null;
            }
        }

        private void OnLogoAdjustChanged(object sender, EventArgs e) { if (imgLogo != null) imgLogo.Width = imgLogo.Height = sldLogoSize.Value; }
        private void OnBlurValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (bgBlur != null) bgBlur.Radius = e.NewValue; }
        private void OnFpsCheckboxChanged(object sender, RoutedEventArgs e) { if (chkShowFPS.IsChecked != true) this.Title = baseTitle; }

        private void StartListening(string deviceName = null)
        {
            try
            {
                if (capture != null) { capture.StopRecording(); capture.Dispose(); }
                if (string.IsNullOrEmpty(deviceName) || deviceName == "Dispositivo por Defecto") capture = new WasapiLoopbackCapture();
                else
                {
                    var enumerator = new MMDeviceEnumerator();
                    var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    var selected = devices.FirstOrDefault(d => d.FriendlyName == deviceName);
                    capture = selected != null ? new WasapiLoopbackCapture(selected) : new WasapiLoopbackCapture();
                }
                capture.DataAvailable += (s, e) => { for (int i = 0; i < e.BytesRecorded; i += 4) AnalyzeSample(BitConverter.ToSingle(e.Buffer, i)); };
                capture.StartRecording();
            }
            catch (Exception ex) { MessageBox.Show("Error de Audio: " + ex.Message); }
        }

        private void RefreshAudioDevices()
        {
            if (cmbAudioDevices == null) return;
            isUpdatingUI = true;
            cmbAudioDevices.Items.Clear();
            cmbAudioDevices.Items.Add("Dispositivo por Defecto");
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var dev in devices) cmbAudioDevices.Items.Add(dev.FriendlyName);
            cmbAudioDevices.SelectedIndex = 0;
            isUpdatingUI = false;
        }

        // --- GESTIÓN DE CONFIGURACIÓN ---
        private void OnSaveConfigClick(object sender, RoutedEventArgs e) { SaveConfiguration(); MessageBox.Show("¡Configuración Guardada!"); }
        private void OnLoadConfigClick(object sender, RoutedEventArgs e) => LoadConfiguration();

        private void SaveConfiguration()
        {
            try
            {
                var config = new ConfigData
                {
                    Sensibilidad = sldSens.Value,
                    BarWidth = sldBarWidth.Value,
                    BarRadius = sldBarRadius.Value,
                    BarsX = sldBarsX.Value,
                    BarsY = sldBarsY.Value,
                    BarsScale = sldBarsScale.Value,
                    VisualMode = cmbVisualMode.SelectedIndex,
                    BarR = (byte)sldBarR.Value,
                    BarG = (byte)sldBarG.Value,
                    BarB = (byte)sldBarB.Value,
                    LogoSize = sldLogoSize.Value,
                    BgZoom = sldZoom.Value,
                    BgScaleY = sldScaleY.Value,
                    BgPosX = sldPosX.Value,
                    BgPosY = sldPosY.Value,
                    BgBlur = sldBlur.Value,
                    LinkProportions = chkVinculado.IsChecked ?? true,
                    BumpBackground = chkBumpBg.IsChecked ?? false,
                    ShowFPS = chkShowFPS.IsChecked ?? false,
                    MaxFPS = (int)sldMaxFps.Value, // Guardamos FPS
                    SensibilidadBg = sldSensBg.Value,
                    BackgroundPath = (imgBackground.Source as BitmapImage)?.UriSource?.LocalPath ?? "",
                    LogoPath = (imgLogo.Source as BitmapImage)?.UriSource?.LocalPath ?? ""
                };
                foreach (TextBox tb in textCanvas.Children)
                {
                    var col = ((SolidColorBrush)tb.Foreground).Color;
                    config.Textos.Add(new TextConfig { Content = tb.Text, X = Canvas.GetLeft(tb), Y = Canvas.GetTop(tb), Size = tb.FontSize, R = col.R, G = col.G, B = col.B });
                }
                File.WriteAllText(configPath, JsonConvert.SerializeObject(config, Formatting.Indented));
            }
            catch { }
        }

        private void LoadConfiguration()
        {
            if (!File.Exists(configPath)) return;
            try
            {
                var config = JsonConvert.DeserializeObject<ConfigData>(File.ReadAllText(configPath));
                isUpdatingUI = true;
                sldSens.Value = config.Sensibilidad; sldBarWidth.Value = config.BarWidth; sldBarRadius.Value = config.BarRadius;
                sldBarsX.Value = config.BarsX; sldBarsY.Value = config.BarsY; sldBarsScale.Value = config.BarsScale;
                cmbVisualMode.SelectedIndex = config.VisualMode;
                sldBarR.Value = config.BarR; sldBarG.Value = config.BarG; sldBarB.Value = config.BarB;
                sldLogoSize.Value = config.LogoSize; sldZoom.Value = config.BgZoom; sldScaleY.Value = config.BgScaleY;
                sldPosX.Value = config.BgPosX; sldPosY.Value = config.BgPosY; sldBlur.Value = config.BgBlur;
                chkVinculado.IsChecked = config.LinkProportions; chkBumpBg.IsChecked = config.BumpBackground;
                chkShowFPS.IsChecked = config.ShowFPS;
                sldMaxFps.Value = config.MaxFPS > 0 ? config.MaxFPS : 60; // Cargamos FPS
                sldSensBg.Value = config.SensibilidadBg;
                if (File.Exists(config.BackgroundPath)) imgBackground.Source = new BitmapImage(new Uri(config.BackgroundPath));
                if (File.Exists(config.LogoPath)) imgLogo.Source = new BitmapImage(new Uri(config.LogoPath));
                textCanvas.Children.Clear();
                foreach (var t in config.Textos)
                {
                    var tb = CrearTextBox(t.Content, t.Size, Color.FromRgb(t.R, t.G, t.B));
                    textCanvas.Children.Add(tb); Canvas.SetLeft(tb, t.X); Canvas.SetTop(tb, t.Y);
                }
                isUpdatingUI = false;
                OnBarStyleChanged(null, null); OnBarsTransformChanged(null, null); OnLogoAdjustChanged(null, null);
                if (bgBlur != null) bgBlur.Radius = sldBlur.Value;
            }
            catch { isUpdatingUI = false; }
        }

        // --- TEXTOS ---
        private TextBox CrearTextBox(string texto, double size, Color color)
        {
            TextBox tb = new TextBox { Text = texto, FontSize = size, Foreground = new SolidColorBrush(color), Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontWeight = FontWeights.Bold };
            tb.GotFocus += (s, e) => { selectedTextBox = tb; ActualizarInterfazTexto(tb); };
            return tb;
        }

        private void ActualizarInterfazTexto(TextBox tb)
        {
            if (tb == null) return;
            isUpdatingUI = true;
            txtSelectedInfo.Text = tb.Text; sldFontSize.Value = tb.FontSize;
            sldTextX.Value = Canvas.GetLeft(tb); sldTextY.Value = Canvas.GetTop(tb);
            if (tb.Foreground is SolidColorBrush scb) { sldColR.Value = scb.Color.R; sldColG.Value = scb.Color.G; sldColB.Value = scb.Color.B; }
            isUpdatingUI = false;
        }

        private void OnTextPropChanged(object sender, EventArgs e)
        {
            if (isUpdatingUI || selectedTextBox == null) return;
            selectedTextBox.FontSize = sldFontSize.Value;
            selectedTextBox.Foreground = new SolidColorBrush(Color.FromRgb((byte)sldColR.Value, (byte)sldColG.Value, (byte)sldColB.Value));
            Canvas.SetLeft(selectedTextBox, sldTextX.Value); Canvas.SetTop(selectedTextBox, sldTextY.Value);
        }

        private void OnAddTextClick(object sender, RoutedEventArgs e) { var tb = CrearTextBox("NUEVO TEXTO", 30, Colors.White); textCanvas.Children.Add(tb); Canvas.SetLeft(tb, 100); Canvas.SetTop(tb, 100); selectedTextBox = tb; ActualizarInterfazTexto(tb); }
        private void OnDeleteTextClick(object sender, RoutedEventArgs e) { if (selectedTextBox != null) { textCanvas.Children.Remove(selectedTextBox); selectedTextBox = null; txtSelectedInfo.Text = "Ninguno"; } }
        private void OnLoadBackgroundClick(object sender, RoutedEventArgs e) { var ofd = new Microsoft.Win32.OpenFileDialog(); if (ofd.ShowDialog() == true) imgBackground.Source = new BitmapImage(new Uri(ofd.FileName)); }
        private void OnLoadLogoClick(object sender, RoutedEventArgs e) { var ofd = new Microsoft.Win32.OpenFileDialog(); if (ofd.ShowDialog() == true) imgLogo.Source = new BitmapImage(new Uri(ofd.FileName)); }
        private void OnWindowKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Insert) OnAddTextClick(null, null); }

        // --- TOGGLES ---
        private void OnToggleEscenarioClick(object sender, RoutedEventArgs e) => panelEscenario.Visibility = panelEscenario.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        private void OnToggleVisualizerClick(object sender, RoutedEventArgs e) => panelVisualizer.Visibility = panelVisualizer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        private void OnToggleTextClick(object sender, RoutedEventArgs e) => panelTextProp.Visibility = panelTextProp.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        protected override void OnClosed(EventArgs e)
        {
            SaveConfiguration();
            if (capture != null) { capture.StopRecording(); capture.Dispose(); }
            base.OnClosed(e);
        }
    }
}