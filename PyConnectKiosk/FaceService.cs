using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OpenCvSharp;
using OpenCvSharp.Face;

namespace PyConnectKiosk;

/// <summary>
/// LBPH face recognition with hot-reload.
///
/// On construction the directory is loaded and a FileSystemWatcher is attached.
/// When a photo is added, modified, renamed, or deleted, the recognizer is
/// rebuilt automatically — no kiosk restart needed after admin enrolment.
/// </summary>
public class FaceService : IDisposable
{
    private readonly object             _lock = new();
    private readonly CascadeClassifier  _cascade;
    private readonly string             _directory;

    // Swappable state — protected by _lock.
    private LBPHFaceRecognizer _recognizer;
    private List<string>       _names = new();

    // Hot-reload plumbing
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Timers.Timer _debounce;
    private bool _disposed;

    public bool HasFaces      { get { lock (_lock) return _names.Count > 0; } }
    public int  EnrolledCount { get { lock (_lock) return _names.Count;  } }

    public event Action? OnReloaded;  // fires after a hot-reload completes

    public FaceService(string directory)
    {
        if (!File.Exists(FaceSettings.HaarCascadePath))
            throw new FileNotFoundException(
                $"Haar cascade not found: '{FaceSettings.HaarCascadePath}'\n" +
                "Download from https://github.com/opencv/opencv/tree/master/data/haarcascades");

        _directory  = directory;
        _cascade    = new CascadeClassifier(FaceSettings.HaarCascadePath);
        _recognizer = LBPHFaceRecognizer.Create();

        // Initial load
        Reload();

        // Watch for changes — debounce because adding a photo often produces
        // several FileSystemWatcher events in quick succession.
        _debounce = new System.Timers.Timer(800) { AutoReset = false };
        _debounce.Elapsed += (_, __) =>
        {
            try { Reload(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FaceService] Reload failed: {ex.Message}");
            }
        };

        if (!Directory.Exists(_directory))
            Directory.CreateDirectory(_directory);

        _watcher = new FileSystemWatcher(_directory)
        {
            Filter                = "*.*",
            NotifyFilter          = NotifyFilters.FileName
                                  | NotifyFilters.LastWrite
                                  | NotifyFilters.Size
                                  | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            EnableRaisingEvents   = true,
        };
        _watcher.Created  += OnFileSystemEvent;
        _watcher.Changed  += OnFileSystemEvent;
        _watcher.Deleted  += OnFileSystemEvent;
        _watcher.Renamed  += OnFileSystemEvent;
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;
        // Only react to image files
        var ext = Path.GetExtension(e.Name ?? "").ToLowerInvariant();
        if (ext != ".jpg" && ext != ".jpeg" && ext != ".png") return;

        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>
    /// Rebuild the recognizer from disk. Safe to call from any thread.
    /// Atomically swaps the recognizer + names list under the lock; the old
    /// recognizer is disposed only after the swap, so in-flight ProcessFrame
    /// calls finish against the previous instance without crashing.
    /// </summary>
    public void Reload()
    {
        var newNames      = new List<string>();
        var newRecognizer = LBPHFaceRecognizer.Create();

        if (Directory.Exists(_directory))
        {
            var images = new List<Mat>();
            var labels = new List<int>();
            int id     = 0;

            var files = Directory.GetFiles(_directory, "*.jpg")
                        .Concat(Directory.GetFiles(_directory, "*.jpeg"))
                        .Concat(Directory.GetFiles(_directory, "*.png"));

            foreach (var file in files)
            {
                Mat? face = null;
                try
                {
                    var stem = Path.GetFileNameWithoutExtension(file);
                    var name = CultureInfo.CurrentCulture
                                   .TextInfo.ToTitleCase(stem.Replace("_", " "));

                    using var gray = Cv2.ImRead(file, ImreadModes.Grayscale);
                    if (gray.Empty()) continue;

                    face = ExtractFirstFace(gray);
                    if (face == null) continue;

                    images.Add(face);
                    labels.Add(id++);
                    newNames.Add(name);
                    face = null; // ownership transferred to images list
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[FaceService] Skipped '{file}': {ex.Message}");
                }
                finally
                {
                    face?.Dispose();
                }
            }

            if (images.Count > 0)
                newRecognizer.Train(images, labels);

            foreach (var m in images) m.Dispose();
        }

        // Atomic swap
        LBPHFaceRecognizer oldRecognizer;
        lock (_lock)
        {
            oldRecognizer = _recognizer;
            _recognizer   = newRecognizer;
            _names        = newNames;
        }
        oldRecognizer.Dispose();

        System.Diagnostics.Debug.WriteLine(
            $"[FaceService] Reloaded '{_directory}': {newNames.Count} face(s)");
        OnReloaded?.Invoke();
    }

    public FaceMatch? ProcessFrame(Mat bgrFrame)
    {
        // Hold the lock for the whole frame — Predict is fast (~ms) and reloads
        // are rare. This guarantees we never use a disposed recognizer.
        lock (_lock)
        {
            if (_names.Count == 0) return null;

            using var gray = new Mat();
            Cv2.CvtColor(bgrFrame, gray, ColorConversionCodes.BGR2GRAY);

            var       faces = DetectFaces(gray);
            FaceMatch? best = null;

            foreach (var rect in faces)
            {
                using var roi     = new Mat(gray, rect);
                using var resized = new Mat();
                Cv2.Resize(roi, resized, new Size(200, 200));

                _recognizer.Predict(resized, out int label, out double confidence);

                bool matched = label >= 0
                               && label < _names.Count
                               && confidence < FaceSettings.Threshold;

                var color = matched ? Scalar.LimeGreen : Scalar.OrangeRed;
                Cv2.Rectangle(bgrFrame, rect, color, 2);

                if (matched)
                {
                    var name = _names[label];
                    var conf = Math.Round(100.0 - confidence, 1);
                    Cv2.PutText(bgrFrame,
                                $"{name}  {conf}%",
                                new Point(rect.X, rect.Y - 8),
                                HersheyFonts.HersheySimplex, 0.6, color, 1);
                    best = new FaceMatch(name, conf);
                }
                else
                {
                    Cv2.PutText(bgrFrame, "Unknown",
                                new Point(rect.X, rect.Y - 8),
                                HersheyFonts.HersheySimplex, 0.6, color, 1);
                }
            }

            return best;
        }
    }

    private Rect[] DetectFaces(Mat gray) =>
        _cascade.DetectMultiScale(gray, 1.1, 5, minSize: new Size(80, 80));

    private Mat? ExtractFirstFace(Mat gray)
    {
        var faces = DetectFaces(gray);
        if (faces.Length == 0) return null;

        var resized = new Mat();
        using var roi = new Mat(gray, faces[0]);
        Cv2.Resize(roi, resized, new Size(200, 200));
        return resized;
    }

    public void Dispose()
    {
        _disposed = true;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created  -= OnFileSystemEvent;
            _watcher.Changed  -= OnFileSystemEvent;
            _watcher.Deleted  -= OnFileSystemEvent;
            _watcher.Renamed  -= OnFileSystemEvent;
            _watcher.Dispose();
        }

        _debounce.Stop();
        _debounce.Dispose();

        lock (_lock)
        {
            _recognizer.Dispose();
        }
        _cascade.Dispose();
    }
}
