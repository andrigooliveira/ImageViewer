using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageViewer;

public partial class MainWindow : Window
{
    const string AppTitle = "Visualizador de Imagens";
    const double MaxScale = 32;
    const double WheelStep = 1.2;
    const double ButtonStep = 1.25;

    List<string> _files = [];
    int _index = -1;
    BitmapSource? _bitmap;
    ImageEntry? _entry;
    readonly Dictionary<string, ImageEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    int _loadVersion;
    bool _loadingFull;
    readonly FileCache _fileCache = new();

    /// <summary>A decoded image. Bitmap may be a screen-sized preview; Width/Height are the real dimensions.</summary>
    sealed record ImageEntry(BitmapSource Bitmap, int Width, int Height, bool IsFull);

    // View state: _scale 1.0 == one image pixel per screen pixel. Offset is the image
    // center relative to the viewport center, in DIPs.
    double _scale = 1, _offX, _offY;
    int _rotation;
    bool _fitMode = true;
    double _dpiScale = 1;

    bool _dragging;
    Point _dragStart;
    double _dragOffX, _dragOffY;

    bool _fullScreen;
    WindowState _restoreState;

    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    bool _overlaysVisible;

    public MainWindow(string? path)
    {
        InitializeComponent();
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Fade(Toast, 0); };
        _overlayTimer.Tick += (_, _) => { _overlayTimer.Stop(); if (!IsOverOverlayButton()) SetOverlays(false); };
        Closed += (_, _) => _fileCache.Dispose();

        Loaded += (_, _) =>
        {
            _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (!string.IsNullOrWhiteSpace(path)) OpenPath(path);
            else UpdateUi();
        };
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpiScale = newDpi.DpiScaleX;
        if (_bitmap != null) { SetImageSize(); if (_fitMode) ApplyFit(); else { ClampOffset(); UpdateTransform(); } }
    }

    string? CurrentPath => _index >= 0 && _index < _files.Count ? _files[_index] : null;

    // ---------------------------------------------------------------- Files

    void OpenPath(string path)
    {
        try
        {
            path = Path.GetFullPath(path.Trim('"'));
            string? dir;
            if (Directory.Exists(path))
            {
                dir = path;
                path = "";
            }
            else dir = Path.GetDirectoryName(path);

            var files = dir != null ? ListImages(dir) : [];
            if (path != "" && !files.Contains(path, StringComparer.OrdinalIgnoreCase) && File.Exists(path))
            {
                files.Add(path);
                files.Sort(NaturalFileComparer.Instance);
            }

            _cache.Clear();
            _files = files;
            _index = path == "" ? (files.Count > 0 ? 0 : -1)
                                : files.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
            ShowCurrent();
        }
        catch (Exception ex)
        {
            ShowToast("Não foi possível abrir: " + ex.Message);
        }
    }

    static List<string> ListImages(string dir)
    {
        try
        {
            // FileInfo attributes come with the enumeration: no extra call per file (matters on cloud drives).
            var list = new DirectoryInfo(dir).EnumerateFiles()
                .Where(f => ImageFormats.IsSupported(f.Name) &&
                            (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .Select(f => f.FullName)
                .ToList();
            list.Sort(NaturalFileComparer.Instance);
            return list;
        }
        catch
        {
            return [];
        }
    }

    async void ShowCurrent()
    {
        var path = CurrentPath;
        int version = ++_loadVersion;
        if (path == null)
        {
            SetEntry(null, null);
            return;
        }

        UpdateHeader();

        if (!_cache.TryGetValue(path, out var entry))
        {
            string? error = null;
            var (maxW, maxH) = PreviewLimit();
            try
            {
                var data = await _fileCache.GetAsync(path);
                // Current file is in memory: start reading the rest of the folder while it decodes.
                if (version == _loadVersion) _fileCache.Preload(_files, _index);
                entry = await Task.Run(() => LoadImage(data, maxW, maxH));
            }
            catch (Exception ex) { error = ex is FileNotFoundException ? "O arquivo não existe mais." : ex.Message; }
            if (version != _loadVersion) return;
            if (entry == null)
            {
                SetEntry(null, error);
                return;
            }
            _cache[path] = entry;
        }

        SetEntry(entry, null);
        Prefetch(version);
    }

    async void Prefetch(int version)
    {
        // Raw files of the whole folder are read in the background (see FileCache)...
        _fileCache.Preload(_files, _index);

        // ...while only the current image and its neighbours are kept decoded; next one first.
        var keep = new List<string>();
        foreach (int d in new[] { 0, 1, -1, 2 })
        {
            if (_files.Count == 0) break;
            var p = _files[((_index + d) % _files.Count + _files.Count) % _files.Count];
            if (!keep.Contains(p, StringComparer.OrdinalIgnoreCase)) keep.Add(p);
        }
        foreach (var key in _cache.Keys.Where(k => !keep.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
            _cache.Remove(key);

        var (maxW, maxH) = PreviewLimit();
        foreach (var p in keep)
        {
            if (_cache.ContainsKey(p)) continue;
            try
            {
                var entry = await Task.Run(async () => LoadImage(await _fileCache.GetAsync(p), maxW, maxH));
                if (version != _loadVersion) return;
                _cache[p] = entry;
            }
            catch { /* shown when navigated to */ }
        }
    }

    /// <summary>Largest size worth decoding for "fit to window": the screen, in physical pixels.</summary>
    (int, int) PreviewLimit() =>
        ((int)(SystemParameters.PrimaryScreenWidth * _dpiScale), (int)(SystemParameters.PrimaryScreenHeight * _dpiScale));

    /// <summary>
    /// Decodes an image, downscaled to fit maxW×maxH (0 = full resolution). JPEG decoders scale
    /// while decoding, so a screen-sized preview of a large photo is several times faster.
    /// </summary>
    static ImageEntry LoadImage(byte[] data, int maxW, int maxH)
    {
        // Decoding from memory: the file is never locked.
        using var stream = new MemoryStream(data);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

        BitmapFrame first = decoder.Frames[0];
        int orientation = GetOrientation(first);
        bool swap = orientation >= 5;
        int rawW = first.PixelWidth, rawH = first.PixelHeight;
        int fullW = swap ? rawH : rawW, fullH = swap ? rawW : rawH;

        BitmapSource decoded;
        bool isFull = true;
        if (decoder is IconBitmapDecoder)
        {
            stream.Position = 0;
            var ico = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            decoded = ico.Frames.OrderByDescending(f => f.PixelWidth).First();
            fullW = decoded.PixelWidth;
            fullH = decoded.PixelHeight;
        }
        else
        {
            double s = maxW > 0 && maxH > 0 ? Math.Min(1, Math.Min(maxW / (double)fullW, maxH / (double)fullH)) : 1;
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (s < 0.95)
            {
                image.DecodePixelWidth = Math.Max(1, (int)Math.Round(rawW * s));
                isFull = false;
            }
            image.EndInit();
            decoded = image;
        }

        if (orientation > 1)
        {
            var t = new TransformGroup();
            switch (orientation)
            {
                case 2: t.Children.Add(new ScaleTransform(-1, 1)); break;
                case 3: t.Children.Add(new RotateTransform(180)); break;
                case 4: t.Children.Add(new ScaleTransform(1, -1)); break;
                case 5: t.Children.Add(new ScaleTransform(-1, 1)); t.Children.Add(new RotateTransform(270)); break;
                case 6: t.Children.Add(new RotateTransform(90)); break;
                case 7: t.Children.Add(new ScaleTransform(-1, 1)); t.Children.Add(new RotateTransform(90)); break;
                case 8: t.Children.Add(new RotateTransform(270)); break;
            }
            decoded = new CachedBitmap(new TransformedBitmap(decoded, t), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }

        decoded.Freeze();
        return new ImageEntry(decoded, fullW, fullH, isFull);
    }

    /// <summary>Swaps the preview for the full-resolution image once the zoom needs more detail.</summary>
    async void EnsureFullResolution()
    {
        if (_entry is not { IsFull: false } entry || _loadingFull) return;
        if (_scale * entry.Width <= entry.Bitmap.PixelWidth * 1.05) return;

        var path = CurrentPath!;
        int version = _loadVersion;
        _loadingFull = true;
        try
        {
            var full = await Task.Run(async () => LoadImage(await _fileCache.GetAsync(path), 0, 0));
            if (version != _loadVersion) return;
            _cache[path] = full;
            _entry = full;
            _bitmap = full.Bitmap;
            Img.Source = full.Bitmap;
        }
        catch { /* keep the preview */ }
        finally { _loadingFull = false; }
    }

    static int GetOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("System.Photo.Orientation") is ushort o)
                return o;
        }
        catch { }
        return 1;
    }

    // ---------------------------------------------------------------- Display

    void SetEntry(ImageEntry? entry, string? error)
    {
        _entry = entry;
        _bitmap = entry?.Bitmap;
        Img.Source = _bitmap;
        _rotation = 0;
        _fitMode = true;
        ErrorState.Visibility = CurrentPath != null && entry == null ? Visibility.Visible : Visibility.Collapsed;
        ErrorDetail.Text = error ?? "";
        if (entry != null)
        {
            SetImageSize();
            ApplyFit();
        }
        UpdateUi();
    }

    /// <summary>Layout size uses the real dimensions, so a preview bitmap is simply stretched to it.</summary>
    void SetImageSize()
    {
        if (_entry == null) return;
        Img.Width = _entry.Width / _dpiScale;
        Img.Height = _entry.Height / _dpiScale;
    }

    Size RotatedSize()
    {
        if (_bitmap == null) return new Size(0, 0);
        double w = Img.Width, h = Img.Height;
        return _rotation % 180 == 0 ? new Size(w, h) : new Size(h, w);
    }

    double FitScale()
    {
        var s = RotatedSize();
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        if (s.Width <= 0 || s.Height <= 0 || vw <= 0 || vh <= 0) return 1;
        return Math.Min(1, Math.Min(vw / s.Width, vh / s.Height));
    }

    void ApplyFit()
    {
        _fitMode = true;
        _scale = FitScale();
        _offX = _offY = 0;
        UpdateTransform();
        UpdateZoomUi();
    }

    void ZoomTo(double newScale, Point anchor, bool toast = true)
    {
        if (_bitmap == null) return;
        double min = FitScale();
        newScale = Math.Clamp(newScale, min, MaxScale);

        double cx = Viewport.ActualWidth / 2, cy = Viewport.ActualHeight / 2;
        double fx = anchor.X - cx, fy = anchor.Y - cy;
        double k = newScale / _scale;
        _offX = fx - (fx - _offX) * k;
        _offY = fy - (fy - _offY) * k;
        _scale = newScale;
        _fitMode = Math.Abs(_scale - min) < 1e-9;
        if (_fitMode) _offX = _offY = 0;

        ClampOffset();
        UpdateTransform();
        UpdateZoomUi();
        if (toast) ShowToast(ZoomLabel());
    }

    void ZoomBy(double factor, Point anchor)
    {
        double target = _scale * factor;
        // Snap to 100% when the zoom steps across it.
        if ((_scale < 1 && target > 1) || (_scale > 1 && target < 1)) target = 1;
        ZoomTo(target, anchor);
    }

    Point ViewportCenter => new(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);

    void ClampOffset()
    {
        var s = RotatedSize();
        double maxX = Math.Max(0, (s.Width * _scale - Viewport.ActualWidth) / 2);
        double maxY = Math.Max(0, (s.Height * _scale - Viewport.ActualHeight) / 2);
        _offX = Math.Clamp(_offX, -maxX, maxX);
        _offY = Math.Clamp(_offY, -maxY, maxY);
    }

    bool CanPan()
    {
        var s = RotatedSize();
        return s.Width * _scale > Viewport.ActualWidth + 0.5 || s.Height * _scale > Viewport.ActualHeight + 0.5;
    }

    void UpdateTransform()
    {
        if (_bitmap == null) return;
        double w = Img.Width, h = Img.Height;
        var m = Matrix.Identity;
        m.Translate(-w / 2, -h / 2);
        m.Rotate(_rotation);
        m.Scale(_scale, _scale);
        m.Translate(Viewport.ActualWidth / 2 + _offX, Viewport.ActualHeight / 2 + _offY);
        // Snap to device pixels to keep the image crisp.
        m.OffsetX = Math.Round(m.OffsetX * _dpiScale) / _dpiScale;
        m.OffsetY = Math.Round(m.OffsetY * _dpiScale) / _dpiScale;
        ImgTransform.Matrix = m;

        RenderOptions.SetBitmapScalingMode(Img,
            _scale >= 3 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        Viewport.Cursor = _dragging ? Cursors.SizeAll : CanPan() ? Cursors.Hand : null;
    }

    string ZoomLabel() => Math.Round(_scale * 100).ToString(CultureInfo.CurrentCulture) + "%";

    void UpdateZoomUi()
    {
        bool has = _bitmap != null;
        if (has) EnsureFullResolution();
        ZoomLabelText.Text = has ? ZoomLabel() : "—";
        ZoomText.IsEnabled = has;
        ZoomInButton.IsEnabled = has && _scale < MaxScale - 1e-9;
        ZoomOutButton.IsEnabled = has && !_fitMode;
        FitButton.IsEnabled = has && !_fitMode;
    }

    void UpdateHeader()
    {
        var path = CurrentPath;
        FileNameText.Text = path != null ? Path.GetFileName(path) : "";
        IndexText.Text = path != null ? $"{_index + 1} de {_files.Count}" : "";
        Title = path != null ? $"{Path.GetFileName(path)} - {AppTitle}" : AppTitle;
    }

    void UpdateUi()
    {
        UpdateHeader();
        var path = CurrentPath;
        bool hasFile = path != null;
        bool multiple = _files.Count > 1;

        EmptyState.Visibility = hasFile ? Visibility.Collapsed : Visibility.Visible;
        Img.Visibility = _bitmap != null ? Visibility.Visible : Visibility.Collapsed;

        PrevButton.IsEnabled = NextButton.IsEnabled = multiple;
        PrevOverlay.Visibility = NextOverlay.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        RotateLeftButton.IsEnabled = RotateRightButton.IsEnabled = _bitmap != null;
        CopyButton.IsEnabled = _bitmap != null;
        DeleteButton.IsEnabled = RevealButton.IsEnabled = PropertiesButton.IsEnabled = hasFile;

        string info = "";
        if (hasFile)
        {
            var parts = new List<string>();
            if (_entry != null) parts.Add($"{_entry.Width} × {_entry.Height} px");
            try { parts.Add(FormatSize(new FileInfo(path!).Length)); } catch { }
            try { parts.Add(File.GetLastWriteTime(path!).ToString("g", CultureInfo.CurrentCulture)); } catch { }
            info = string.Join("   ·   ", parts);
        }
        InfoText.Text = info;
        UpdateZoomUi();
    }

    static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} bytes" : v.ToString(v >= 100 ? "0" : "0.##", CultureInfo.CurrentCulture) + " " + units[u];
    }

    // ---------------------------------------------------------------- Actions

    void Navigate(int delta)
    {
        if (_files.Count < 2) return;
        _index = ((_index + delta) % _files.Count + _files.Count) % _files.Count;
        ShowCurrent();
    }

    void GoTo(int index)
    {
        if (_files.Count == 0) return;
        _index = Math.Clamp(index, 0, _files.Count - 1);
        ShowCurrent();
    }

    void Rotate(int degrees)
    {
        if (_bitmap == null) return;
        _rotation = ((_rotation + degrees) % 360 + 360) % 360;
        ApplyFit();
    }

    void ToggleZoom(Point anchor)
    {
        if (_bitmap == null) return;
        double fit = FitScale();
        if (!_fitMode) ApplyFit();
        else ZoomTo(fit < 1 ? 1 : 2, anchor);
        ShowToast(ZoomLabel());
    }

    void RequestDelete()
    {
        var path = CurrentPath;
        if (path == null) return;
        DeleteFileName.Text = "“" + Path.GetFileName(path) + "”";
        DeleteOverlay.Visibility = Visibility.Visible;
        DeleteConfirmButton.Focus();
    }

    void CloseDeleteDialog() => DeleteOverlay.Visibility = Visibility.Collapsed;

    void ConfirmDelete()
    {
        CloseDeleteDialog();
        var path = CurrentPath;
        if (path == null) return;

        bool ok;
        try { ok = NativeShell.MoveToRecycleBin(path, new WindowInteropHelper(this).Handle); }
        catch { ok = false; }
        if (!ok)
        {
            ShowToast("Não foi possível excluir o arquivo");
            return;
        }

        _cache.Remove(path);
        _fileCache.Remove(path);
        _files.RemoveAt(_index);
        if (_files.Count == 0) _index = -1;
        else if (_index >= _files.Count) _index = 0;
        ShowCurrent();
    }

    void OpenWithDialog()
    {
        if (CurrentPath is not { } path) return;
        try
        {
            System.Diagnostics.Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}");
        }
        catch { }
    }

    void CopyToClipboard()
    {
        if (_entry == null || CurrentPath == null) return;
        try
        {
            var image = _entry.IsFull ? _entry.Bitmap : LoadImage(_fileCache.Get(CurrentPath), 0, 0).Bitmap;
            var data = new DataObject();
            data.SetImage(image);
            data.SetFileDropList(new StringCollection { CurrentPath });
            Clipboard.SetDataObject(data, true);
            ShowToast("Copiado");
        }
        catch
        {
            ShowToast("Não foi possível copiar");
        }
    }

    void ShowOpenDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = ImageFormats.DialogFilter,
            Title = "Abrir imagem",
        };
        if (CurrentPath != null) dialog.InitialDirectory = Path.GetDirectoryName(CurrentPath);
        if (dialog.ShowDialog(this) == true) OpenPath(dialog.FileName);
    }

    void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        if (_fullScreen)
        {
            _restoreState = WindowState;
            WindowState = WindowState.Normal; // required so the maximized window covers the taskbar
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            TopBar.Visibility = BottomBar.Visibility = Visibility.Collapsed;
            Viewport.Background = Brushes.Black;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _restoreState;
            TopBar.Visibility = BottomBar.Visibility = Visibility.Visible;
            Viewport.Background = Brushes.Transparent;
        }
        FullScreenButton.Content = _fullScreen ? "" : "";
    }

    void ShowToast(string text)
    {
        ToastText.Text = text;
        Fade(Toast, 1, 80);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    static void Fade(UIElement element, double to, int ms = 180) =>
        element.BeginAnimation(OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new QuadraticEase() });

    void SetOverlays(bool visible)
    {
        if (_overlaysVisible == visible) return;
        _overlaysVisible = visible;
        Fade(PrevOverlay, visible ? 1 : 0);
        Fade(NextOverlay, visible ? 1 : 0);
    }

    bool IsOverOverlayButton() => PrevOverlay.IsMouseOver || NextOverlay.IsMouseOver;

    // ---------------------------------------------------------------- Input

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        if (DeleteOverlay.Visibility == Visibility.Visible)
        {
            if (key == Key.Escape) { CloseDeleteDialog(); e.Handled = true; }
            else if (key == Key.Enter && Keyboard.FocusedElement is not Button) { ConfirmDelete(); e.Handled = true; }
            else if (key is not (Key.Tab or Key.Enter or Key.Space)) e.Handled = true;
            return;
        }

        e.Handled = true;
        switch (key)
        {
            case Key.Left or Key.PageUp or Key.BrowserBack: Navigate(-1); break;
            case Key.Right or Key.PageDown or Key.BrowserForward: Navigate(1); break;
            case Key.Home: GoTo(0); break;
            case Key.End: GoTo(_files.Count - 1); break;
            case Key.Add or Key.OemPlus: ZoomBy(ButtonStep, ViewportCenter); break;
            case Key.Subtract or Key.OemMinus: ZoomBy(1 / ButtonStep, ViewportCenter); break;
            case Key.D0 or Key.NumPad0 when ctrl: ApplyFit(); ShowToast(ZoomLabel()); break;
            case Key.D1 or Key.NumPad1 when ctrl: ZoomTo(1, ViewportCenter); break;
            case Key.OemPeriod when ctrl: Rotate(90); break;
            case Key.OemComma when ctrl: Rotate(-90); break;
            case Key.Delete: RequestDelete(); break;
            case Key.O when ctrl: ShowOpenDialog(); break;
            case Key.C when ctrl: CopyToClipboard(); break;
            case Key.Enter when alt: if (CurrentPath != null) NativeShell.ShowProperties(CurrentPath, new WindowInteropHelper(this).Handle); break;
            case Key.F11: ToggleFullScreen(); break;
            case Key.Escape when _fullScreen: ToggleFullScreen(); break;
            default: e.Handled = false; break;
        }
    }

    void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) { Navigate(-1); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { Navigate(1); e.Handled = true; }
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            OpenPath(paths[0]);
            Activate();
        }
    }

    void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_bitmap == null) return;
        if (_fitMode || _scale < FitScale()) ApplyFit();
        else { ClampOffset(); UpdateTransform(); }
    }

    void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_bitmap == null) return;
        ZoomBy(Math.Pow(WheelStep, e.Delta / 120.0), e.GetPosition(Viewport));
        e.Handled = true;
    }

    void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_bitmap == null) return;
        if (e.ClickCount == 2)
        {
            ToggleZoom(e.GetPosition(Viewport));
            e.Handled = true;
            return;
        }
        if (!CanPan()) return;
        _dragging = true;
        _dragStart = e.GetPosition(Viewport);
        _dragOffX = _offX;
        _dragOffY = _offY;
        Viewport.CaptureMouse();
        Viewport.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            var p = e.GetPosition(Viewport);
            _offX = _dragOffX + (p.X - _dragStart.X);
            _offY = _dragOffY + (p.Y - _dragStart.Y);
            ClampOffset();
            UpdateTransform();
            return;
        }
        if (_files.Count > 1)
        {
            SetOverlays(true);
            _overlayTimer.Stop();
            _overlayTimer.Start();
        }
    }

    void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) Viewport.ReleaseMouseCapture();
    }

    void Viewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _dragging = false;
        UpdateTransform();
    }

    void Viewport_MouseLeave(object sender, MouseEventArgs e) => SetOverlays(false);

    // ---------------------------------------------------------------- Buttons

    void Open_Click(object sender, RoutedEventArgs e) => ShowOpenDialog();
    void Prev_Click(object sender, RoutedEventArgs e) => Navigate(-1);
    void Next_Click(object sender, RoutedEventArgs e) => Navigate(1);
    void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(ButtonStep, ViewportCenter);
    void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(1 / ButtonStep, ViewportCenter);
    void ZoomText_Click(object sender, RoutedEventArgs e) => ToggleZoom(ViewportCenter);
    void Fit_Click(object sender, RoutedEventArgs e) { ApplyFit(); ShowToast(ZoomLabel()); }
    void RotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(-90);
    void RotateRight_Click(object sender, RoutedEventArgs e) => Rotate(90);
    void Delete_Click(object sender, RoutedEventArgs e) => RequestDelete();
    void DeleteConfirm_Click(object sender, RoutedEventArgs e) => ConfirmDelete();
    void DeleteCancel_Click(object sender, RoutedEventArgs e) => CloseDeleteDialog();
    void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();
    void Copy_Click(object sender, RoutedEventArgs e) => CopyToClipboard();
    void OpenWith_Click(object sender, RoutedEventArgs e) => OpenWithDialog();

    void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPath != null) NativeShell.RevealInExplorer(CurrentPath);
    }

    void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPath != null) NativeShell.ShowProperties(CurrentPath, new WindowInteropHelper(this).Handle);
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = MoreButton.ContextMenu!;
        menu.PlacementTarget = MoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Registration.RegisterAndOpenSettings())
                MessageBox.Show(this,
                    "Não foi possível registrar para todo o computador, então o registro foi feito só para este usuário.\n\n" +
                    "Detalhes em: " + Registration.LogPath,
                    "Visualizador de Imagens", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ShowToast("Falha ao registrar: " + ex.Message);
        }
    }

    void Shortcuts_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "← / →  ou  PgUp / PgDn\tImagem anterior / próxima\n" +
            "Home / End\t\tPrimeira / última imagem\n" +
            "Roda do mouse\t\tZoom no ponto do cursor\n" +
            "Duplo clique\t\tAjustar / tamanho real\n" +
            "Arrastar\t\tMover imagem ampliada\n" +
            "+ / -\t\tAmpliar / reduzir\n" +
            "Ctrl+0 / Ctrl+1\t\tAjustar / tamanho real\n" +
            "Ctrl+, / Ctrl+.\t\tGirar esquerda / direita\n" +
            "Del\t\tExcluir (Lixeira)\n" +
            "Ctrl+C\t\tCopiar\n" +
            "Ctrl+O\t\tAbrir\n" +
            "Alt+Enter\t\tPropriedades\n" +
            "F11 / Esc\t\tTela inteira",
            "Atalhos do teclado", MessageBoxButton.OK, MessageBoxImage.None);
    }
}
