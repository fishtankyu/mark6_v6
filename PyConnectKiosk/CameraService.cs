using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace PyConnectKiosk;
public class CameraService : IDisposable
{
    public event Action<BitmapSource, Mat>? FrameReady;

    private VideoCapture?           _cap;
    private CancellationTokenSource _cts = new CancellationTokenSource();
    public  bool                    IsRunning { get; private set; }

    public void Start(int index = 0)
    {
        if (IsRunning) return;
        _cts      = new CancellationTokenSource();
        _cap      = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
        IsRunning = true;
        Task.Run(() => Loop(_cts.Token));
    }

    public void Stop()
    {
        IsRunning = false;
        _cts.Cancel();
        _cap?.Release();
        _cap = null;
    }

    private void Loop(CancellationToken ct)
    {
        using var frame = new Mat();
        while (!ct.IsCancellationRequested)
        {
            if (_cap == null || !_cap.IsOpened()) break;
            if (!_cap.Read(frame) || frame.Empty()) { Thread.Sleep(30); continue; }

            var clone  = frame.Clone();
            var bitmap = MatToBitmapSource(frame);
            // bitmap is already frozen inside MatToBitmapSource

            FrameReady?.Invoke(bitmap, clone);
            Thread.Sleep(30);
        }
    }

    /// <summary>
    /// Converts an OpenCV Mat to a WPF BitmapSource safely from any thread.
    /// Encodes as PNG bytes then loads via BitmapImage (thread-safe).
    /// </summary>
    public static BitmapSource MatToBitmapSource(Mat mat)
    {
        Cv2.ImEncode(".png", mat, out byte[] buf);
        using var ms = new MemoryStream(buf);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption  = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public void Dispose() => Stop();
}