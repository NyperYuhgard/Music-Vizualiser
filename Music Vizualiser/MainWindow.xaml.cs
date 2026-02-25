using System;
using System.Collections.Generic; // Necesario para listas
using System.IO;                // Necesario para archivos
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using NAudio.Wave;
using NAudio.Dsp;
using Newtonsoft.Json; // Requiere instalar el paquete NuGet Newtonsoft.Json

namespace Music_Vizualiser
{
    // Clase para estructurar los datos del archivo CFG
    public class ConfigData
    {
        public double Sensibilidad { get; set; }
        public double BarWidth { get; set; }
        public double BarRadius { get; set; }
        public double BarsX { get; set; }
        public double BarsY { get; set; }
        public double BarsScale { get; set; }
        public double LogoSize { get; set; }
        public double BgZoom { get; set; }
        public double BgScaleY { get; set; }
        public double BgPosX { get; set; }   
        public double BgPosY { get; set; }   
        public double BgBlur { get; set; }
        public bool LinkProportions { get; set; }
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
        private WasapiLoopbackCapture capture;
        private DispatcherTimer timer;
        private float[] fftBuffer = new float[1024];
        private Complex[] fftComplex = new Complex[1024];
        private int sampleIndex = 0;
        private Rectangle[] bars = new Rectangle[32];
        private TextBox selectedTextBox = null;
        private bool isUpdatingUI = false;
        private string configPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.cfg");

        public MainWindow()
        {
            InitializeComponent();
            this.AllowDrop = true;
            this.Drop += MainWindow_Drop;

            this.Loaded += (s, e) => {
                SetupBars();
                StartListening();
                LoadConfiguration(); // Cargar al iniciar
                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                timer.Tick += UpdateVisualizer;
                timer.Start();
            };
        }

        private bool IsUiReady => IsLoaded && sldSens != null;

        // --- SISTEMA DE GUARDADO Y CARGA (TEXTO PLANO CFG) ---
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
                    LogoSize = sldLogoSize.Value,
                    BgZoom = sldZoom.Value,
                    BgScaleY = sldScaleY.Value,
                    BgPosX = sldPosX.Value, 
                    BgPosY = sldPosY.Value,
                    BgBlur = sldBlur.Value,
                    LinkProportions = chkVinculado.IsChecked ?? true,
                    BackgroundPath = (imgBackground.Source as BitmapImage)?.UriSource.LocalPath,
                    LogoPath = (imgLogo.Source as BitmapImage)?.UriSource.LocalPath
                };

                foreach (TextBox tb in textCanvas.Children)
                {
                    var color = (tb.Foreground as SolidColorBrush).Color;
                    config.Textos.Add(new TextConfig
                    {
                        Content = tb.Text,
                        X = Canvas.GetLeft(tb),
                        Y = Canvas.GetTop(tb),
                        Size = tb.FontSize,
                        R = color.R,
                        G = color.G,
                        B = color.B
                    });
                }

                string json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(configPath, json);
            }
            catch (Exception ex) { Console.WriteLine("Error al guardar: " + ex.Message); }
        }

        private void LoadConfiguration()
        {
            if (!File.Exists(configPath)) return;
            try
            {
                string json = File.ReadAllText(configPath);
                var config = JsonConvert.DeserializeObject<ConfigData>(json);
                if (config == null) return;

                isUpdatingUI = true;

                // 1. Aplicar Sliders (Estos siempre se cargan)
                sldSens.Value = config.Sensibilidad;
                sldBarWidth.Value = config.BarWidth;
                sldBarRadius.Value = config.BarRadius;
                sldBarsX.Value = config.BarsX;
                sldBarsY.Value = config.BarsY;
                sldBarsScale.Value = config.BarsScale;
                sldLogoSize.Value = config.LogoSize;
                sldZoom.Value = config.BgZoom;
                sldScaleY.Value = config.BgScaleY;
                sldPosX.Value = config.BgPosX; 
                sldPosY.Value = config.BgPosY; 
                sldBlur.Value = config.BgBlur;
                if (chkVinculado != null)
                    chkVinculado.IsChecked = config.LinkProportions;

                // 2. Cargar Imágenes (Solo si la ruta es válida y el archivo existe)
                try
                {
                    if (!string.IsNullOrEmpty(config.BackgroundPath) && File.Exists(config.BackgroundPath))
                    {
                        imgBackground.Source = new BitmapImage(new Uri(config.BackgroundPath));
                    }

                    if (!string.IsNullOrEmpty(config.LogoPath) && File.Exists(config.LogoPath))
                    {
                        imgLogo.Source = new BitmapImage(new Uri(config.LogoPath));
                    }
                }
                catch (Exception imgEx)
                {
                    // Si hay un error con las imágenes, lo ignoramos para que el resto cargue
                    Console.WriteLine("Error cargando imágenes: " + imgEx.Message);
                }

                // 3. Recrear Textos (Siempre se cargan)
                textCanvas.Children.Clear();
                if (config.Textos != null)
                {
                    foreach (var t in config.Textos)
                    {
                        TextBox tb = CrearTextBox(t.Content, t.Size, Color.FromRgb(t.R, t.G, t.B));
                        textCanvas.Children.Add(tb);
                        Canvas.SetLeft(tb, t.X);
                        Canvas.SetTop(tb, t.Y);
                    }
                }

                isUpdatingUI = false;
                ActualizarTodo(); // Refresca visualmente con los nuevos sliders
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al leer el archivo de configuración: " + ex.Message);
            }
        }

        private void ActualizarTodo()
        {
            // Disparar eventos manualmente para refrescar transformaciones
            BackgroundAdjust_Changed(null, null);
            BarsTransform_Changed(null, null);
            BarStyle_Changed(null, null);
        }

        // --- VISUALIZADOR Y TEXTOS ---
        private void SetupBars()
        {
            if (mainCanvas == null) return;
            mainCanvas.Children.Clear();
            for (int i = 0; i < bars.Length; i++)
            {
                bars[i] = new Rectangle
                {
                    Width = 25,
                    Height = 10,
                    Fill = Brushes.DeepSkyBlue,
                    RadiusX = 5,
                    RadiusY = 5,
                    Effect = new DropShadowEffect { Color = Colors.Cyan, BlurRadius = 15, ShadowDepth = 0 }
                };
                mainCanvas.Children.Add(bars[i]);
                Canvas.SetLeft(bars[i], i * 27);
            }
        }

        private TextBox CrearTextBox(string texto, double size, Color color)
        {
            TextBox tb = new TextBox
            {
                Text = texto,
                FontSize = size,
                Foreground = new SolidColorBrush(color),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontWeight = FontWeights.Bold,
                MinWidth = 50
            };
            tb.PreviewMouseLeftButtonDown += (s, e) => { selectedTextBox = tb; ActualizarInterfazTexto(tb); };
            return tb;
        }

        private void AddDynamicText()
        {
            if (textCanvas == null) return;
            TextBox tb = CrearTextBox("EDITAME", 30, Colors.White);
            textCanvas.Children.Add(tb);
            Canvas.SetLeft(tb, 400); Canvas.SetTop(tb, 300);
            selectedTextBox = tb;
            ActualizarInterfazTexto(tb);
        }

        private void ActualizarInterfazTexto(TextBox tb)
        {
            if (txtSelectedInfo == null || tb == null) return;
            isUpdatingUI = true;
            txtSelectedInfo.Text = tb.Text;
            sldFontSize.Value = tb.FontSize;
            sldTextX.Value = Canvas.GetLeft(tb);
            sldTextY.Value = Canvas.GetTop(tb);
            if (tb.Foreground is SolidColorBrush scb) { sldColR.Value = scb.Color.R; sldColG.Value = scb.Color.G; sldColB.Value = scb.Color.B; }
            isUpdatingUI = false;
        }

        private void TextProp_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (isUpdatingUI || !IsUiReady || selectedTextBox == null) return;
            selectedTextBox.FontSize = sldFontSize.Value;
            selectedTextBox.Foreground = new SolidColorBrush(Color.FromRgb((byte)sldColR.Value, (byte)sldColG.Value, (byte)sldColB.Value));
            Canvas.SetLeft(selectedTextBox, sldTextX.Value);
            Canvas.SetTop(selectedTextBox, sldTextY.Value);
        }

        // --- EVENTOS Y AUDIO (Mantenidos igual) ---
        private void StartListening()
        {
            try
            {
                capture = new WasapiLoopbackCapture();
                capture.DataAvailable += (s, e) => { for (int i = 0; i < e.BytesRecorded; i += 4) AnalyzeSample(BitConverter.ToSingle(e.Buffer, i)); };
                capture.StartRecording();
            }
            catch (Exception ex) { MessageBox.Show("Error de Audio: " + ex.Message); }
        }

        private void AnalyzeSample(float sample)
        {
            fftComplex[sampleIndex].X = (float)(sample * FastFourierTransform.HammingWindow(sampleIndex, 1024));
            fftComplex[sampleIndex].Y = 0;
            if (++sampleIndex >= 1024)
            {
                FastFourierTransform.FFT(true, 10, fftComplex);
                for (int i = 0; i < 1024; i++) fftBuffer[i] = (float)Math.Sqrt(fftComplex[i].X * fftComplex[i].X + fftComplex[i].Y * fftComplex[i].Y);
                sampleIndex = 0;
            }
        }

        private void UpdateVisualizer(object sender, EventArgs e)
        {
            if (!IsUiReady || bars == null || bars[0] == null) return;
            float sens = (float)sldSens.Value;
            if (logoScale != null)
            {
                float bass = (fftBuffer[1] + fftBuffer[2]) * sens;
                double target = 1.0 + Math.Min(bass / 20000, 0.4);
                logoScale.ScaleX += (target - logoScale.ScaleX) * 0.2;
                logoScale.ScaleY += (target - logoScale.ScaleY) * 0.2;
            }
            for (int i = 0; i < bars.Length; i++)
            {
                double h = Math.Max(5, fftBuffer[i + 2] * sens);
                bars[i].Height += (h - bars[i].Height) * 0.2;
                Canvas.SetTop(bars[i], -bars[i].Height);
            }
        }

        private void BtnToggleEscenario_Click(object sender, RoutedEventArgs e) { if (panelAjustes != null) panelAjustes.Visibility = panelAjustes.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; }
        private void BtnToggleBars_Click(object sender, RoutedEventArgs e) { if (panelBars != null) panelBars.Visibility = panelBars.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; }
        private void BtnToggleText_Click(object sender, RoutedEventArgs e) { if (panelTextProp != null) panelTextProp.Visibility = panelTextProp.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; }

        private void BarsTransform_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsUiReady || barsMove == null) return;
            barsMove.X = sldBarsX.Value; barsMove.Y = sldBarsY.Value;
            barsScale.ScaleX = barsScale.ScaleY = sldBarsScale.Value;
        }

        private void BarStyle_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsUiReady || bars == null) return;
            foreach (var b in bars) { if (b != null) { b.Width = sldBarWidth.Value; b.RadiusX = b.RadiusY = sldBarRadius.Value; } }
        }

        private void LogoAdjust_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (imgLogo != null) imgLogo.Width = imgLogo.Height = sldLogoSize.Value; }
        private void SldBlur_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (bgBlur != null) bgBlur.Radius = e.NewValue; }
        private void BackgroundAdjust_Changed(object sender, EventArgs e)
        {
            if (!IsUiReady || bgScale == null) return;
            bgScale.ScaleX = sldZoom.Value;
            bgScale.ScaleY = (chkVinculado?.IsChecked == true) ? sldZoom.Value : (sldScaleY?.Value ?? sldZoom.Value);
            bgMove.X = sldPosX.Value; bgMove.Y = sldPosY.Value;
        }

        private void BtnLoadBackground_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "Imágenes|*.jpg;*.png;*.bmp" };
            if (ofd.ShowDialog() == true) imgBackground.Source = new BitmapImage(new Uri(ofd.FileName));
        }

        private void BtnLoadLogo_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "Imágenes|*.jpg;*.png;*.bmp" };
            if (ofd.ShowDialog() == true) imgLogo.Source = new BitmapImage(new Uri(ofd.FileName));
        }

        private void BtnDeleteText_Click(object sender, RoutedEventArgs e) { if (selectedTextBox != null) { textCanvas.Children.Remove(selectedTextBox); selectedTextBox = null; } }
        private void BtnAddText_Click(object sender, RoutedEventArgs e) => AddDynamicText();
        private void Window_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Insert) AddDynamicText(); }
        private void MainWindow_Drop(object sender, DragEventArgs e) { }

        private void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
        {
            SaveConfiguration();
            MessageBox.Show("Configuración guardada en config.cfg", "Éxito");
        }

        private void BtnLoadConfig_Click(object sender, RoutedEventArgs e)
        {
            LoadConfiguration();
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveConfiguration(); // GUARDAR AL SALIR
            if (capture != null) { capture.StopRecording(); capture.Dispose(); }
            if (timer != null) timer.Stop();
            base.OnClosed(e);
            Application.Current.Shutdown();
        }
    }
}