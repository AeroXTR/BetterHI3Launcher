﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ProgressItem = System.Collections.Generic.KeyValuePair<long, float>;

namespace BetterHI3Launcher
{
    public class BpUtility
    {
        public static void StartProcess(string proccess, string arguments, string workingDir, bool useShellExec)
        {
            var startInfo = new ProcessStartInfo(proccess, arguments);
            startInfo.WorkingDirectory = workingDir;
            startInfo.UseShellExecute = useShellExec;
            Process.Start(startInfo);
        }

        // https://stackoverflow.com/a/10520086/7570821
        public static string CalculateMD5(string filename)
        {
            using(var md5 = MD5.Create())
            {
                using(var stream = File.OpenRead(filename))
                {
                    return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", string.Empty);
                }
            }
        }

        // https://stackoverflow.com/a/49535675/7570821
        public static string ToBytesCount(long bytes)
        {
            int unit = 1024;
            string unitStr = MainWindow.textStrings["binary_prefix_byte"];
            if(bytes < unit) return string.Format("{0} {1}", bytes, unitStr);
            else unitStr = unitStr.ToUpper();
            int exp = (int)(Math.Log(bytes) / Math.Log(unit));
            return string.Format("{0:##.##} {1}{2}", bytes / Math.Pow(unit, exp), MainWindow.textStrings["binary_prefixes"][exp - 1], unitStr);
        }

        public static bool IsFileLocked(FileInfo file)
        {
            try
            {
                using(FileStream stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    stream.Close();
                }
            }
            catch(IOException)
            {
                return true;
            }
            return false;
        }

        public static HttpWebRequest CreateWebRequest(string url, string method = "GET", int timeout = 30000)
        {
            var webRequest = (HttpWebRequest)WebRequest.Create(url);
            webRequest.Method = method;
            webRequest.UserAgent = MainWindow.UserAgent;
            webRequest.Headers.Add("Accept-Language", MainWindow.LauncherLanguage);
            webRequest.Timeout = timeout;
            return webRequest;
        }
    }

    // https://stackoverflow.com/a/42725580/7570821
    public class DownloadProgressTracker
    {
        private long _totalFileSize;
        private readonly int _sampleSize;
        private readonly TimeSpan _valueDelay;

        private DateTime _lastUpdateCalculated;
        private long _previousProgress;

        private double _cachedSpeed;

        private Queue<Tuple<DateTime, long>> _changes = new Queue<Tuple<DateTime, long>>();

        public DownloadProgressTracker(int sampleSize, TimeSpan valueDelay)
        {
            _lastUpdateCalculated = DateTime.Now;
            _sampleSize = sampleSize;
            _valueDelay = valueDelay;
        }

        public void NewFile()
        {
            _previousProgress = 0;
        }

        public void SetProgress(long bytesReceived, long totalBytesToReceive)
        {
            _totalFileSize = totalBytesToReceive;

            long diff = bytesReceived - _previousProgress;
            if(diff <= 0)
                return;

            _previousProgress = bytesReceived;

            _changes.Enqueue(new Tuple<DateTime, long>(DateTime.Now, diff));
            while(_changes.Count > _sampleSize)
                _changes.Dequeue();
        }

        public double GetProgress()
        {
            return _previousProgress / (double)_totalFileSize;
        }

        public string GetProgressString()
        {
            return string.Format("{0:P0}", GetProgress());
        }

        public string GetBytesPerSecondString()
        {
            double speed = GetBytesPerSecond();
            string[] prefix;
            switch(MainWindow.OSLanguage)
            {
                case "ru-RU":
                case "uk-UA":
                case "be-BY":
                    prefix = new[]{"", "К", "М", "Г"};
                    break;
                default:
                    prefix = new[]{"", "K", "M", "G"};
                    break;
            }

            int index = 0;
            while(speed > 1024 && index < prefix.Length - 1)
            {
                speed /= 1024;
                index++;
            }

            int intLen = ((int)speed).ToString().Length;
            int decimals = 3 - intLen;
            if(decimals < 0)
                decimals = 0;

            string format = string.Format("{{0:F{0}}}", decimals) + " {1}" + MainWindow.textStrings["bytes_per_second"];

            return string.Format(format, speed, prefix[index]);
        }

        public double GetBytesPerSecond()
        {
            if(DateTime.Now >= _lastUpdateCalculated + _valueDelay)
            {
                _lastUpdateCalculated = DateTime.Now;
                _cachedSpeed = GetRateInternal();
            }

            return _cachedSpeed;
        }

        private double GetRateInternal()
        {
            if(_changes.Count == 0)
                return 0;

            TimeSpan timespan = _changes.Last().Item1 - _changes.First().Item1;
            long bytes = _changes.Sum(t => t.Item2);

            double rate = bytes / timespan.TotalSeconds;

            if(double.IsInfinity(rate) || double.IsNaN(rate))
                return 0;

            return rate;
        }
    }

    // https://stackoverflow.com/a/62039306/7570821
    public class DownloadPauseable
    {
        private volatile bool _allowedToRun;
        private readonly string _sourceUrl;
        private readonly string _destination;
        private readonly int _chunkSize;
        private readonly IProgress<double> _progress;
        private readonly Lazy<long> _contentLength;

        public long BytesWritten {get; private set;}
        public long ContentLength => _contentLength.Value;

        public bool Done => ContentLength == BytesWritten;

        public DownloadPauseable(string source, string destination, int chunkSizeInBytes = 8192, IProgress<double> progress = null)
        {
            if(string.IsNullOrEmpty(source) || string.IsNullOrEmpty(destination))
                throw new ArgumentNullException();

            _allowedToRun = true;
            _sourceUrl = source;
            _destination = destination;
            _chunkSize = chunkSizeInBytes;
            _contentLength = new Lazy<long>(GetContentLength);
            _progress = progress;

            if(!File.Exists(destination))
                BytesWritten = 0;
            else
            {
                try
                {
                    BytesWritten = new FileInfo(destination).Length;
                }
                catch
                {
                    BytesWritten = 0;
                }
            }
        }

        private long GetContentLength()
        {
            var request = (HttpWebRequest)WebRequest.Create(_sourceUrl);
            request.Method = "HEAD";

            using(var response = request.GetResponse())
                return response.ContentLength;
        }

        private async Task Start(long range)
        {
            if(!_allowedToRun)
                throw new InvalidOperationException();

            if(Done)
                //file has been found in folder destination and is already fully downloaded 
                return;

            var request = (HttpWebRequest)WebRequest.Create(_sourceUrl);
            request.UserAgent = MainWindow.UserAgent;
            request.AddRange(range);

            using(var response = await request.GetResponseAsync())
            {
                using(var responseStream = response.GetResponseStream())
                {
                    using(var fs = new FileStream(_destination, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        while(_allowedToRun)
                        {
                            var buffer = new byte[_chunkSize];
                            var bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);

                            if(bytesRead == 0) break;

                            await fs.WriteAsync(buffer, 0, bytesRead);
                            BytesWritten += bytesRead;
                            _progress?.Report((double)BytesWritten / ContentLength);
                        }

                        await fs.FlushAsync();
                    }
                }
            }
        }

        public Task Start()
        {
            _allowedToRun = true;
            return Start(BytesWritten);
        }

        public void Pause()
        {
            _allowedToRun = false;
        }
    }

    public class BpWebClient : WebClient
    {
        public int Timeout {get; set;}

        public BpWebClient()
        {
            Encoding = Encoding.UTF8;
            Timeout = 10000;
            Headers.Set(HttpRequestHeader.UserAgent, MainWindow.UserAgent);
            Headers.Set(HttpRequestHeader.AcceptLanguage, MainWindow.LauncherLanguage);
        }

        protected override WebRequest GetWebRequest(Uri address)
        {
            var webRequest = base.GetWebRequest(address);
            webRequest.Timeout = Timeout;
            return webRequest;
        }
    }

    // https://github.com/scottrippey/Progression/blob/master/Progression/Extras/ETACalculator.cs
    public interface IETACalculator
    {
        /// <summary> Clears all collected data.
        /// </summary>
        void Reset();

        /// <summary> Updates the current progress.
        /// </summary>
        /// <param name="progress">The current level of completion.
        /// Must be between 0.0 and 1.0 (inclusively).</param>
        void Update(float progress);

        /// <summary> Returns True when there is enough data to calculate the ETA.
        /// Returns False if the ETA is still calculating.
        /// </summary>
        bool ETAIsAvailable {get;}

        /// <summary> Calculates the Estimated Time of Arrival (Completion)
        /// </summary>
        DateTime ETA {get;}

        /// <summary> Calculates the Estimated Time Remaining.
        /// </summary>
        TimeSpan ETR {get;}
    }

    /// <summary> Calculates the "Estimated Time of Arrival"
    /// (or more accurately, "Estimated Time of Completion"),
    /// based on a "rolling average" of progress over time.
    /// </summary>
    public class ETACalculator : IETACalculator
    {
        /// <summary>
        /// </summary>
        /// <param name="minimumData">
        /// The minimum number of data points required before ETA can be calculated.
        /// </param>
        /// <param name="maximumDuration">
        /// Determines how many seconds of data will be used to calculate the ETA.
        /// </param>
        public ETACalculator(int minimumData, double maximumDuration)
        {
            this.minimumData = minimumData;
            this.maximumTicks = (long)(maximumDuration * Stopwatch.Frequency);
            this.queue = new Queue<ProgressItem>(minimumData * 2);
            this.timer = Stopwatch.StartNew();
        }

        private int minimumData;
        private long maximumTicks;
        private readonly Stopwatch timer;
        private readonly Queue<ProgressItem> queue;

        private ProgressItem current;
        private ProgressItem oldest;

        public void Reset()
        {
            queue.Clear();

            timer.Reset();
            timer.Start();
        }

        private void ClearExpired()
        {
            var expired = timer.ElapsedTicks - this.maximumTicks;
            while(queue.Count > this.minimumData && queue.Peek().Key < expired)
            {
                this.oldest = queue.Dequeue();
            }
        }

        /// <summary> Adds the current progress to the calculation of ETA.
        /// </summary>
        /// <param name="progress">The current level of completion.
        /// Must be between 0.0 and 1.0 (inclusively).</param>
        public void Update(float progress)
        {
            // If progress hasn't changed, ignore:
            if(this.current.Value == progress)
            {
                return;
            }

            // Clear space for this item:
            ClearExpired();

            // Queue this item:
            long currentTicks = timer.ElapsedTicks;
            this.current = new ProgressItem(currentTicks, progress);
            this.queue.Enqueue(this.current);

            // See if its the first item:
            if(this.queue.Count == 1)
            {
                this.oldest = this.current;
            }
        }

        /// <summary> Calculates the Estimated Time Remaining
        /// </summary>
        public TimeSpan ETR
        {
            get
            {
                // Create local copies of the oldest & current,
                // so that another thread can update them without locking:
                var oldest = this.oldest;
                var current = this.current;

                // Make sure we have enough items:
                if(queue.Count < this.minimumData || oldest.Value == current.Value)
                {
                    return TimeSpan.MaxValue;
                }

                // Calculate the estimated finished time:
                double finishedInTicks = (1.0d - current.Value) * (current.Key - oldest.Key) / (current.Value - oldest.Value);

                return TimeSpan.FromSeconds(finishedInTicks / Stopwatch.Frequency);
            }
        }

        /// <summary> Calculates the Estimated Time of Arrival (Completion)
        /// </summary>
        public DateTime ETA
        {
            get
            {
                return DateTime.Now.Add(ETR);
            }
        }

        /// <summary> Returns True when there is enough data to calculate the ETA.
        /// Returns False if the ETA is still calculating.
        /// </summary>
        public bool ETAIsAvailable
        {
            get
            {
                // Make sure we have enough items:
                return (queue.Count >= this.minimumData && oldest.Value != current.Value);
            }
        }
    }

    #if DEBUG
    // https://stackoverflow.com/a/48864902/7570821
    static class WinConsole
    {
        static public void Initialize(bool alwaysCreateNewConsole = true)
        {
            bool consoleAttached = true;
            if (alwaysCreateNewConsole
                || (AttachConsole(ATTACH_PARRENT) == 0
                && Marshal.GetLastWin32Error() != ERROR_ACCESS_DENIED))
            {
                consoleAttached = AllocConsole() != 0;
            }

            if (consoleAttached)
            {
                InitializeOutStream();
                InitializeInStream();
            }
        }

        private static void InitializeOutStream()
        {
            var fs = CreateFileStream("CONOUT$", GENERIC_WRITE, FILE_SHARE_WRITE, FileAccess.Write);
            if (fs != null)
            {
                var writer = new StreamWriter(fs) {AutoFlush = true};
                Console.SetOut(writer);
                Console.SetError(writer);
            }
        }

        private static void InitializeInStream()
        {
            var fs = CreateFileStream("CONIN$", GENERIC_READ, FILE_SHARE_READ, FileAccess.Read);
            if (fs != null)
            {
                Console.SetIn(new StreamReader(fs));
            }
        }

        private static FileStream CreateFileStream(string name, uint win32DesiredAccess, uint win32ShareMode, FileAccess dotNetFileAccess)
        {
            var file = new Microsoft.Win32.SafeHandles.SafeFileHandle(CreateFileW(name, win32DesiredAccess, win32ShareMode, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero), true);
            if (!file.IsInvalid)
            {
                var fs = new FileStream(file, dotNetFileAccess);
                return fs;
            }
            return null;
        }

        #region Win API Functions and Constants
        [DllImport("kernel32.dll",
            EntryPoint = "AllocConsole",
            SetLastError = true,
            CharSet = CharSet.Auto,
            CallingConvention = CallingConvention.StdCall)]
        private static extern int AllocConsole();

        [DllImport("kernel32.dll",
            EntryPoint = "AttachConsole",
            SetLastError = true,
            CharSet = CharSet.Auto,
            CallingConvention = CallingConvention.StdCall)]
        private static extern UInt32 AttachConsole(UInt32 dwProcessId);

        [DllImport("kernel32.dll",
            EntryPoint = "CreateFileW",
            SetLastError = true,
            CharSet = CharSet.Auto,
            CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr CreateFileW
        (
                string lpFileName,
                UInt32 dwDesiredAccess,
                UInt32 dwShareMode,
                IntPtr lpSecurityAttributes,
                UInt32 dwCreationDisposition,
                UInt32 dwFlagsAndAttributes,
                IntPtr hTemplateFile
        );

        private const UInt32 GENERIC_WRITE = 0x40000000;
        private const UInt32 GENERIC_READ = 0x80000000;
        private const UInt32 FILE_SHARE_READ = 0x00000001;
        private const UInt32 FILE_SHARE_WRITE = 0x00000002;
        private const UInt32 OPEN_EXISTING = 0x00000003;
        private const UInt32 FILE_ATTRIBUTE_NORMAL = 0x80;
        private const UInt32 ERROR_ACCESS_DENIED = 5;

        private const UInt32 ATTACH_PARRENT = 0xFFFFFFFF;

        #endregion
    }
    #endif
}