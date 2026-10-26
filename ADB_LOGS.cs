using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Accord.Video;
using Accord.Video.DirectShow;
using Accord.Video.FFMPEG;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;

namespace TestCamera
{
    public partial class ADB_LOGS : Form
    {
        private FilterInfoCollection videoDevices;
        public static bool deviceFlag = true;
        public static bool skipScrcpy = false;

        // Tracks if user clicked Cancel/X on video save so ONLY logs run & save
        private bool _videoRecordingCancelledByUser = false;

        private Process adbLogProcess = null;
        private Process kernelLogProcess = null;

        private bool isDeviceConnected = false;
        private bool hasStartedManually = false;
        private bool _isPaused = false;
        private bool _isCheckingDevice = false;
        private bool _isStartingUp = false;

        public static string adbPath = "adb.exe";
        public static string scrcpy_path = "scrcpy.exe";

        public Form2 form2;

        private enum LogView { None, ADB, Kernel, VHAL, PHAL }
        private LogView currentView = LogView.None;

        private List<LogLine> uiAdbList = new List<LogLine>(150000);
        private List<LogLine> uiKernelList = new List<LogLine>(150000);
        private List<LogLine> uiVhalList = new List<LogLine>(150000);
        private List<LogLine> uiPhalList = new List<LogLine>(150000);

        private List<LogLine> filteredAdbList = new List<LogLine>(150000);
        private List<LogLine> filteredKernelList = new List<LogLine>(150000);
        private List<LogLine> filteredVhalList = new List<LogLine>(150000);
        private List<LogLine> filteredPhalList = new List<LogLine>(150000);

        private readonly ConcurrentQueue<LogLine> adbQueue = new ConcurrentQueue<LogLine>();
        private readonly ConcurrentQueue<LogLine> kernelQueue = new ConcurrentQueue<LogLine>();
        private readonly ConcurrentQueue<LogLine> vhalQueue = new ConcurrentQueue<LogLine>();
        private readonly ConcurrentQueue<LogLine> phalQueue = new ConcurrentQueue<LogLine>();

        private string _continuousLogFolder = "";
        private readonly ConcurrentQueue<string> adbDiskQueue = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> kernelDiskQueue = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> vhalDiskQueue = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> phalDiskQueue = new ConcurrentQueue<string>();

        private string currentSearchText = "";
        private bool IsSearching => !string.IsNullOrWhiteSpace(currentSearchText);
        private readonly Timer searchDebounceTimer;
        private readonly Timer flushTimer;

        private bool _isWritingToDisk = false;
        private readonly object _diskWriteLock = new object();

        private static readonly Regex AdbRegex = new Regex(@"^(\d{2}-\d{2})\s+(\d{2}:\d{2}:\d{2}\.\d{3})\s+(?:[\w-]+\s+)?(\d+)\s+(\d+)\s+([VDIWEFASvdiwefas])\s+(.*?)\s*:\s*(.*)$", RegexOptions.Compiled);
        private static readonly Regex AnsiRegex = new Regex(@"\x1B\[[0-9;]*[a-zA-Z]", RegexOptions.Compiled);

        private static volatile string _lastAdbDate = DateTime.Now.ToString("MM-dd");
        private static volatile string _lastAdbTime = DateTime.Now.ToString("HH:mm:ss.fff");

        public ADB_LOGS()
        {
            InitializeComponent();
            this.Text = "ADB LOGS";

            // Resolve full paths for adb.exe and scrcpy.exe inside the installed application folder
            ResolveToolPaths();

            panel1.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            panel2.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            panel3.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            panel4.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            guna2TextBox1.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            guna2Button5.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            _continuousLogFolder = Path.Combine(Path.GetTempPath(), "TestCamera_ContinuousLogs");
            if (!Directory.Exists(_continuousLogFolder))
                Directory.CreateDirectory(_continuousLogFolder);
            else
            {
                try { foreach (var f in Directory.GetFiles(_continuousLogFolder)) File.Delete(f); } catch { }
            }

            guna2Button1.Click -= Guna2Button1_Click;
            guna2Button1.Click += Guna2Button1_Click;

            guna2Button2.Click -= Guna2Button2_Click;
            guna2Button2.Click += Guna2Button2_Click;

            guna2Button3.Click -= Guna2Button3_Click;
            guna2Button3.Click += Guna2Button3_Click;

            guna2Button4.Click -= Guna2Button4_Click;
            guna2Button4.Click += Guna2Button4_Click;

            guna2Button7.Click -= Guna2Button7_Click;
            guna2Button7.Click += Guna2Button7_Click;

            guna2Button5.Click -= Guna2Button5_Click;
            guna2Button5.Click += Guna2Button5_Click;

            Guna2HtmlLabel3.BackColor = Color.Transparent;
            Guna2HtmlLabel3.ForeColor = Color.White;

            Control searchBox = Controls.Find("guna2TextBox1", true).FirstOrDefault();
            if (searchBox != null)
            {
                searchBox.TextChanged -= Guna2TextBoxSearch_TextChanged;
                searchBox.TextChanged += Guna2TextBoxSearch_TextChanged;
            }

            WireButtonByText(this, "PHAL", Guna2ButtonPhal_Click);

            if (adb_timer != null)
            {
                adb_timer.Interval = 1000;
                adb_timer.Tick -= Timer1_Tick;
                adb_timer.Tick += Timer1_Tick;
            }

            Guna2HtmlLabel3.Text = "No device is connected";
            guna2Button1.Enabled = true;
            guna2Button2.Enabled = false;

            SetupGridColumns();

            searchDebounceTimer = new Timer { Interval = 350 };
            searchDebounceTimer.Tick += async (s, e) =>
            {
                searchDebounceTimer.Stop();
                await RebuildFilterAsync();
            };

            flushTimer = new Timer { Interval = 200 };
            flushTimer.Tick += FlushTimer_Tick;
            flushTimer.Start();

            adb_timer?.Start();
            _ = LoadDeviceIdAsync();
        }

        private void ResolveToolPaths()
        {
            string startupDir = Application.StartupPath;

            string localAdb = Path.Combine(startupDir, "adb.exe");
            if (File.Exists(localAdb))
            {
                adbPath = localAdb;
            }

            string localScrcpy = Path.Combine(startupDir, "scrcpy.exe");
            if (File.Exists(localScrcpy))
            {
                scrcpy_path = localScrcpy;
            }
        }

        private void BroadcastSystemMessage(string message, Color color, string level = "I")
        {
            LogLine sysMsg = new LogLine
            {
                Date = DateTime.Now.ToString("MM-dd"),
                Time = DateTime.Now.ToString("HH:mm:ss.fff"),
                Level = level,
                Tag = "SYSTEM",
                Message = message,
                ForeColor = color
            };

            adbQueue.Enqueue(sysMsg);
            kernelQueue.Enqueue(sysMsg);
            vhalQueue.Enqueue(sysMsg);
            phalQueue.Enqueue(sysMsg);
        }

        public bool CheckDevice(bool isAutoReconnect = false)
        {
            try
            {
                videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (videoDevices.Count == 0)
                {
                    deviceFlag = false;
                    if (!isAutoReconnect)
                    {
                        DialogResult res = MessageBox.Show(
                            "No camera detected!\n\n• Click OK to continue for ScreenCopy\n• Click Cancel (or 'X') to run ADB Logs ONLY",
                            "Camera Missing",
                            MessageBoxButtons.OKCancel,
                            MessageBoxIcon.Information);

                        skipScrcpy = (res != DialogResult.OK);
                    }
                    return false;
                }
                deviceFlag = true;
                skipScrcpy = false;
                return true;
            }
            catch
            {
                deviceFlag = false;
                return false;
            }
        }

        private void ClearAllBuffers()
        {
            uiAdbList = new List<LogLine>(150000);
            uiKernelList = new List<LogLine>(150000);
            uiVhalList = new List<LogLine>(150000);
            uiPhalList = new List<LogLine>(150000);

            filteredAdbList = new List<LogLine>(150000);
            filteredKernelList = new List<LogLine>(150000);
            filteredVhalList = new List<LogLine>(150000);
            filteredPhalList = new List<LogLine>(150000);

            while (adbQueue.TryDequeue(out _)) { }
            while (kernelQueue.TryDequeue(out _)) { }
            while (vhalQueue.TryDequeue(out _)) { }
            while (phalQueue.TryDequeue(out _)) { }

            while (adbDiskQueue.TryDequeue(out _)) { }
            while (kernelDiskQueue.TryDequeue(out _)) { }
            while (vhalDiskQueue.TryDequeue(out _)) { }
            while (phalDiskQueue.TryDequeue(out _)) { }

            try
            {
                if (Directory.Exists(_continuousLogFolder))
                {
                    foreach (string file in Directory.GetFiles(_continuousLogFolder))
                        File.Delete(file);
                }
            }
            catch { }

            guna2GridLogs.RowCount = 0;
        }

        private void WireButtonByText(Control parent, string text, EventHandler handler)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is ButtonBase btn && btn.Text != null && btn.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    btn.Click -= handler;
                    btn.Click += handler;
                }
                else
                {
                    WireButtonByText(c, text, handler);
                }
            }
        }

        private void SetupGridColumns()
        {
            EnableDoubleBuffered(guna2GridLogs);

            guna2GridLogs.Columns.Clear();

            _ = guna2GridLogs.Columns.Add("ColLine", "Line");
            guna2GridLogs.Columns["ColLine"].Width = 60;

            _ = guna2GridLogs.Columns.Add("ColDate", "Date");
            _ = guna2GridLogs.Columns.Add("ColTime", "Time");
            _ = guna2GridLogs.Columns.Add("ColLevel", "Level");
            _ = guna2GridLogs.Columns.Add("ColPID", "PID");
            _ = guna2GridLogs.Columns.Add("ColTID", "TID");
            _ = guna2GridLogs.Columns.Add("ColTag", "Tag");
            _ = guna2GridLogs.Columns.Add("ColMessage", "Message");

            guna2GridLogs.Columns["ColMessage"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            guna2GridLogs.ReadOnly = true;

            foreach (DataGridViewColumn col in guna2GridLogs.Columns) col.SortMode = DataGridViewColumnSortMode.NotSortable;

            guna2GridLogs.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            guna2GridLogs.GridColor = Color.Gainsboro;

            guna2GridLogs.BorderStyle = BorderStyle.None;
            guna2GridLogs.BackgroundColor = Color.White;
            guna2GridLogs.DefaultCellStyle.BackColor = Color.White;

            guna2GridLogs.AllowUserToAddRows = false;
            guna2GridLogs.AllowUserToOrderColumns = false;
            guna2GridLogs.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            guna2GridLogs.AllowUserToResizeRows = false;
            guna2GridLogs.RowHeadersVisible = false;
            guna2GridLogs.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            guna2GridLogs.ScrollBars = ScrollBars.Both;
            guna2GridLogs.VirtualMode = true;

            guna2GridLogs.ColumnHeaderMouseClick -= Guna2GridLogs_ColumnHeaderMouseClick;
            guna2GridLogs.ColumnHeaderMouseClick += Guna2GridLogs_ColumnHeaderMouseClick;

            guna2GridLogs.ShowCellToolTips = true;
            guna2GridLogs.ShowRowErrors = false;
            guna2GridLogs.ShowCellErrors = false;
            guna2GridLogs.ShowEditingIcon = false;
            guna2GridLogs.EnableHeadersVisualStyles = false;
            guna2GridLogs.RowTemplate.Height = 24;

            guna2GridLogs.CellValueNeeded += Guna2GridLogs_CellValueNeeded;
            guna2GridLogs.CellFormatting += Guna2GridLogs_CellFormatting;
            guna2GridLogs.CellPainting -= Guna2GridLogs_CellPainting;
            guna2GridLogs.CellPainting += Guna2GridLogs_CellPainting;
            guna2GridLogs.CellClick += Guna2GridLogs_CellClick;
        }

        private void Guna2GridLogs_ColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            guna2GridLogs.ClearSelection();
        }

        public static void EnableDoubleBuffered(Control control)
        {
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(control, true, null);
        }

        private void Guna2TextBoxSearch_TextChanged(object sender, EventArgs e)
        {
            if (sender is Control searchCtrl)
            {
                currentSearchText = searchCtrl.Text.Trim();
                searchDebounceTimer.Stop();
                searchDebounceTimer.Start();
            }
        }

        private bool ItemMatchesFilter(LogLine log, string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return true;

            searchText = searchText.Trim();
            string fullLogLine = $"{log.Date} {log.Time} {log.PID} {log.TID} {log.Level} {log.Tag} {log.Message}";
            string[] wordsToFind = searchText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string word in wordsToFind)
            {
                if (fullLogLine.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }
            return true;
        }

        private async Task RebuildFilterAsync()
        {
            if (!IsSearching)
            {
                filteredAdbList = new List<LogLine>();
                filteredKernelList = new List<LogLine>();
                filteredVhalList = new List<LogLine>();
                filteredPhalList = new List<LogLine>();
                RefreshGridCount();
                return;
            }

            string searchText = currentSearchText;

            int adbCount = uiAdbList.Count;
            int kernelCount = uiKernelList.Count;
            int vhalCount = uiVhalList.Count;
            int phalCount = uiPhalList.Count;

            var adbSnap = uiAdbList.Take(adbCount).ToList();
            var kernelSnap = uiKernelList.Take(kernelCount).ToList();
            var vhalSnap = uiVhalList.Take(vhalCount).ToList();
            var phalSnap = uiPhalList.Take(phalCount).ToList();

            var newFilteredAdb = new List<LogLine>();
            var newFilteredKernel = new List<LogLine>();
            var newFilteredVhal = new List<LogLine>();
            var newFilteredPhal = new List<LogLine>();

            await Task.Run(() =>
            {
                foreach (var log in adbSnap) if (ItemMatchesFilter(log, searchText)) newFilteredAdb.Add(log);
                foreach (var log in kernelSnap) if (ItemMatchesFilter(log, searchText)) newFilteredKernel.Add(log);
                foreach (var log in vhalSnap) if (ItemMatchesFilter(log, searchText)) newFilteredVhal.Add(log);
                foreach (var log in phalSnap) if (ItemMatchesFilter(log, searchText)) newFilteredPhal.Add(log);
            });

            for (int i = adbCount; i < uiAdbList.Count; i++) if (ItemMatchesFilter(uiAdbList[i], searchText)) newFilteredAdb.Add(uiAdbList[i]);
            for (int i = kernelCount; i < uiKernelList.Count; i++) if (ItemMatchesFilter(uiKernelList[i], searchText)) newFilteredKernel.Add(uiKernelList[i]);
            for (int i = vhalCount; i < uiVhalList.Count; i++) if (ItemMatchesFilter(uiVhalList[i], searchText)) newFilteredVhal.Add(uiVhalList[i]);
            for (int i = phalCount; i < uiPhalList.Count; i++) if (ItemMatchesFilter(uiPhalList[i], searchText)) newFilteredPhal.Add(uiPhalList[i]);

            filteredAdbList = newFilteredAdb;
            filteredKernelList = newFilteredKernel;
            filteredVhalList = newFilteredVhal;
            filteredPhalList = newFilteredPhal;

            RefreshGridCount();
        }

        private LogLine GetLogAt(int index)
        {
            try
            {
                if (currentView == LogView.ADB) return IsSearching ? (index < filteredAdbList.Count ? filteredAdbList[index] : null) : (index < uiAdbList.Count ? uiAdbList[index] : null);
                if (currentView == LogView.Kernel) return IsSearching ? (index < filteredKernelList.Count ? filteredKernelList[index] : null) : (index < uiKernelList.Count ? uiKernelList[index] : null);
                if (currentView == LogView.VHAL) return IsSearching ? (index < filteredVhalList.Count ? filteredVhalList[index] : null) : (index < uiVhalList.Count ? uiVhalList[index] : null);
                if (currentView == LogView.PHAL) return IsSearching ? (index < filteredPhalList.Count ? filteredPhalList[index] : null) : (index < uiPhalList.Count ? uiPhalList[index] : null);
            }
            catch { }
            return null;
        }

        private void RefreshGridCount()
        {
            if (currentView == LogView.ADB) UpdateGrid(IsSearching ? filteredAdbList.Count : uiAdbList.Count);
            else if (currentView == LogView.Kernel) UpdateGrid(IsSearching ? filteredKernelList.Count : uiKernelList.Count);
            else if (currentView == LogView.VHAL) UpdateGrid(IsSearching ? filteredVhalList.Count : uiVhalList.Count);
            else if (currentView == LogView.PHAL) UpdateGrid(IsSearching ? filteredPhalList.Count : uiPhalList.Count);

            guna2GridLogs.Invalidate();
        }

        private void Guna2GridLogs_CellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            LogLine log = GetLogAt(e.RowIndex);
            if (log == null || e.ColumnIndex >= 8) return;

            switch (e.ColumnIndex)
            {
                case 0: e.Value = (e.RowIndex + 1).ToString(); break;
                case 1: e.Value = log.Date; break;
                case 2: e.Value = log.Time; break;
                case 3: e.Value = log.Level; break;
                case 4: e.Value = log.PID; break;
                case 5: e.Value = log.TID; break;
                case 6: e.Value = log.Tag; break;
                case 7: e.Value = log.Message; break;
            }
        }

        private void Guna2GridLogs_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            LogLine log = GetLogAt(e.RowIndex);
            if (log != null)
            {
                if (e.ColumnIndex == 0)
                {
                    e.CellStyle.ForeColor = Color.Blue;
                }
                else
                {
                    e.CellStyle.ForeColor = log.ForeColor;
                }

                e.CellStyle.BackColor = Color.White;
                e.CellStyle.SelectionBackColor = Color.DodgerBlue;
                e.CellStyle.SelectionForeColor = Color.White;
            }
        }

        private void Guna2GridLogs_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.Value == null || !IsSearching) return;

            string cellText = e.Value.ToString();
            int matchIndex = cellText.IndexOf(currentSearchText, StringComparison.OrdinalIgnoreCase);

            if (matchIndex < 0) return;

            e.Paint(e.CellBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);

            string beforeMatch = cellText.Substring(0, matchIndex);
            string matchWord = cellText.Substring(matchIndex, currentSearchText.Length);
            string afterMatch = cellText.Substring(matchIndex + currentSearchText.Length);

            Color normalColor = e.State.HasFlag(DataGridViewElementStates.Selected) ? e.CellStyle.SelectionForeColor : e.CellStyle.ForeColor;
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

            Rectangle bounds = e.CellBounds;
            int currentX = bounds.X + 2;

            if (beforeMatch.Length > 0)
            {
                Size s1 = TextRenderer.MeasureText(e.Graphics, beforeMatch, e.CellStyle.Font, bounds.Size, flags);
                TextRenderer.DrawText(e.Graphics, beforeMatch, e.CellStyle.Font, new Rectangle(currentX, bounds.Y, s1.Width, bounds.Height), normalColor, flags);
                currentX += s1.Width;
            }

            Size s2 = TextRenderer.MeasureText(e.Graphics, matchWord, e.CellStyle.Font, bounds.Size, flags);
            Color highlightColor = e.State.HasFlag(DataGridViewElementStates.Selected) ? Color.White : Color.PaleVioletRed;
            TextRenderer.DrawText(e.Graphics, matchWord, e.CellStyle.Font, new Rectangle(currentX, bounds.Y, s2.Width, bounds.Height), highlightColor, flags);

            currentX += s2.Width;

            if (afterMatch.Length > 0)
            {
                int remainingWidth = bounds.Width - (currentX - bounds.X);
                if (remainingWidth > 0)
                {
                    TextRenderer.DrawText(e.Graphics, afterMatch, e.CellStyle.Font, new Rectangle(currentX, bounds.Y, remainingWidth, bounds.Height), normalColor, flags | TextFormatFlags.EndEllipsis);
                }
            }
            e.Handled = true;
        }

        private void Guna2GridLogs_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

            LogLine log = GetLogAt(e.RowIndex);
            if (log != null)
            {
                string fullLog = $"{log.Date} {log.Time} {log.PID,5} {log.TID,5} {log.Level} {log.Tag,-8}: {log.Message}";
                try { Clipboard.SetText(fullLog); } catch { }
            }
        }

        private void FlushTimer_Tick(object sender, EventArgs e)
        {
            FlushDiskQueues();

            if (_isPaused)
            {
                while (adbQueue.Count > 15000 && adbQueue.TryDequeue(out _)) { }
                while (kernelQueue.Count > 15000 && kernelQueue.TryDequeue(out _)) { }
                while (vhalQueue.Count > 15000 && vhalQueue.TryDequeue(out _)) { }
                while (phalQueue.Count > 15000 && phalQueue.TryDequeue(out _)) { }
                return;
            }

            bool adbUpdated = ProcessQueue(adbQueue, uiAdbList, filteredAdbList);
            bool kernelUpdated = ProcessQueue(kernelQueue, uiKernelList, filteredKernelList);
            bool vhalUpdated = ProcessQueue(vhalQueue, uiVhalList, filteredVhalList);
            bool phalUpdated = ProcessQueue(phalQueue, uiPhalList, filteredPhalList);

            if ((currentView == LogView.ADB && adbUpdated) ||
                (currentView == LogView.Kernel && kernelUpdated) ||
                (currentView == LogView.VHAL && vhalUpdated) ||
                (currentView == LogView.PHAL && phalUpdated))
            {
                RefreshGridCount();
            }
        }

        private bool ProcessQueue(ConcurrentQueue<LogLine> queue, List<LogLine> list, List<LogLine> filteredList)
        {
            if (queue.IsEmpty) return false;

            int added = 0;
            int toProcess = queue.Count;
            if (toProcess > 25000) toProcess = 25000;

            while (added < toProcess && queue.TryDequeue(out LogLine item))
            {
                list.Add(item);
                if (IsSearching && ItemMatchesFilter(item, currentSearchText))
                    filteredList.Add(item);
                added++;
            }

            if (list.Count > 150000)
            {
                int itemsToRemove = list.Count - 125000;

                if (IsSearching)
                {
                    int matchedToRemove = 0;
                    for (int i = 0; i < itemsToRemove; i++)
                    {
                        if (ItemMatchesFilter(list[i], currentSearchText))
                            matchedToRemove++;
                    }

                    if (matchedToRemove > 0 && matchedToRemove <= filteredList.Count)
                    {
                        filteredList.RemoveRange(0, matchedToRemove);
                    }
                }
                list.RemoveRange(0, itemsToRemove);
            }
            return added > 0;
        }

        private void FlushDiskQueues()
        {
            if (_isWritingToDisk) return;

            _isWritingToDisk = true;
            Task.Run(() =>
            {
                try
                {
                    lock (_diskWriteLock)
                    {
                        WriteQueueToFileSafe(adbDiskQueue, Path.Combine(_continuousLogFolder, "adb_continuous.txt"));
                        WriteQueueToFileSafe(kernelDiskQueue, Path.Combine(_continuousLogFolder, "kernel_continuous.txt"));
                        WriteQueueToFileSafe(vhalDiskQueue, Path.Combine(_continuousLogFolder, "vhal_continuous.txt"));
                        WriteQueueToFileSafe(phalDiskQueue, Path.Combine(_continuousLogFolder, "phal_continuous.txt"));
                    }
                }
                finally
                {
                    _isWritingToDisk = false;
                }
            });
        }

        // ==========================================
        // 🛠 FIX #1: MEMORY LEAK & CPU SPIN FIXED
        // ==========================================
        private void WriteQueueToFileSafe(ConcurrentQueue<string> queue, string filePath)
        {
            if (queue.IsEmpty) return;

            List<string> lines = new List<string>();
            while (queue.TryDequeue(out string line)) lines.Add(line);

            if (lines.Count > 0)
            {
                try
                {
                    File.AppendAllLines(filePath, lines);
                }
                catch (Exception)
                {
                    // If file is locked, place items back and rest the thread to prevent 100% CPU usage
                    foreach (string failedLine in lines) queue.Enqueue(failedLine);
                    System.Threading.Thread.Sleep(2000);
                }
            }
        }

        private void UpdateGrid(int newCount)
        {
            guna2GridLogs.SuspendLayout();

            if (newCount == 0)
            {
                guna2GridLogs.CurrentCell = null;
                guna2GridLogs.RowCount = 0;
                guna2GridLogs.ResumeLayout();
                return;
            }

            bool isAtBottom = true;
            if (guna2GridLogs.RowCount > 0 && guna2GridLogs.FirstDisplayedScrollingRowIndex >= 0)
            {
                int visibleRows = guna2GridLogs.DisplayedRowCount(false);
                if (guna2GridLogs.FirstDisplayedScrollingRowIndex + visibleRows < guna2GridLogs.RowCount - 5)
                    isAtBottom = false;
            }

            if (guna2GridLogs.RowCount != newCount)
            {
                if (newCount < guna2GridLogs.RowCount)
                {
                    guna2GridLogs.CurrentCell = null;
                    guna2GridLogs.RowCount = 0;
                    isAtBottom = true;
                }
                guna2GridLogs.RowCount = newCount;
            }

            if (isAtBottom && guna2GridLogs.RowCount > 0)
            {
                try
                {
                    int visibleRows = guna2GridLogs.DisplayedRowCount(false);
                    if (guna2GridLogs.RowCount > visibleRows)
                    {
                        int targetRow = guna2GridLogs.RowCount - visibleRows;
                        if (targetRow >= 0) guna2GridLogs.FirstDisplayedScrollingRowIndex = targetRow;
                    }
                }
                catch { }
            }

            guna2GridLogs.ResumeLayout(true);
        }

        private void Guna2Button3_Click(object sender, EventArgs e) { if (currentView == LogView.ADB) return; currentView = LogView.ADB; guna2GridLogs.Visible = true; RefreshGridCount(); }
        private void Guna2Button4_Click(object sender, EventArgs e) { if (currentView == LogView.Kernel) return; currentView = LogView.Kernel; guna2GridLogs.Visible = true; RefreshGridCount(); }
        private void Guna2Button7_Click(object sender, EventArgs e) { if (currentView == LogView.VHAL) return; currentView = LogView.VHAL; guna2GridLogs.Visible = true; RefreshGridCount(); }
        private void Guna2ButtonPhal_Click(object sender, EventArgs e) { if (currentView == LogView.PHAL) return; currentView = LogView.PHAL; guna2GridLogs.Visible = true; RefreshGridCount(); }

        private void StartLogs(bool clearOldLogs = true, string timeFilter = "1")
        {
            StopLogs();
            if (clearOldLogs) ClearAllBuffers();

            string tFilter = (timeFilter == "1" || string.IsNullOrWhiteSpace(timeFilter)) ? "" : $"-T {timeFilter}";

            try
            {
                adbLogProcess = CreateProcess(adbPath, $"logcat -b all -v threadtime {tFilter} *:V");
                adbLogProcess.EnableRaisingEvents = true;
                adbLogProcess.Exited += (s, e) => BroadcastSystemMessage("ERROR: Log Stream connection lost! Device may have disconnected.", Color.Red, "E");

                adbLogProcess.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null) ParseAndEnqueueLogLine(e.Data, AdbRegex, adbQueue, "SYSTEM", true);
                };
                adbLogProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) adbQueue.Enqueue(new LogLine { Level = "E", Tag = "ADB ERROR", Message = e.Data, ForeColor = Color.Red });
                };

                _ = adbLogProcess.Start();
                adbLogProcess.BeginOutputReadLine();
                adbLogProcess.BeginErrorReadLine();
            }
            catch (Exception ex) { _ = MessageBox.Show("Failed to start ADB.\n" + ex.Message); }

            try
            {
                kernelLogProcess = CreateProcess(adbPath, $"logcat -b kernel -v threadtime {tFilter} *:V");
                kernelLogProcess.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null) ParseAndEnqueueLogLine(e.Data, AdbRegex, kernelQueue, "KERNEL", false);
                };
                kernelLogProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        string msg = e.Data.Contains("Permission denied") || e.Data.Contains("Failed to open") ? "KERNEL LOGS BLOCKED BY ANDROID: Device refused Root Access." : e.Data;
                        kernelQueue.Enqueue(new LogLine { Level = "E", Tag = "KERNEL ERROR", Message = msg, ForeColor = Color.Red });
                    }
                };

                _ = kernelLogProcess.Start();
                kernelLogProcess.BeginOutputReadLine();
                kernelLogProcess.BeginErrorReadLine();
            }
            catch (Exception ex) { _ = MessageBox.Show("Failed to start Kernel logs.\n" + ex.Message); }
        }

        private Process CreateProcess(string path, string args)
        {
            return new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
        }

        private void ParseAndEnqueueLogLine(string line, Regex regex, ConcurrentQueue<LogLine> targetQueue, string defaultTag, bool isMainAdb)
        {
            try
            {
                if (line.Contains('\x1B')) line = AnsiRegex.Replace(line, "");
                if (string.IsNullOrWhiteSpace(line)) return;

                Match match = regex.Match(line);
                LogLine log = new LogLine();
                string formattedLineForDisk = "";

                if (match.Success)
                {
                    string pDate = match.Groups[1].Value;
                    string pTime = match.Groups[2].Value;

                    if (pDate == "01-01" || pDate == "12-31" || pDate == "01-02")
                    {
                        log.Date = _lastAdbDate; log.Time = _lastAdbTime;
                    }
                    else
                    {
                        log.Date = pDate; log.Time = pTime;
                        if (isMainAdb) { _lastAdbDate = pDate; _lastAdbTime = pTime; }
                    }

                    log.PID = match.Groups[3].Value;
                    log.TID = match.Groups[4].Value;
                    log.Level = match.Groups[5].Value.ToUpper();
                    log.Tag = match.Groups[6].Value.Trim();
                    log.Message = match.Groups[7].Value;

                    switch (log.Level)
                    {
                        case "F": log.ForeColor = Color.Yellow; break;
                        case "A": log.ForeColor = Color.DarkMagenta; break;
                        case "E": log.ForeColor = Color.Red; break;
                        case "W": log.ForeColor = Color.Orange; break;
                        case "I": log.ForeColor = Color.Green; break;
                        case "D": log.ForeColor = Color.Blue; break;
                        case "V": log.ForeColor = Color.MediumPurple; break;
                        default: log.ForeColor = Color.DarkGray; break;
                    }

                    formattedLineForDisk = $"{log.Date} {log.Time} {log.PID,5} {log.TID,5} {log.Level} {log.Tag,-8}: {log.Message}";
                }
                else
                {
                    log.Date = _lastAdbDate; log.Time = _lastAdbTime;
                    if (line.Contains("Permission denied") || line.Contains("Failed to open"))
                    {
                        log.Level = "E"; log.Tag = "SECURITY"; log.Message = "LOGS BLOCKED: Device refused Access."; log.ForeColor = Color.Red;
                    }
                    else { log.Level = "RAW"; log.Tag = defaultTag; log.Message = line; log.ForeColor = Color.Chocolate; }
                    formattedLineForDisk = line;
                }

                if (isMainAdb)
                {
                    adbQueue.Enqueue(log);
                    if (log.Tag != "SYSTEM" || (!log.Message.Contains("LOGS ARE") && !log.Message.Contains("ADB IS") && !log.Message.Contains("Zips")))
                        adbDiskQueue.Enqueue(formattedLineForDisk);

                    string tag = log.Tag ?? ""; string msg = log.Message ?? "";

                    if (tag.IndexOf("vhal", StringComparison.OrdinalIgnoreCase) >= 0 || tag.IndexOf("vehicle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        tag.IndexOf("carservice", StringComparison.OrdinalIgnoreCase) >= 0 || tag.IndexOf("car.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        msg.IndexOf("vhal", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("vehicle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        msg.IndexOf("carservice", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        vhalQueue.Enqueue(log); vhalDiskQueue.Enqueue(formattedLineForDisk);
                    }

                    if (tag.IndexOf("phal", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("phal", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        phalQueue.Enqueue(log); phalDiskQueue.Enqueue(formattedLineForDisk);
                    }
                }
                else
                {
                    targetQueue.Enqueue(log); kernelDiskQueue.Enqueue(formattedLineForDisk);
                }
            }
            catch { }
        }

        private void StopLogs()
        {
            var pAdb = adbLogProcess;
            var pKer = kernelLogProcess;
            adbLogProcess = null;
            kernelLogProcess = null;

            Task.Run(() =>
            {
                try { pAdb?.Kill(); } catch { } finally { try { pAdb?.Dispose(); } catch { } }
                try { pKer?.Kill(); } catch { } finally { try { pKer?.Dispose(); } catch { } }
            });
        }

        private void ResetUIToStoppedState(bool isDisconnect = false)
        {
            StopLogs();

            if (!isDisconnect)
            {
                hasStartedManually = false;
                _videoRecordingCancelledByUser = false;
            }

            _isPaused = true;
            guna2Button1.Enabled = true;
            guna2Button2.Enabled = false;

            if (form2 != null && !form2.IsDisposed)
            {
                try
                {
                    form2.testCamStop();
                    form2.StopScrcpyGracefully();
                    form2.Hide();
                }
                catch { }
            }
        }

        private async void Guna2Button1_Click(object sender, EventArgs e)
        {
            if (!isDeviceConnected)
            {
                _ = MessageBox.Show("No ADB device connected.\nPlease connect a device first.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            guna2Button1.Enabled = false;
            guna2Button2.Enabled = true;

            _isStartingUp = true;
            adb_timer?.Stop();

            try
            {
                if (!hasStartedManually)
                {
                    _videoRecordingCancelledByUser = false;
                    currentView = LogView.ADB;
                    guna2GridLogs.Visible = true;

                    ClearAllBuffers();
                    BroadcastSystemMessage("LOGS ARE STARTED. Initializing Device...", Color.DarkBlue);
                    ProcessQueue(adbQueue, uiAdbList, filteredAdbList);
                    RefreshGridCount();

                    await RunADBAsync("root");
                    await Task.Delay(1500);
                    await RunADBAsync("wait-for-device", 5000);

                    await RunADBAsync("shell setprop persist.log.tag DEBUG");
                    BroadcastSystemMessage("SYSTEM: adb shell setprop persist.log.tag DEBUG (Applied)", Color.DeepSkyBlue);

                    await RunADBAsync("logcat -G 16M");
                    await RunADBAsync("logcat -P \"\"");
                    await DeepWipeDeviceAsync();

                    await RunADBAsync("shell setprop log.tag.VehicleHal VERBOSE");
                    await RunADBAsync("shell setprop log.tag.CarService VERBOSE");
                    await RunADBAsync("shell setprop log.tag.VehicleProperty VERBOSE");

                    hasStartedManually = true;
                    _isPaused = false;
                    StartLogs(false, "1");
                }
                else
                {
                    _isPaused = false;
                    BroadcastSystemMessage("LOGS ARE RESUMED", Color.LimeGreen);
                    ProcessQueue(adbQueue, uiAdbList, filteredAdbList);
                    RefreshGridCount();
                }

                if (form2 == null || form2.IsDisposed)
                {
                    form2 = new Form2(adbPath, scrcpy_path);
                    form2.FormClosing += (s, ev) =>
                    {
                        if (ev.CloseReason == CloseReason.UserClosing)
                        {
                            ev.Cancel = true;
                            if (this.IsHandleCreated) this.BeginInvoke(new Action(() => Guna2Button2_Click(null, null)));
                        }
                    };
                }

                bool hasCamera = CheckDevice(false);
                bool camStarted = false;
                bool scrcpyStarted = false;

                if (hasCamera)
                {
                    camStarted = form2.testCamStart();
                    if (!camStarted)
                    {
                        // User clicked Cancel or X on Webcam Save Dialog -> Do NOT ask again, run Logs ONLY!
                        _videoRecordingCancelledByUser = true;
                        skipScrcpy = true;
                    }
                }
                else if (!skipScrcpy)
                {
                    scrcpyStarted = await form2.StartAndEmbedScrcpy();
                    if (!scrcpyStarted)
                    {
                        // User clicked Cancel or X on Scrcpy Save Dialog -> Do NOT ask again, run Logs ONLY!
                        _videoRecordingCancelledByUser = true;
                        skipScrcpy = true;
                    }
                }

                if (camStarted || scrcpyStarted)
                {
                    form2.ArrangeLayout();
                    form2.Show();
                }
                else
                {
                    BroadcastSystemMessage("Video recording skipped/cancelled. Logs are running...", Color.DeepPink);
                }
            }
            catch (Exception ex)
            {
                ResetUIToStoppedState();
                MessageBox.Show("Failed to start: " + ex.Message);
            }
            finally
            {
                _isStartingUp = false;
                adb_timer?.Start();
            }
        }

        private void Guna2Button2_Click(object sender, EventArgs e)
        {
            if (!hasStartedManually || _isPaused) return;

            BroadcastSystemMessage("LOGS ARE STOPPED", Color.DeepPink);
            ResetUIToStoppedState();

            ProcessQueue(adbQueue, uiAdbList, filteredAdbList);
            ProcessQueue(kernelQueue, uiKernelList, filteredKernelList);
            RefreshGridCount();

            AutoSaveAllLogsToZip();
        }

        // =========================================================================
        // 🛠 FIX #2: COMPLETE, UNCORRUPTED BUGREPORT GENERATION & EXTRACTION FIX
        // =========================================================================
        public async Task<string> RunAdbBugReportAsync(string savePath)
        {
            string result = "Success";

            try { if (File.Exists(savePath)) File.Delete(savePath); } catch { }

            using (Form progressForm = new Form())
            {
                progressForm.Text = "Generating Android Bug Report";
                progressForm.Size = new Size(420, 160);
                progressForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                progressForm.StartPosition = FormStartPosition.CenterParent;
                progressForm.ControlBox = false;
                progressForm.TopMost = true;

                Label lbl = new Label()
                {
                    Text = "Generating Bug Report... Please wait (2 to 5 mins).\nDo NOT disconnect the device.\nStarting dumpstate...",
                    Location = new Point(20, 15),
                    AutoSize = true
                };
                ProgressBar pBar = new ProgressBar()
                {
                    Style = ProgressBarStyle.Marquee,
                    Location = new Point(20, 80),
                    Size = new Size(365, 25)
                };
                progressForm.Controls.Add(lbl);
                progressForm.Controls.Add(pBar);

                progressForm.Shown += async (s, e) =>
                {
                    await Task.Run(() =>
                    {
                        string tempBugDir = Path.Combine(Path.GetTempPath(), "AdbBugReport_" + Guid.NewGuid().ToString("N"));
                        try
                        {
                            Directory.CreateDirectory(tempBugDir);

                            ProcessStartInfo psi = new ProcessStartInfo
                            {
                                FileName = adbPath,
                                Arguments = $"bugreport \"{tempBugDir}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            };

                            string lastError = "";

                            using (Process process = new Process { StartInfo = psi })
                            {
                                DataReceivedEventHandler outputHandler = (senderObj, args) =>
                                {
                                    if (!string.IsNullOrWhiteSpace(args.Data))
                                    {
                                        string line = args.Data.Trim();
                                        if (progressForm.IsHandleCreated)
                                        {
                                            try
                                            {
                                                progressForm.BeginInvoke(new Action(() =>
                                                {
                                                    lbl.Text = "Generating Bug Report... Please wait.\n" +
                                                               (line.Length > 55 ? line.Substring(0, 55) + "..." : line);
                                                }));
                                            }
                                            catch { }
                                        }
                                    }
                                };

                                process.OutputDataReceived += outputHandler;
                                process.ErrorDataReceived += (senderObj, args) =>
                                {
                                    if (!string.IsNullOrWhiteSpace(args.Data))
                                    {
                                        lastError = args.Data.Trim();
                                        outputHandler(senderObj, args);
                                    }
                                };

                                process.Start();
                                process.BeginOutputReadLine();
                                process.BeginErrorReadLine();

                                // Allow up to 10 minutes (600,000 ms)
                                if (!process.WaitForExit(600000))
                                {
                                    try { process.Kill(); } catch { }
                                    result = "Error: Bug report timed out after 10 minutes. Device might be frozen.";
                                }
                                else
                                {
                                    // Flush all remaining async output events
                                    process.WaitForExit();
                                }
                            }

                            // Allow OS 1 second to completely release file locks
                            System.Threading.Thread.Sleep(1000);

                            if (result == "Success")
                            {
                                string generatedZip = Directory.GetFiles(tempBugDir, "*.zip").FirstOrDefault();
                                if (string.IsNullOrEmpty(generatedZip))
                                {
                                    generatedZip = Directory.GetFiles(tempBugDir).FirstOrDefault();
                                }

                                if (!string.IsNullOrEmpty(generatedZip) && new FileInfo(generatedZip).Length > 0)
                                {
                                    if (progressForm.IsHandleCreated)
                                    {
                                        try
                                        {
                                            progressForm.BeginInvoke(new Action(() =>
                                            {
                                                lbl.Text = "Finalizing ZIP archive for Windows extraction...\nPlease wait...";
                                            }));
                                        }
                                        catch { }
                                    }

                                    // Repackage cleanly without Linux symlink attributes so 7-Zip / Windows Extract All never gets stuck!
                                    SanitizeBugReportZipForWindows(generatedZip, savePath);
                                }
                                else
                                {
                                    result = "Error: ADB finished but did not produce a valid zip file. " + lastError;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            result = "Error: " + ex.Message;
                        }
                        finally
                        {
                            try { if (Directory.Exists(tempBugDir)) Directory.Delete(tempBugDir, true); } catch { }
                        }
                    });

                    if (progressForm.IsHandleCreated)
                        progressForm.Close();
                };

                progressForm.ShowDialog(this);
            }
            return result;
        }

        /// <summary>
        /// Copies all standard files from the Android bugreport zip into a clean Windows-compatible zip,
        /// stripping Unix symlink external attributes that cause 7-Zip to halt with "Cannot create symbolic link".
        /// </summary>
        private void SanitizeBugReportZipForWindows(string rawAndroidZipPath, string finalWindowsZipPath)
        {
            try
            {
                if (File.Exists(finalWindowsZipPath)) File.Delete(finalWindowsZipPath);

                using (FileStream sourceStream = new FileStream(rawAndroidZipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (ZipArchive sourceArchive = new ZipArchive(sourceStream, ZipArchiveMode.Read))
                using (FileStream destStream = new FileStream(finalWindowsZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (ZipArchive destArchive = new ZipArchive(destStream, ZipArchiveMode.Create))
                {
                    HashSet<string> addedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (ZipArchiveEntry entry in sourceArchive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;

                        // Check Unix mode in upper 16 bits of ExternalAttributes: 0xA000 is S_IFLNK (Linux Symbolic Link)
                        int unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
                        if (unixFileType == 0xA000)
                        {
                            // Skip Linux symbolic link entries that cause 7-Zip extraction errors on Windows
                            continue;
                        }

                        string cleanFullName = entry.FullName.Replace('\\', '/').TrimStart('/');
                        if (string.IsNullOrEmpty(cleanFullName) || !addedEntries.Add(cleanFullName))
                            continue;

                        ZipArchiveEntry newEntry = destArchive.CreateEntry(cleanFullName, CompressionLevel.Fastest);
                        newEntry.LastWriteTime = entry.LastWriteTime;

                        using (Stream inStream = entry.Open())
                        using (Stream outStream = newEntry.Open())
                        {
                            inStream.CopyTo(outStream);
                        }
                    }
                }
            }
            catch
            {
                // Fallback: if anything fails during sanitization, move the raw zip directly
                if (File.Exists(finalWindowsZipPath)) File.Delete(finalWindowsZipPath);
                File.Move(rawAndroidZipPath, finalWindowsZipPath);
            }
        }

        public async Task<string> RunADBAsync(string command, int timeoutMs = 3000)
        {
            try
            {
                using (Process process = CreateProcess(adbPath, command))
                {
                    _ = process.Start();
                    Task<string> readErr = process.StandardError.ReadToEndAsync();
                    Task<string> readOut = process.StandardOutput.ReadToEndAsync();
                    Task exitTask = Task.Run(() => process.WaitForExit());
                    Task timeoutTask = Task.Delay(timeoutMs);

                    if (await Task.WhenAny(exitTask, timeoutTask) == timeoutTask)
                    {
                        try { process.Kill(); } catch { }
                        return "";
                    }
                    _ = await Task.WhenAll(readOut, readErr);
                    return (await readOut + " " + await readErr).Trim();
                }
            }
            catch { return ""; }
        }

        // =========================================================================
        // 🛠 FIX #3: WAIT FOR ANDROID DISPLAY/BOOT READY SO SCRCPY NEVER FAILS
        // =========================================================================
        private async Task<bool> WaitForAndroidDisplayReadyAsync(int maxWaitSeconds = 35)
        {
            for (int i = 0; i < maxWaitSeconds; i++)
            {
                if (!isDeviceConnected || _isPaused) return false;

                string bootCompleted = await RunADBAsync("shell getprop sys.boot_completed", 2000);
                if (bootCompleted != null && bootCompleted.Trim() == "1")
                {
                    return true;
                }

                // Also check if WindowManager/SurfaceFlinger is ready for scrcpy even before launcher finishes
                string wmCheck = await RunADBAsync("shell service check window", 2000);
                if (!string.IsNullOrEmpty(wmCheck) && wmCheck.Contains("found") && !wmCheck.Contains("not found") && i >= 6)
                {
                    return true;
                }

                await Task.Delay(1000);
            }
            return true;
        }

        private async Task LoadDeviceIdAsync()
        {
            if (_isCheckingDevice || _isStartingUp) return;

            _isCheckingDevice = true;

            try
            {
                string output = await RunADBAsync("devices", 5000);
                if (string.IsNullOrEmpty(output)) return;

                bool deviceNowConnected = output.Contains("\tdevice");

                if (deviceNowConnected)
                {
                    if (!isDeviceConnected)
                    {
                        isDeviceConnected = true;
                        string deviceLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(l => l.EndsWith("\tdevice"));
                        Guna2HtmlLabel3.Text = deviceLine != null ? deviceLine.Split('\t')[0] : "Connected";
                        Guna2HtmlLabel3.ForeColor = Color.White;

                        BroadcastSystemMessage("ADB IS CONNECTED", Color.LimeGreen);
                        if (currentView == LogView.None) { currentView = LogView.ADB; guna2GridLogs.Visible = true; }
                        RefreshGridCount();

                        if (hasStartedManually)
                        {
                            _isPaused = false;
                            BroadcastSystemMessage("ADB RECONNECTED. Pulling Boot Logs & Resuming Scrcpy...", Color.LimeGreen);
                            ProcessQueue(adbQueue, uiAdbList, filteredAdbList);
                            RefreshGridCount();

                            await RunADBAsync("root");
                            await Task.Delay(1500);
                            await RunADBAsync("wait-for-device", 5000);

                            await RunADBAsync("shell setprop persist.log.tag DEBUG");
                            BroadcastSystemMessage("SYSTEM: Re-applying persist.log.tag DEBUG after reconnection", Color.DeepSkyBlue);

                            await RunADBAsync("shell setprop log.tag.VehicleHal VERBOSE");
                            await RunADBAsync("shell setprop log.tag.CarService VERBOSE");
                            await RunADBAsync("shell setprop log.tag.VehicleProperty VERBOSE");

                            // Start logs immediately so boot logs are never missed
                            StartLogs(false, "1");

                            guna2Button1.Enabled = false;
                            guna2Button2.Enabled = true;

                            // Ensure form2 exists if needed
                            if (form2 == null || form2.IsDisposed)
                            {
                                form2 = new Form2(adbPath, scrcpy_path);
                                form2.FormClosing += (s, ev) =>
                                {
                                    if (ev.CloseReason == CloseReason.UserClosing)
                                    {
                                        ev.Cancel = true;
                                        if (this.IsHandleCreated) this.BeginInvoke(new Action(() => Guna2Button2_Click(null, null)));
                                    }
                                };
                            }

                            // Only auto-reconnect video if user did NOT cancel video recording
                            if (form2 != null && !form2.IsDisposed && !_videoRecordingCancelledByUser)
                            {
                                bool hasCamera = CheckDevice(true);
                                bool camSuccess = false;

                                if (hasCamera)
                                {
                                    camSuccess = form2.testCamStart(true);
                                    if (camSuccess)
                                    {
                                        form2.ArrangeLayout();
                                        form2.Show();
                                    }
                                }
                                else if (!skipScrcpy)
                                {
                                    // Run Scrcpy auto-reconnect in background after waiting for Android Display Service to finish booting
                                    _ = Task.Run(async () =>
                                    {
                                        BroadcastSystemMessage("Waiting for Android Display Service to finish booting for ScreenCopy...", Color.DeepSkyBlue);
                                        await WaitForAndroidDisplayReadyAsync(35);

                                        for (int attempt = 1; attempt <= 5; attempt++)
                                        {
                                            if (!isDeviceConnected || _isPaused || !hasStartedManually || _videoRecordingCancelledByUser)
                                                break;

                                            bool scrcpySuccess = false;
                                            if (this.IsHandleCreated)
                                            {
                                                TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
                                                this.BeginInvoke(new Action(async () =>
                                                {
                                                    try
                                                    {
                                                        bool started = await form2.StartAndEmbedScrcpy(true);
                                                        if (started)
                                                        {
                                                            form2.ArrangeLayout();
                                                            form2.Show();
                                                        }
                                                        tcs.TrySetResult(started);
                                                    }
                                                    catch
                                                    {
                                                        tcs.TrySetResult(false);
                                                    }
                                                }));
                                                scrcpySuccess = await tcs.Task;
                                            }

                                            if (scrcpySuccess)
                                            {
                                                BroadcastSystemMessage("ScreenCopy auto-recording resumed successfully!", Color.LimeGreen);
                                                break;
                                            }

                                            await Task.Delay(3000);
                                        }
                                    });
                                }
                            }
                        }
                    }
                }
                else if (isDeviceConnected)
                {
                    isDeviceConnected = false;
                    Guna2HtmlLabel3.Text = "No device connected";
                    Guna2HtmlLabel3.ForeColor = Color.White;

                    BroadcastSystemMessage("ADB IS DISCONNECTED / REBOOTING...", Color.Red, "E");
                    RefreshGridCount();

                    ResetUIToStoppedState(true);
                }
            }
            finally { _isCheckingDevice = false; }
        }

        private async void Timer1_Tick(object sender, EventArgs e) { adb_timer?.Stop(); await LoadDeviceIdAsync(); adb_timer?.Start(); }

        //ZOMBIE ADB/SCRCPY PROCESSES

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            this.Hide();
            StopLogs();
            flushTimer?.Stop();
            adb_timer?.Stop();

            if (form2 != null && !form2.IsDisposed)
            {
                try
                {
                    form2.testCamStop();
                    form2.StopScrcpyGracefully();

                    // Allow the threads 500ms to cleanly close tools before environment nuke
                    System.Threading.Thread.Sleep(500);
                }
                catch { }
            }

            base.OnFormClosing(e);
            Environment.Exit(0);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.S))
            {
                if (hasStartedManually || _isPaused || adbDiskQueue.Count > 0) AutoSaveAllLogsToZip();
                else MessageBox.Show("No active logs to save.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void Form1_Load(object sender, EventArgs e) { }
        private void Guna2HtmlLabel3_Click(object sender, EventArgs e) { }

        private async void Guna2Button5_Click(object sender, EventArgs e)
        {
            guna2Button5.Enabled = false;

            if (hasStartedManually && isDeviceConnected)
            {
                StopLogs();
                ClearAllBuffers();
                BroadcastSystemMessage("--- DEEP WIPING DEVICE LOGS & CRASHES ---", Color.Orange);
                RefreshGridCount();
                await DeepWipeDeviceAsync();
                BroadcastSystemMessage("--- DEVICE CLEARED. STARTING FRESH ---", Color.LimeGreen);
                ProcessQueue(adbQueue, uiAdbList, filteredAdbList);
                RefreshGridCount();
                StartLogs(false, "1");
            }
            else
            {
                ClearAllBuffers();
                BroadcastSystemMessage("--- LOGS CLEARED ON UI ---", Color.HotPink);
                ProcessQueue(adbQueue, uiAdbList, filteredAdbList);
                RefreshGridCount();
            }
            guna2Button5.Enabled = true;
        }

        private void guna2Button6_Click(object sender, EventArgs e) { Form3 form3 = new Form3(); form3.Show(); }
        private void button1_Click(object sender, EventArgs e) { }

        private void guna2Button8_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Title = "Select scrcpy.exe (Version Mismatch Fix)";
                openFileDialog.Filter = "Executable files (*.exe)|*.exe";

                if (openFileDialog.ShowDialog(this) == DialogResult.OK)
                {
                    scrcpy_path = openFileDialog.FileName;
                    string autoAdbPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(scrcpy_path), "adb.exe");
                    if (System.IO.File.Exists(autoAdbPath))
                    {
                        adbPath = autoAdbPath;
                        if (form2 != null && !form2.IsDisposed)
                        {
                            try { form2.Dispose(); } catch { }
                            form2 = null;
                        }
                        MessageBox.Show("Success!");
                    }
                    else
                    {
                        MessageBox.Show("adb.exe not found alongside scrcpy.exe");
                    }
                }
            }
        }

        private void label1_Click(object sender, EventArgs e) { }
        private void guna2Button1_Click_1(object sender, EventArgs e) { }
        private void guna2TextBox1_TextChanged(object sender, EventArgs e) { }

        private async void bugBtn_Click(object sender, EventArgs e)
        {
            if (!isDeviceConnected)
            {
                MessageBox.Show("No device is connected.");
                return;
            }

            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Title = "Save Android Bug Report";
                sfd.Filter = "Zip File|*.zip";
                sfd.FileName = "BugReport_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip";

                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    // Pause device polling timer and active logcat streams so ADB USB transfer is 100% uninterrupted
                    adb_timer?.Stop();
                    StopLogs();

                    try
                    {
                        BroadcastSystemMessage("Deleting old bug reports from phone storage...", Color.Orange);
                        RefreshGridCount();

                        await RunADBAsync("shell \"rm -rf /bugreports/* && rm -rf /data/user_de/0/com.android.shell/files/bugreports/*\"", 5000);

                        BroadcastSystemMessage("Starting fresh Bug Report generation... Please wait.", Color.DodgerBlue);
                        RefreshGridCount();

                        string adbResponse = await RunAdbBugReportAsync(sfd.FileName);

                        if (adbResponse == "Success" && File.Exists(sfd.FileName))
                        {
                            BroadcastSystemMessage("Bug report saved! Wiping ALL PC & Phone data for your next test...", Color.Orange);
                            RefreshGridCount();

                            await DeepWipeDeviceAsync();
                            ClearAllBuffers();
                            RefreshGridCount();

                            if (hasStartedManually && isDeviceConnected)
                            {
                                StartLogs(false, "1");
                                BroadcastSystemMessage("Logs restarted completely fresh.", Color.LimeGreen);
                            }

                            MessageBox.Show("Bug report saved successfully to:\n" + sfd.FileName, "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            BroadcastSystemMessage("Bug Report Error: " + adbResponse, Color.Red, "E");
                            RefreshGridCount();

                            if (hasStartedManually && isDeviceConnected)
                            {
                                StartLogs(false, "1");
                            }

                            MessageBox.Show("Bug report failed:\n" + adbResponse, "Bug Report Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                    finally
                    {
                        adb_timer?.Start();
                    }
                }
            }
        }

        // ADB SERVER OVERLOAD PROTECTION

        private async Task DeepWipeDeviceAsync()
        {
            try
            {
                // 1. Clear core logs independently
                await RunADBAsync("shell setprop persist.log.tag DEBUG"); // Reinforce debug logging
                await RunADBAsync("logcat -b all -c");
                await RunADBAsync("logcat -b kernel -c");

                // 2. Chain shell clear commands into ONE session (protects ADB Daemon from crashing)
                string wipeCommand = "shell \"dmesg -c && rm -rf /data/tombstones/* && rm -rf /data/anr/* && rm -rf /data/system/dropbox/* && rm -rf /bugreports/* && rm -rf /data/user_de/0/com.android.shell/files/bugreports/*\"";
                await RunADBAsync(wipeCommand);
            }
            catch { }
        }

        private async void AutoSaveAllLogsToZip()
        {
            flushTimer?.Stop();

            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "Select a folder to save the compressed logs (.zip)";
                if (fbd.ShowDialog(this) == DialogResult.OK)
                {
                    string targetPath = fbd.SelectedPath;
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                    BroadcastSystemMessage("Background Saving and Compressing Zips...", Color.DodgerBlue);
                    RefreshGridCount();
                    guna2Button1.Enabled = false;

                    await Task.Run(() =>
                    {
                        try
                        {
                            lock (_diskWriteLock)
                            {
                                WriteQueueToFileSafe(adbDiskQueue, Path.Combine(_continuousLogFolder, "adb_continuous.txt"));
                                WriteQueueToFileSafe(kernelDiskQueue, Path.Combine(_continuousLogFolder, "kernel_continuous.txt"));
                                WriteQueueToFileSafe(vhalDiskQueue, Path.Combine(_continuousLogFolder, "vhal_continuous.txt"));
                                WriteQueueToFileSafe(phalDiskQueue, Path.Combine(_continuousLogFolder, "phal_continuous.txt"));
                            }

                            ZipContinuousFile("adb_continuous.txt", Path.Combine(targetPath, $"debug_logs_{timestamp}.zip"));
                            ZipContinuousFile("kernel_continuous.txt", Path.Combine(targetPath, $"kernel_logs_{timestamp}.zip"));
                            ZipContinuousFile("vhal_continuous.txt", Path.Combine(targetPath, $"vhal_logs_{timestamp}.zip"));
                            ZipContinuousFile("phal_continuous.txt", Path.Combine(targetPath, $"phal_logs_{timestamp}.zip"));

                            this.Invoke(new Action(() =>
                            {
                                BroadcastSystemMessage("Zips successfully saved in the background!", Color.LimeGreen);
                                MessageBox.Show($"Logs successfully saved to:\n\n{targetPath}", "Save Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }));
                        }
                        catch (Exception ex)
                        {
                            this.Invoke(new Action(() => MessageBox.Show("Error creating zip files: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)));
                        }
                    });

                    guna2Button1.Enabled = true;
                }
            }
            flushTimer?.Start();
        }

        private void ZipContinuousFile(string sourceFileName, string destZipPath)
        {
            string sourcePath = Path.Combine(_continuousLogFolder, sourceFileName);
            if (!File.Exists(sourcePath)) return;

            using (FileStream zipToOpen = new FileStream(destZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (ZipArchive archive = new ZipArchive(zipToOpen, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = archive.CreateEntry(sourceFileName.Replace("_continuous", ""), CompressionLevel.Optimal);
                using (Stream entryStream = entry.Open())
                using (FileStream sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    sourceStream.CopyTo(entryStream);
                }
            }
        }

        private void button1_Click_1(object sender, EventArgs e) { }
        private void guna2GridLogs_CellContentClick(object sender, DataGridViewCellEventArgs e) { }
        private void roundedThemeButton1_Click(object sender, EventArgs e) { }
        private void button1_Click_2(object sender, EventArgs e) { }

        public class LogLine
        {
            public string Date;
            public string Time;
            public string Level;
            public string PID;
            public string TID;
            public string Tag;
            public string Message;
            public Color ForeColor;
        }
    }
}