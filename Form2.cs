using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
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

        private const uint WM_CLOSE = 0x0010;
        private const int GWL_STYLE = -16;
        private const int WS_CHILD = 0x40000000;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;

        private string _adbPath;
        private string _scrcpyPath;
        private Process _scrcpyProcess;

        private FilterInfoCollection videoDevices;
        private VideoCaptureDevice videoSource;
        private VideoFileWriter videoWriter;

        // --- NAudio Variables ---
        private WaveInEvent waveIn;
        private WaveFileWriter waveWriter;
        private readonly object _audioLock = new object(); // FIX 4: Thread safety for Audio

        // --- Anti-Lag Video Variables ---
        private ConcurrentQueue<Bitmap> _videoFrameQueue = new ConcurrentQueue<Bitmap>();
        private CancellationTokenSource _videoWriteTokenSource;
        private Task _videoSaverTask; // FIX 3: Required to wait for frame queue to empty safely

        // --- File Saving Variables ---
        private bool isRecording = false;
        private bool isStopping = false; // FIX 2: Prevents double triggering
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
            }

            if (panelContainer != null)
            {
                panelContainer.Parent = this;
                panelContainer.BackColor = Color.Black;
                panelContainer.Visible = false;
            }

            _scrcpyResizeTimer = new System.Windows.Forms.Timer();
            _scrcpyResizeTimer.Interval = 200;
            _scrcpyResizeTimer.Tick += (s, ev) => ResizeEmbeddedWindow();

            if (panelContainer != null) panelContainer.Resize += PanelContainer_Resize;

            Load += Form2_Load;
            FormClosing += Form2_FormClosing;
            this.Resize += Form2_Resize;
        }

        private void Form2_Load(object sender, EventArgs e) { }

        public void ArrangeLayout()
        {
            if (this.InvokeRequired) { this.Invoke(new Action(ArrangeLayout)); return; }

            bool hasScrcpy = (_scrcpyProcess != null && !_scrcpyProcess.HasExited);
            bool hasCam = isRecording;

            if (hasScrcpy && hasCam)
            {
                panelContainer.Visible = true;
                pictureBox1.Visible = true;
                panelContainer.Dock = DockStyle.Left;
                panelContainer.Width = this.ClientSize.Width / 2;
                pictureBox1.Dock = DockStyle.Fill;
                pictureBox1.BringToFront();
            }
            else if (hasScrcpy)
            {
                pictureBox1.Visible = false;
                panelContainer.Visible = true;
                panelContainer.Dock = DockStyle.Fill;
                panelContainer.BringToFront();
            }
            else if (hasCam)
            {
                panelContainer.Visible = false;
                pictureBox1.Visible = true;
                pictureBox1.Dock = DockStyle.Fill;
                pictureBox1.BringToFront();
            }
        }

        private void Form2_Resize(object sender, EventArgs e)
        {
            ResizeEmbeddedWindow(true);
            ArrangeLayout();
        }

        private void PanelContainer_Resize(object sender, EventArgs e) { ResizeEmbeddedWindow(); }

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

            if (waveIn == null)
            {
                try
                {
                    waveIn = new WaveInEvent();
                    waveIn.WaveFormat = new WaveFormat(44100, 16, 1);

                    if (!string.IsNullOrEmpty(tempAudioFile))
                    {
                        waveWriter = new WaveFileWriter(tempAudioFile, waveIn.WaveFormat);
                    }

                    waveIn.DataAvailable += WaveIn_DataAvailable;
                    waveIn.StartRecording();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Could not start microphone: " + ex.Message);
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

            // FIX 4: Thread-safe disposal of audio writer
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
            if (!isAutoReconnect)
            {
                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Title = "Choose where to save the Webcam video";
                    sfd.Filter = "MP4 Video File|*.mp4";
                    sfd.FileName = "Webcam_Video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4";

                    if (sfd.ShowDialog() != DialogResult.OK) return false;

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
            string baseName = Path.GetFileNameWithoutExtension(saveFileName);
            tempVideoFile = Path.Combine(directory, baseName + "_TEMP_VIDEO.mp4");
            tempAudioFile = Path.Combine(directory, baseName + "_TEMP_AUDIO.wav");

            TurnCameraOn();
            needToOpenWriter = true;
            isRecording = true;
            isStopping = false;

            // Start Background Video Saver
            _videoWriteTokenSource = new CancellationTokenSource();
            _videoSaverTask = Task.Run(() => BackgroundVideoWriter(_videoWriteTokenSource.Token));

            ArrangeLayout();
            return true;
        }

        public void testCamStop()
        {
            // FIX 2: Prevent double triggers
            if (!isRecording || isStopping) return;
            isStopping = true;
            isRecording = false;

            if (_videoWriteTokenSource != null)
            {
                _videoWriteTokenSource.Cancel();
            }

            // FIX 3: Wait for the frame queue to completely write to disk before closing stream
            if (_videoSaverTask != null)
            {
                try { _videoSaverTask.Wait(3000); } catch { }
            }

            TurnCameraOff();

            lock (_writerLock)
            {
                try
                {
                    if (videoWriter != null && videoWriter.IsOpen)
                    {
                        videoWriter.Close();
                        videoWriter.Dispose();
                        videoWriter = null;
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(tempVideoFile) && !string.IsNullOrEmpty(tempAudioFile))
            {
                MergeVideoAndAudio(tempVideoFile, tempAudioFile, saveFileName);

                // Erase temp references so they aren't processed twice
                tempVideoFile = "";
                tempAudioFile = "";
            }
        }

        // FIX 1: Synchronous UI blocking merge to prevent broken MP4 files if App closes early
        private void MergeVideoAndAudio(string videoPath, string audioPath, string outputPath)
        {
            if (!File.Exists(videoPath) || !File.Exists(audioPath)) return;

            // Use a popup modal to delay environment exit until the MP4 is fully constructed
            using (Form progressForm = new Form())
            {
                progressForm.Text = "Saving Video";
                progressForm.Size = new Size(350, 120);
                progressForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                progressForm.StartPosition = FormStartPosition.CenterScreen;
                progressForm.ControlBox = false;

                Label lbl = new Label() { Text = "Merging Audio and Video...\nPlease do not close the app.", Location = new Point(20, 20), AutoSize = true };
                progressForm.Controls.Add(lbl);

                progressForm.Shown += async (s, ev) =>
                {
                    await Task.Run(() =>
                    {
                        try
                        {
                            ProcessStartInfo psi = new ProcessStartInfo
                            {
                                FileName = "ffmpeg.exe",
                                Arguments = $"-y -i \"{videoPath}\" -i \"{audioPath}\" -c:v copy -c:a aac \"{outputPath}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };

                            using (Process p = Process.Start(psi))
                            {
                                p.WaitForExit();
                            }

                            if (File.Exists(outputPath))
                            {
                                File.Delete(videoPath);
                                File.Delete(audioPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("Could not merge audio and video. Please ensure 'ffmpeg.exe' is in your app folder!\n\n" + ex.Message, "Missing FFmpeg");
                        }
                    });

                    if (progressForm.IsHandleCreated) progressForm.Close();
                };

                progressForm.ShowDialog(this);
            }
        }

        public async Task<bool> StartAndEmbedScrcpy(bool isAutoReconnect = false)
        {
            string scrcpySavePath = "";

            if (!isAutoReconnect)
            {
                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Title = "Choose where to save the Android video";
                    sfd.Filter = "MP4 Video File|*.mp4";
                    sfd.FileName = "Scrcpy_Video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4";

                    if (sfd.ShowDialog() != DialogResult.OK) return false;

                    scrcpySavePath = sfd.FileName;
                    _lastScrcpyFolder = Path.GetDirectoryName(scrcpySavePath);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(_lastScrcpyFolder)) _lastScrcpyFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                scrcpySavePath = Path.Combine(_lastScrcpyFolder, "Scrcpy_Video_Reconnect_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4");
            }

            // --- AUTO-DETECT SCRCPY VERSION ---
            string dynamicArguments = $"-m 1024 --video-bit-rate 2M --max-fps 60 --window-borderless -r \"{scrcpySavePath}\"";
            try
            {
                ProcessStartInfo versionCheck = new ProcessStartInfo
                {
                    FileName = _scrcpyPath,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(versionCheck))
                {
                    string output = p.StandardOutput.ReadLine();
                    p.WaitForExit();
                    if (output != null && output.Contains("scrcpy 1."))
                    {
                        dynamicArguments = $"-m 1024 -b 2M --max-fps 60 --window-borderless -r \"{scrcpySavePath}\"";
                    }
                }
            }
            catch { }

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = _scrcpyPath,
                Arguments = dynamicArguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true
            };

            psi.EnvironmentVariables["ADB"] = _adbPath;
            psi.WorkingDirectory = System.IO.Path.GetDirectoryName(_scrcpyPath);

            _scrcpyProcess = Process.Start(psi);
            _scrcpyProcess.EnableRaisingEvents = true;

            while (_scrcpyProcess.MainWindowHandle == IntPtr.Zero)
            {
                await Task.Delay(100);
                _scrcpyProcess.Refresh();

                // FIX 5: If Scrcpy crashed because phone disconnected, return false!
                if (_scrcpyProcess.HasExited) return false;
            }

            await Task.Delay(200);
            IntPtr hwnd = _scrcpyProcess.MainWindowHandle;

            int style = GetWindowLong(hwnd, GWL_STYLE);
            style = (style & ~WS_CAPTION & ~WS_THICKFRAME) | WS_CHILD;

            SetParent(hwnd, panelContainer.Handle);
            SetWindowLong(hwnd, GWL_STYLE, style);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

            _forceResizeTicks = 25;
            ResizeEmbeddedWindow(true);
            _scrcpyResizeTimer.Start();

            ArrangeLayout();
            return true;
        }

        private void ResizeEmbeddedWindow(bool force = false)
        {
            if (_scrcpyProcess != null && !_scrcpyProcess.HasExited && _scrcpyProcess.MainWindowHandle != IntPtr.Zero)
            {
                if (force || _lastPanelWidth != panelContainer.Width || _lastPanelHeight != panelContainer.Height || _forceResizeTicks > 0)
                {
                    _lastPanelWidth = panelContainer.Width;
                    _lastPanelHeight = panelContainer.Height;
                    MoveWindow(_scrcpyProcess.MainWindowHandle, 0, 0, _lastPanelWidth, _lastPanelHeight, true);
                    if (_forceResizeTicks > 0) _forceResizeTicks--;
                }
            }
        }

        private void VideoSource_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            if (isRecording)
            {
                if (_videoFrameQueue.Count < 30)
                {
                    _videoFrameQueue.Enqueue((Bitmap)eventArgs.Frame.Clone());
                }
            }

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
                            if (needToOpenWriter)
                            {
                                videoWriter = new VideoFileWriter();
                                videoWriter.Open(tempVideoFile, frame.Width, frame.Height, 25, VideoCodec.MPEG4, 4000000);
                                needToOpenWriter = false;
                            }
                            if (videoWriter != null && videoWriter.IsOpen)
                            {
                                videoWriter.WriteVideoFrame(frame);
                            }
                        }
                        catch { }
                    }
                    frame.Dispose();
                }
                else
                {
                    Thread.Sleep(5);
                }
            }
        }

        private void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            // FIX 4: Prevent App crash by locking object
            lock (_audioLock)
            {
                if (waveWriter != null)
                {
                    waveWriter.Write(e.Buffer, 0, e.BytesRecorded);
                    waveWriter.Flush();
                }
            }
        }

        public void StopScrcpyGracefully()
        {
            if (_scrcpyProcess != null && !_scrcpyProcess.HasExited)
            {
                try
                {
                    SendMessage(_scrcpyProcess.MainWindowHandle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    _scrcpyProcess.WaitForExit(3000);
                    if (!_scrcpyProcess.HasExited) _scrcpyProcess.Kill();
                }
                catch { }
            }
        }

        private void Form2_FormClosing(object sender, FormClosingEventArgs e)
        {
            // FIX 2: Form Closing uses the EXACT same safe-stop flow to prevent double processing
            if (_scrcpyResizeTimer != null)
            {
                _scrcpyResizeTimer.Stop();
                _scrcpyResizeTimer.Dispose();
            }

            testCamStop();
            StopScrcpyGracefully();
        }

        private static int GetWindowLong(IntPtr hWnd, int nIndex) { return IntPtr.Size == 8 ? (int)GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex); }
        private static IntPtr SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong) { return IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, new IntPtr(dwNewLong)) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong)); }

        private void label1_Click(object sender, EventArgs e)
        {

        }
    }
}