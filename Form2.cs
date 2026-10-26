using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Accord.Video;
using Accord.Video.DirectShow;
using Accord.Video.FFMPEG;
using NAudio.Wave;

namespace TestCamera
{
    public partial class Form2 : Form
    {
        private string _lastScrcpyFolder = "";
        private string _lastWebcamFolder = "";

        private bool _webcamSaveCancelled = false;
        private bool _scrcpySaveCancelled = false;

        // Prevents unexpected Scrcpy exits from stopping the recording session
        private bool _isManualScrcpyStop = false;
        private bool _isRestartingScrcpy = false;
        private bool _isArrangingLayout = false;

        // --- WINDOWS API IMPORTS ---
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int cx, int cy, bool repaint);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_CLOSE = 0x0010;
        private const uint WM_QUIT = 0x0012;
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;

        private const int WS_CHILD = 0x40000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_BORDER = 0x00800000;
        private const int WS_DLGFRAME = 0x00400000;

        private const int WS_EX_DLGMODALFRAME = 0x00000001;
        private const int WS_EX_WINDOWEDGE = 0x00000100;
        private const int WS_EX_CLIENTEDGE = 0x00000200;
        private const int WS_EX_STATICEDGE = 0x00020000;

        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;

        private string _adbPath;
        private string _scrcpyPath;
        private Process _scrcpyProcess;

        // CRITICAL FIX: Store the window handle BEFORE SetParent turns it into a child window!
        // Otherwise _scrcpyProcess.MainWindowHandle becomes IntPtr.Zero and WM_CLOSE fails to save the MP4!
        private IntPtr _embeddedScrcpyHwnd = IntPtr.Zero;

        private FilterInfoCollection videoDevices;
        private VideoCaptureDevice videoSource;
        private object videoWriter;

        private int _recordingWidth = 0;
        private int _recordingHeight = 0;
        private bool _writerFailed = false;

        private WaveInEvent waveIn;
        private WaveFileWriter waveWriter;
        private readonly object _audioLock = new object();

        private ConcurrentQueue<Bitmap> _videoFrameQueue = new ConcurrentQueue<Bitmap>();
        private CancellationTokenSource _videoWriteTokenSource;
        private Task _videoSaverTask;

        private bool isRecording = false;
        private bool isStopping = false;
        private bool needToOpenWriter = false;
        private string saveFileName = "";
        private string tempVideoFile = "";
        private string tempAudioFile = "";

        private readonly object _writerLock = new object();
        private DateTime _lastUiUpdate = DateTime.MinValue;

        private int _lastPanelWidth = 0;
        private int _lastPanelHeight = 0;
        private int _forceResizeTicks = 0;
        private System.Windows.Forms.Timer _scrcpyResizeTimer;

        public Form2()
        {
            InitializeComponent();
        }

        public Form2(string adbPath, string scrcpyPath)
        {
            _adbPath = adbPath;
            _scrcpyPath = scrcpyPath;
            InitializeComponent();

            this.DoubleBuffered = true;
            this.BackColor = Color.Black;

            if (pictureBox1 != null)
            {
                pictureBox1.Parent = this;
                pictureBox1.SizeMode = PictureBoxSizeMode.Zoom;
                pictureBox1.BackColor = Color.Black;
                pictureBox1.BorderStyle = BorderStyle.None;
                pictureBox1.Margin = new Padding(0);
            }

            if (panelContainer != null)
            {
                panelContainer.Parent = this;
                panelContainer.BackColor = Color.Black;
                panelContainer.BorderStyle = BorderStyle.None;
                panelContainer.Margin = new Padding(0);
                panelContainer.Padding = new Padding(0);
                panelContainer.Visible = false;
            }

            _scrcpyResizeTimer = new System.Windows.Forms.Timer();
            _scrcpyResizeTimer.Interval = 250;
            _scrcpyResizeTimer.Tick += (s, ev) => ResizeEmbeddedWindow(false);

            if (panelContainer != null) panelContainer.Resize += PanelContainer_Resize;

            Load += Form2_Load;
            FormClosing += Form2_FormClosing;
            this.Resize += Form2_Resize;
        }

        private void Form2_Load(object sender, EventArgs e) { }

        public void ArrangeLayout()
        {
            if (this.InvokeRequired) { this.Invoke(new Action(ArrangeLayout)); return; }
            if (_isArrangingLayout) return;

            _isArrangingLayout = true;
            this.SuspendLayout();
            try
            {
                bool hasScrcpy = (_scrcpyProcess != null && !_scrcpyProcess.HasExited) || _isRestartingScrcpy;
                bool hasCam = (videoSource != null && videoSource.IsRunning) || isRecording;

                if (hasScrcpy && hasCam)
                {
                    if (!panelContainer.Visible) panelContainer.Visible = true;
                    if (!pictureBox1.Visible) pictureBox1.Visible = true;
                    if (panelContainer.Dock != DockStyle.Left) panelContainer.Dock = DockStyle.Left;
                    panelContainer.Width = this.ClientSize.Width / 2;
                    if (pictureBox1.Dock != DockStyle.Fill) pictureBox1.Dock = DockStyle.Fill;
                    pictureBox1.BringToFront();
                }
                else if (hasScrcpy)
                {
                    if (pictureBox1.Visible) pictureBox1.Visible = false;
                    if (!panelContainer.Visible) panelContainer.Visible = true;
                    if (panelContainer.Dock != DockStyle.Fill) panelContainer.Dock = DockStyle.Fill;
                    panelContainer.BringToFront();
                }
                else if (hasCam)
                {
                    if (panelContainer.Visible) panelContainer.Visible = false;
                    if (!pictureBox1.Visible) pictureBox1.Visible = true;
                    if (pictureBox1.Dock != DockStyle.Fill) pictureBox1.Dock = DockStyle.Fill;
                    pictureBox1.BringToFront();
                }
            }
            finally
            {
                this.ResumeLayout(true);
                _isArrangingLayout = false;
            }
        }

        private void Form2_Resize(object sender, EventArgs e)
        {
            if (_isArrangingLayout) return;
            ArrangeLayout();
            ResizeEmbeddedWindow(true);
        }

        private void PanelContainer_Resize(object sender, EventArgs e)
        {
            if (_isArrangingLayout) return;
            ResizeEmbeddedWindow(true);
        }

        /// <summary>
        /// Dynamically detects major version of scrcpy (1, 2, or 3) so flags never conflict
        /// </summary>
        private int GetScrcpyMajorVersion(string scrcpyExePath)
        {
            if (string.IsNullOrEmpty(scrcpyExePath) || !File.Exists(scrcpyExePath))
                return 1;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = scrcpyExePath,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(scrcpyExePath)
                };

                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd() + " " + p.StandardError.ReadToEnd();
                    p.WaitForExit(2000);

                    Match m = Regex.Match(output, @"scrcpy\s+(\d+)\.", RegexOptions.IgnoreCase);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int major))
                    {
                        return major;
                    }
                }
            }
            catch { }

            if (scrcpyExePath.IndexOf("2.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                scrcpyExePath.IndexOf("3.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                scrcpyExePath.IndexOf("EU", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 2;
            }

            return 1; // Default to 1.x compatible flags if unknown
        }

        public async Task<bool> StartAndEmbedScrcpy(bool isAutoReconnect = false)
        {
            if (!string.IsNullOrEmpty(ADB_LOGS.scrcpy_path))
                _scrcpyPath = ADB_LOGS.scrcpy_path;
            if (!string.IsNullOrEmpty(ADB_LOGS.adbPath))
                _adbPath = ADB_LOGS.adbPath;

            if (_scrcpySaveCancelled)
                return false;

            _isManualScrcpyStop = false;
            string scrcpySavePath = "";

            // 1. Ask where to save BEFORE showing Form2 (so Form2 NEVER appears before clicking Save!)
            if (!isAutoReconnect)
            {
                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Title = "Choose where to save the Android video";
                    sfd.Filter = "MP4 Video File|*.mp4";
                    sfd.FileName = "Scrcpy_Video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4";

                    if (sfd.ShowDialog() != DialogResult.OK)
                    {
                        _scrcpySaveCancelled = true;
                        return false;
                    }

                    _scrcpySaveCancelled = false;
                    scrcpySavePath = sfd.FileName;
                    _lastScrcpyFolder = Path.GetDirectoryName(scrcpySavePath);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(_lastScrcpyFolder))
                    _lastScrcpyFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

                scrcpySavePath = Path.Combine(_lastScrcpyFolder, "Scrcpy_Video_Reconnect_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4");
            }

            string scrcpyDir = Path.GetDirectoryName(scrcpySavePath);
            if (!Directory.Exists(scrcpyDir))
                Directory.CreateDirectory(scrcpyDir);

            // Detect whether this scrcpy.exe is v1.x (DA2), v2.x (EU), or v3.x
            int majorVersion = GetScrcpyMajorVersion(_scrcpyPath);

            // Launch Scrcpy off-screen (--window-x -32000 --window-y -32000 --window-borderless)
            // so the standalone Scrcpy window NEVER flashes on the desktop before embedding!
            string dynamicArguments = (majorVersion >= 2)
                ? $"--no-audio -b 2M --render-driver=direct3d --window-borderless --window-x -32000 --window-y -32000 -r \"{scrcpySavePath}\""
                : $"-b 2M --render-driver=direct3d --window-borderless --window-x -32000 --window-y -32000 -r \"{scrcpySavePath}\"";

            string workingDir = Path.GetDirectoryName(_scrcpyPath);
            if (string.IsNullOrEmpty(workingDir)) workingDir = Application.StartupPath;

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = _scrcpyPath,
                Arguments = dynamicArguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDir
            };

            if (!string.IsNullOrEmpty(_adbPath) && File.Exists(_adbPath))
            {
                psi.EnvironmentVariables["ADB"] = Path.GetFullPath(_adbPath);
            }

            psi.EnvironmentVariables["SDL_RENDER_DRIVER"] = "direct3d";

            string lastScrcpyError = "";

            _scrcpyProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _scrcpyProcess.OutputDataReceived += (s, e) => { };
            _scrcpyProcess.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    lastScrcpyError = e.Data;
            };

            // Watchdog to auto-recover if Scrcpy ever exits unexpectedly mid-recording
            _scrcpyProcess.Exited += ScrcpyProcess_UnexpectedExitHandler;

            _embeddedScrcpyHwnd = IntPtr.Zero;

            _scrcpyProcess.Start();
            _scrcpyProcess.BeginOutputReadLine();
            _scrcpyProcess.BeginErrorReadLine();

            int waitTicks = 0;
            while (_scrcpyProcess.MainWindowHandle == IntPtr.Zero)
            {
                await Task.Delay(80);
                _scrcpyProcess.Refresh();
                waitTicks++;

                if (_scrcpyProcess.HasExited || waitTicks > 180)
                {
                    if (!isAutoReconnect && !string.IsNullOrEmpty(lastScrcpyError))
                    {
                        MessageBox.Show($"ScreenCopy failed to start:\n{lastScrcpyError}\n\nPath: {_scrcpyPath}",
                            "Scrcpy Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    return false;
                }
            }

            // SAVE HANDLE BEFORE SetParent!
            _embeddedScrcpyHwnd = _scrcpyProcess.MainWindowHandle;

            // Ensure Form2 & panelContainer handles exist and layout is arranged BEFORE showing Form2
            if (!this.IsHandleCreated) this.CreateControl();
            if (!panelContainer.IsHandleCreated) panelContainer.CreateControl();

            ArrangeLayout();

            // Strip ALL borders, captions, frames, and 3D edges so there is ZERO gray line or extra box!
            int style = GetWindowLong(_embeddedScrcpyHwnd, GWL_STYLE);
            style = (style & ~WS_POPUP & ~WS_CAPTION & ~WS_THICKFRAME & ~WS_BORDER & ~WS_DLGFRAME) | WS_CHILD;

            int exStyle = GetWindowLong(_embeddedScrcpyHwnd, GWL_EXSTYLE);
            exStyle &= ~(WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE);

            SetParent(_embeddedScrcpyHwnd, panelContainer.Handle);
            SetWindowLong(_embeddedScrcpyHwnd, GWL_STYLE, style);
            SetWindowLong(_embeddedScrcpyHwnd, GWL_EXSTYLE, exStyle);
            SetWindowPos(_embeddedScrcpyHwnd, IntPtr.Zero, 0, 0, panelContainer.ClientSize.Width, panelContainer.ClientSize.Height, SWP_NOZORDER | SWP_FRAMECHANGED);

            // Reset cached panel dimensions and size the embedded window to 100% of panelContainer
            _lastPanelWidth = 0;
            _lastPanelHeight = 0;
            _forceResizeTicks = 3;
            ResizeEmbeddedWindow(true);

            // Show Form2 once — completely ready and embedded without any flickering!
            if (!this.Visible)
            {
                this.Show();
            }

            ResizeEmbeddedWindow(true);
            _scrcpyResizeTimer.Start();

            return true;
        }

        /// <summary>
        /// Auto-recovers Scrcpy if the head unit encoder drops frames or exits unexpectedly mid-recording
        /// </summary>
        private async void ScrcpyProcess_UnexpectedExitHandler(object sender, EventArgs e)
        {
            if (_isManualScrcpyStop || _isRestartingScrcpy || this.IsDisposed || !this.IsHandleCreated)
                return;

            _isRestartingScrcpy = true;
            try
            {
                await Task.Delay(1500);

                if (_isManualScrcpyStop || this.IsDisposed || !this.IsHandleCreated)
                    return;

                if (!string.IsNullOrEmpty(_adbPath) && File.Exists(_adbPath))
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = _adbPath,
                        Arguments = "devices",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    using (Process p = Process.Start(psi))
                    {
                        string output = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(2000);
                        if (output.Contains("\tdevice") && !_isManualScrcpyStop)
                        {
                            this.BeginInvoke(new Action(async () =>
                            {
                                if (!_isManualScrcpyStop && !this.IsDisposed)
                                {
                                    await StartAndEmbedScrcpy(true);
                                }
                            }));
                        }
                    }
                }
            }
            catch { }
            finally
            {
                _isRestartingScrcpy = false;
            }
        }

        private void ResizeEmbeddedWindow(bool force = false)
        {
            if (_scrcpyProcess != null && !_scrcpyProcess.HasExited && _embeddedScrcpyHwnd != IntPtr.Zero)
            {
                int currentW = panelContainer.ClientSize.Width;
                int currentH = panelContainer.ClientSize.Height;

                if (currentW <= 0 || currentH <= 0)
                    return;

                // Fill 100% of panelContainer (0, 0, currentW, currentH) so no extra sub-box or border line ever appears!
                if (force || _lastPanelWidth != currentW || _lastPanelHeight != currentH || _forceResizeTicks > 0)
                {
                    _lastPanelWidth = currentW;
                    _lastPanelHeight = currentH;

                    MoveWindow(_embeddedScrcpyHwnd, 0, 0, currentW, currentH, true);
                    if (_forceResizeTicks > 0) _forceResizeTicks--;
                }
            }
        }

        public void StopScrcpyGracefully()
        {
            _isManualScrcpyStop = true;
            _scrcpySaveCancelled = false;

            if (_scrcpyProcess != null)
            {
                try
                {
                    _scrcpyProcess.Exited -= ScrcpyProcess_UnexpectedExitHandler;

                    if (!_scrcpyProcess.HasExited)
                    {
                        IntPtr targetHwnd = _embeddedScrcpyHwnd != IntPtr.Zero ? _embeddedScrcpyHwnd : _scrcpyProcess.MainWindowHandle;

                        if (targetHwnd != IntPtr.Zero)
                        {
                            PostMessage(targetHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        }

                        if (!_scrcpyProcess.WaitForExit(5000))
                        {
                            _scrcpyProcess.Kill();
                        }
                    }
                }
                catch { }
                finally
                {
                    _embeddedScrcpyHwnd = IntPtr.Zero;
                }
            }
        }

        // --- WEBCAM RECORDING LOGIC ---

        private void TurnCameraOn()
        {
            if (videoSource == null)
            {
                videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (videoDevices.Count > 0)
                {
                    videoSource = new VideoCaptureDevice(videoDevices[0].MonikerString);
                    videoSource.NewFrame += new NewFrameEventHandler(VideoSource_NewFrame);
                    videoSource.Start();
                }
            }

            if (isRecording && waveIn == null && !string.IsNullOrEmpty(tempAudioFile))
            {
                try
                {
                    if (WaveInEvent.DeviceCount > 0)
                    {
                        waveIn = new WaveInEvent();
                        waveIn.WaveFormat = new WaveFormat(44100, 16, 1);
                        waveWriter = new WaveFileWriter(tempAudioFile, waveIn.WaveFormat);
                        waveIn.DataAvailable += WaveIn_DataAvailable;
                        waveIn.StartRecording();
                    }
                }
                catch
                {
                    if (waveIn != null)
                    {
                        try { waveIn.Dispose(); } catch { }
                        waveIn = null;
                    }
                }
            }
        }

        private void TurnCameraOff()
        {
            if (videoSource != null)
            {
                videoSource.NewFrame -= new NewFrameEventHandler(VideoSource_NewFrame);
                videoSource.SignalToStop();
                videoSource.WaitForStop();
                videoSource = null;
            }

            if (waveIn != null)
            {
                try
                {
                    waveIn.StopRecording();
                    waveIn.DataAvailable -= WaveIn_DataAvailable;
                    waveIn.Dispose();
                    waveIn = null;
                }
                catch { }
            }

            lock (_audioLock)
            {
                if (waveWriter != null)
                {
                    try
                    {
                        waveWriter.Dispose();
                        waveWriter = null;
                    }
                    catch { }
                }
            }
        }

        public bool testCamStart(bool isAutoReconnect = false)
        {
            FilterInfoCollection checkDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
            if (checkDevices.Count == 0)
            {
                MessageBox.Show("No webcam detected on this PC!", "Camera Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (_webcamSaveCancelled) return false;

            if (!isAutoReconnect)
            {
                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Title = "Choose where to save the Webcam video";
                    sfd.Filter = "MP4 Video File|*.mp4";
                    sfd.FileName = "Webcam_Video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4";

                    if (sfd.ShowDialog() != DialogResult.OK)
                    {
                        _webcamSaveCancelled = true;
                        saveFileName = "";
                        tempVideoFile = "";
                        tempAudioFile = "";
                        isRecording = false;
                        return false;
                    }

                    _webcamSaveCancelled = false;
                    saveFileName = sfd.FileName;
                    _lastWebcamFolder = Path.GetDirectoryName(saveFileName);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(_lastWebcamFolder)) _lastWebcamFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                saveFileName = Path.Combine(_lastWebcamFolder, "Webcam_Video_Reconnect_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4");
            }

            string directory = Path.GetDirectoryName(saveFileName);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

            string baseName = Path.GetFileNameWithoutExtension(saveFileName);
            tempVideoFile = Path.Combine(directory, baseName + "_TEMP_VIDEO.mp4");
            tempAudioFile = Path.Combine(directory, baseName + "_TEMP_AUDIO.wav");

            while (_videoFrameQueue.TryDequeue(out Bitmap oldFrame)) oldFrame.Dispose();

            _writerFailed = false;
            needToOpenWriter = true;
            isRecording = true;
            isStopping = false;

            TurnCameraOn();

            _videoWriteTokenSource = new CancellationTokenSource();
            _videoSaverTask = Task.Run(() => BackgroundVideoWriter(_videoWriteTokenSource.Token));

            ArrangeLayout();
            return true;
        }

        public void testCamStop()
        {
            _webcamSaveCancelled = false;

            if ((!isRecording && videoSource == null) || isStopping) return;
            isStopping = true;
            isRecording = false;

            if (_videoWriteTokenSource != null) _videoWriteTokenSource.Cancel();
            if (_videoSaverTask != null) { try { _videoSaverTask.Wait(3000); } catch { } }

            TurnCameraOff();

            lock (_writerLock)
            {
                try { if (videoWriter != null) CloseVideoWriterSafe(); } catch { }
            }

            if (!string.IsNullOrEmpty(tempVideoFile))
            {
                MergeVideoAndAudio(tempVideoFile, tempAudioFile, saveFileName);
                tempVideoFile = "";
                tempAudioFile = "";
            }

            isStopping = false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void OpenVideoWriterSafe(string filePath, int width, int height)
        {
            var writer = new VideoFileWriter();
            writer.Open(filePath, width, height, 25, VideoCodec.MPEG4, 4000000);
            videoWriter = writer;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void WriteVideoFrameSafe(Bitmap frame)
        {
            var writer = videoWriter as VideoFileWriter;
            if (writer != null && writer.IsOpen) writer.WriteVideoFrame(frame);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CloseVideoWriterSafe()
        {
            var writer = videoWriter as VideoFileWriter;
            if (writer != null)
            {
                if (writer.IsOpen) writer.Close();
                writer.Dispose();
            }
            videoWriter = null;
        }

        private void MergeVideoAndAudio(string videoPath, string audioPath, string outputPath)
        {
            if (!File.Exists(videoPath)) return;

            bool hasValidAudio = File.Exists(audioPath) && new FileInfo(audioPath).Length > 100;
            if (!hasValidAudio)
            {
                try
                {
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                    File.Move(videoPath, outputPath);
                    if (File.Exists(audioPath)) File.Delete(audioPath);
                }
                catch { }
                return;
            }

            if (!string.IsNullOrEmpty(ADB_LOGS.scrcpy_path)) _scrcpyPath = ADB_LOGS.scrcpy_path;

            string ffmpegExe = "ffmpeg.exe";
            string localFfmpeg = Path.Combine(Application.StartupPath, "ffmpeg.exe");
            string scrcpyDirFfmpeg = !string.IsNullOrEmpty(_scrcpyPath) ? Path.Combine(Path.GetDirectoryName(_scrcpyPath), "ffmpeg.exe") : "";

            if (File.Exists(localFfmpeg)) ffmpegExe = localFfmpeg;
            else if (!string.IsNullOrEmpty(scrcpyDirFfmpeg) && File.Exists(scrcpyDirFfmpeg)) ffmpegExe = scrcpyDirFfmpeg;

            using (Form progressForm = new Form())
            {
                progressForm.Text = "Saving Video";
                progressForm.Size = new Size(350, 120);
                progressForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                progressForm.StartPosition = FormStartPosition.CenterScreen;
                progressForm.ControlBox = false;

                Label lbl = new Label() { Text = "Webcam video is saved.", Location = new Point(20, 20), AutoSize = true };
                progressForm.Controls.Add(lbl);

                progressForm.Shown += async (s, ev) =>
                {
                    await Task.Run(() =>
                    {
                        try
                        {
                            ProcessStartInfo psi = new ProcessStartInfo
                            {
                                FileName = ffmpegExe,
                                Arguments = $"-y -i \"{videoPath}\" -i \"{audioPath}\" -c:v copy -c:a aac \"{outputPath}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            using (Process p = Process.Start(psi)) { p.WaitForExit(); }

                            if (File.Exists(outputPath)) { File.Delete(videoPath); File.Delete(audioPath); }
                            else { File.Move(videoPath, outputPath); if (File.Exists(audioPath)) File.Delete(audioPath); }
                        }
                        catch
                        {
                            try
                            {
                                if (File.Exists(outputPath)) File.Delete(outputPath);
                                File.Move(videoPath, outputPath);
                                if (File.Exists(audioPath)) File.Delete(audioPath);
                            }
                            catch { }
                        }
                    });
                    if (progressForm.IsHandleCreated) progressForm.Close();
                };
                progressForm.ShowDialog(this);
            }
        }

        private void VideoSource_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            if (isRecording && !_writerFailed && _videoFrameQueue.Count < 30)
                _videoFrameQueue.Enqueue((Bitmap)eventArgs.Frame.Clone());

            if ((DateTime.Now - _lastUiUpdate).TotalMilliseconds > 66)
            {
                _lastUiUpdate = DateTime.Now;
                Bitmap displayImage = (Bitmap)eventArgs.Frame.Clone();
                try
                {
                    pictureBox1.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (pictureBox1.IsDisposed || pictureBox1.Disposing) { displayImage.Dispose(); return; }
                            var oldImage = pictureBox1.Image;
                            pictureBox1.Image = displayImage;
                            if (oldImage != null) oldImage.Dispose();
                        }
                        catch { displayImage.Dispose(); }
                    }));
                }
                catch { displayImage.Dispose(); }
            }
        }

        private void BackgroundVideoWriter(CancellationToken token)
        {
            while (!token.IsCancellationRequested || !_videoFrameQueue.IsEmpty)
            {
                if (_videoFrameQueue.TryDequeue(out Bitmap frame))
                {
                    lock (_writerLock)
                    {
                        try
                        {
                            if (needToOpenWriter && !_writerFailed)
                            {
                                _recordingWidth = frame.Width % 2 == 0 ? frame.Width : frame.Width - 1;
                                _recordingHeight = frame.Height % 2 == 0 ? frame.Height : frame.Height - 1;
                                OpenVideoWriterSafe(tempVideoFile, _recordingWidth, _recordingHeight);
                                needToOpenWriter = false;
                            }

                            if (videoWriter != null && !_writerFailed)
                            {
                                if (frame.Width == _recordingWidth && frame.Height == _recordingHeight)
                                    WriteVideoFrameSafe(frame);
                                else
                                {
                                    using (Bitmap resizedFrame = new Bitmap(frame, _recordingWidth, _recordingHeight))
                                        WriteVideoFrameSafe(resizedFrame);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (needToOpenWriter)
                            {
                                _writerFailed = true;
                                needToOpenWriter = false;
                                this.BeginInvoke(new Action(() =>
                                {
                                    MessageBox.Show("Video Writer Error:\n" + ex.Message, "Recording Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                }));
                            }
                        }
                    }
                    frame.Dispose();
                }
                else Thread.Sleep(5);
            }
        }

        private void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            lock (_audioLock)
            {
                if (waveWriter != null)
                {
                    waveWriter.Write(e.Buffer, 0, e.BytesRecorded);
                    waveWriter.Flush();
                }
            }
        }

        private void Form2_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.Cancel) return;

            if (_scrcpyResizeTimer != null)
            {
                _scrcpyResizeTimer.Stop();
                _scrcpyResizeTimer.Dispose();
            }

            if (isRecording || videoSource != null) testCamStop();
            StopScrcpyGracefully();
        }

        private static int GetWindowLong(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? unchecked((int)GetWindowLongPtr64(hWnd, nIndex).ToInt64())
                : GetWindowLong32(hWnd, nIndex);
        }

        private static IntPtr SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, new IntPtr(unchecked((uint)dwNewLong)))
                : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong));
        }

        private void label1_Click(object sender, EventArgs e) { }
    }
}