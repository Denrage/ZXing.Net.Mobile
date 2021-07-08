using System;
using Android.Content;
using Android.Runtime;
using Android.Views;
using Android.Graphics;
using ZXing.Mobile.CameraAccess;
using ZXing.Net.Mobile.Android;
using System.Threading.Tasks;
using System.Threading;
using Java.Interop;
using System.Diagnostics;

namespace ZXing.Mobile
{
    public class ZXingSurfaceView : SurfaceView, ISurfaceHolderCallback, IScannerView, IScannerSessionHost, ScaleGestureDetector.IOnScaleGestureListener
    {
        public ZXingSurfaceView(Context context, MobileBarcodeScanningOptions options)
            : base(context)
        {
            ScanningOptions = options ?? new MobileBarcodeScanningOptions();
            Init();
            scaleGestureDetector = new ScaleGestureDetector(context, this);
        }

        protected ZXingSurfaceView(IntPtr javaReference, JniHandleOwnership transfer)
            : base(javaReference, transfer) => Init();

        bool addedHolderCallback = false;

        void Init()
        {
            if (cameraAnalyzer == null)
                cameraAnalyzer = new CameraAnalyzer(this, this);

            cameraAnalyzer.ResumeAnalysis();

            if (!addedHolderCallback)
            {
                Holder.AddCallback(this);
                Holder.SetType(SurfaceType.PushBuffers);
                addedHolderCallback = true;
            }
        }

        public async void SurfaceCreated(ISurfaceHolder holder)
        {
            await PermissionsHandler.RequestPermissionsAsync();

            cameraAnalyzer.SetupCamera();

            surfaceCreated = true;
            surfaceCreatedResetEvent.Set();
        }

        public async void SurfaceChanged(ISurfaceHolder holder, Format format, int wx, int hx)
        {
            cameraAnalyzer.RefreshCamera();
        }

        public async void SurfaceDestroyed(ISurfaceHolder holder)
        {
            try
            {
                if (addedHolderCallback)
                {
                    Holder.RemoveCallback(this);
                    addedHolderCallback = false;
                }
            }
            catch { }

            cameraAnalyzer.ShutdownCamera();
        }

        public override bool OnTouchEvent(MotionEvent e)
        {
            var r = base.OnTouchEvent(e);

            if (e.PointerCount == 2)
            {
                return this.scaleGestureDetector.OnTouchEvent(e);
            }

            switch (e.Action)
            {
                case MotionEventActions.Down:
                    return true;
                case MotionEventActions.Up:
                    var touchX = e.GetX();
                    var touchY = e.GetY();
                    AutoFocus((int)touchX, (int)touchY);
                    break;
            }

            return r;
        }

        public void AutoFocus()
            => cameraAnalyzer.AutoFocus();

        public void AutoFocus(int x, int y)
            => cameraAnalyzer.AutoFocus(x, y);

        public void StartScanning(Action<Result> scanResultCallback, MobileBarcodeScanningOptions options = null)
        {
            Task.Run(() =>
            {
                surfaceCreatedResetEvent.Wait();
                surfaceCreatedResetEvent.Reset();

                ScanningOptions = options ?? MobileBarcodeScanningOptions.Default;

                cameraAnalyzer.BarcodeFound = (result) =>
                    scanResultCallback?.Invoke(result);
                cameraAnalyzer.ResumeAnalysis();
            });
        }

        public void StopScanning()
            => cameraAnalyzer.ShutdownCamera();

        public void PauseAnalysis()
            => cameraAnalyzer.PauseAnalysis();

        public void ResumeAnalysis()
            => cameraAnalyzer.ResumeAnalysis();

        public void Torch(bool on)
        {
            if (on)
                cameraAnalyzer.Torch.TurnOn();
            else
                cameraAnalyzer.Torch.TurnOff();
        }

        public void ToggleTorch()
            => cameraAnalyzer.Torch.Toggle();

        public MobileBarcodeScanningOptions ScanningOptions { get; set; }

        public bool IsTorchOn => cameraAnalyzer.Torch.IsEnabled;

        public bool IsAnalyzing => cameraAnalyzer.IsAnalyzing;

        CameraAnalyzer cameraAnalyzer;
        bool surfaceCreated;
        ManualResetEventSlim surfaceCreatedResetEvent = new ManualResetEventSlim(false);
        private ScaleGestureDetector scaleGestureDetector;

        public bool HasTorch => cameraAnalyzer.Torch.IsSupported;

        protected override void OnAttachedToWindow()
        {
            base.OnAttachedToWindow();

            // Reinit things
            Init();
        }

        protected override void OnWindowVisibilityChanged(ViewStates visibility)
        {
            base.OnWindowVisibilityChanged(visibility);
            if (visibility == ViewStates.Visible)
                Init();
        }

        public override async void OnWindowFocusChanged(bool hasWindowFocus)
        {
            base.OnWindowFocusChanged(hasWindowFocus);

            if (!hasWindowFocus)
                return;

            //only refresh the camera if the surface has already been created. Fixed #569
            if (surfaceCreated)
                cameraAnalyzer.RefreshCamera();
        }

        // [MAGIC VALUE] Values below lets the camera jump back to "normal"
        readonly float minZoomLevel = 0.5f;
        // Camera defaults at 1f. MaxZoomLevel returned 100f on test device.
        // Probably since a Wide-angle lens was present. Makes no sense to go above "normal".
        readonly float maxZoomLevel = 1f;
        // [MAGIC VALUE] To prevent quick jumps in zoom level, this offset exists. It was determined by what felt the best
        readonly float zoomJumpPreventionOffset = 0.25f;
        float zoomStartOffset = 0f;

        float CalculateZoomLevel(float scaleFactor)
        {
            var currentZoomLevel = cameraAnalyzer.CurrentZoomLevel();
            var adjustedScaledFactor = 1f - scaleFactor;
            var preventJump = adjustedScaledFactor < 0f ? -adjustedScaledFactor > zoomJumpPreventionOffset : adjustedScaledFactor > zoomJumpPreventionOffset;

            if (preventJump)
                return currentZoomLevel;

            // This Offset is used to prevent accidental zooms. The Start Value can be set through options
            if (zoomStartOffset > 0f)
            {
                zoomStartOffset += (adjustedScaledFactor > 0f) ? -adjustedScaledFactor : adjustedScaledFactor;
                return currentZoomLevel;
            }

            var newZoomLevel = currentZoomLevel + (adjustedScaledFactor * ScanningOptions.ZoomSpeedModifier);

            if (newZoomLevel < minZoomLevel)
                newZoomLevel = minZoomLevel;
            else if (newZoomLevel > maxZoomLevel)
                newZoomLevel = maxZoomLevel;

            return newZoomLevel;
        }

        public bool OnScale(ScaleGestureDetector detector)
        {
            var zoomLevel = CalculateZoomLevel(detector.ScaleFactor);
            cameraAnalyzer.SetZoom(zoomLevel);

            return true;
        }

        public bool OnScaleBegin(ScaleGestureDetector detector)
        {
            zoomStartOffset = ScanningOptions.ZoomStartOffset;
            cameraAnalyzer.StartZoom();
            cameraAnalyzer.SetZoom(cameraAnalyzer.CurrentZoomLevel());
            return true;
        }

        public void OnScaleEnd(ScaleGestureDetector detector)
        {
            cameraAnalyzer.StopZoom();
        }
    }
}
