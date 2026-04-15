using Library;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Launcher
{
    public partial class LMain : DevExpress.XtraEditors.XtraForm
    {
        public const string PListFileName = "PList.Bin";
        public const string ClientPath = ".\\";
        public const string ClientFileName = "Zircon.exe";
        private const int MinimumConcurrentDownloads = 1;
        private const int MaximumConcurrentDownloads = 8;

        public DateTime LastSpeedCheck;
        public long TotalDownload, TotalProgress, CurrentProgress, LastDownloadProcess;
        public bool NeedUpdate;
        private volatile bool PatchFailed;

        public static bool HasError;

        public LMain()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls |
                                                   SecurityProtocolType.Tls11 |
                                                   SecurityProtocolType.Tls12;

            InitializeComponent();

        }

        private void LMain_Load(object sender, EventArgs e)
        {
            CheckPatch(false);
        }

        private void PatchNotesHyperlinkControl_HyperlinkClick(object sender, DevExpress.Utils.HyperlinkClickEventArgs e)
        {
            PatchNotesHyperlinkControl.LinkVisited = true;
            Process.Start(e.Link);
        }

        private void RepairButton_Click(object sender, EventArgs e)
        {
            CheckPatch(true);
        }

        private async void CheckPatch(bool repair)
        {
            HasError = false;
            RepairButton.Enabled = false;
            StartGameButton.Enabled = false;
            TotalDownload = 0;
            TotalProgress = 0;
            CurrentProgress = 0;
            TotalProgressBar.EditValue = 0;
            LastSpeedCheck = Time.Now;
            NeedUpdate = false;
            PatchFailed = false;

            Progress<string> progress = new Progress<string>(s => StatusLabel.Text = s);

            List<PatchInformation> liveVersion = await GetPatchInformation(progress);

            if (liveVersion == null)
            {
                DownloadSizeLabel.Text = "Downloading failed.";
                RepairButton.Enabled = true;
                StartGameButton.Enabled = true;
                return;
            }

            List<PatchInformation> currentVersion = repair ? null : await LoadVersion(progress);
            List<PatchInformation> patch = await CalculatePatch(liveVersion, currentVersion, progress);

            StatusLabel.Text = "Downloading";
            CreateSizeLabel();

            Task<bool> patchTask = DownloadPatch(patch, progress);

            while (!patchTask.IsCompleted)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
                CreateSizeLabel();
            }

            bool patchSuccess = await patchTask;

            CreateSizeLabel();

            if (!patchSuccess || PatchFailed)
            {
                if (Directory.Exists(ClientPath + "Patch\\"))
                    Directory.Delete(ClientPath + "Patch\\", true);

                StatusLabel.Text = "Patch Failed";
                DownloadSizeLabel.Text = "Patch failed.";
                DownloadSpeedLabel.Text = "Patch failed.";
                RepairButton.Enabled = true;
                StartGameButton.Enabled = false;
                return;
            }

            SaveVersion(liveVersion);

            StatusLabel.Text = "Complete";
            DownloadSizeLabel.Text = "Complete.";
            DownloadSpeedLabel.Text = "Complete.";

            if (Directory.Exists(ClientPath + "Patch\\"))
                Directory.Delete(ClientPath + "Patch\\", true);

            if (NeedUpdate)
            {
                File.WriteAllBytes(Program.PatcherFileName, Properties.Resources.Patcher);
                Process.Start(Program.PatcherFileName, $"\"{Application.ExecutablePath}.tmp\" \"{Application.ExecutablePath}\"");
                Environment.Exit(0);
            }

            try
            {
                if (File.Exists(Program.PatcherFileName))
                    File.Delete(Program.PatcherFileName);
            }
            catch (Exception) { }

            RepairButton.Enabled = true;
            StartGameButton.Enabled = true;
        }
        private void CreateSizeLabel()
        {
            const decimal KB = 1024;
            const decimal MB = KB * 1024;
            const decimal GB = MB * 1024;

            long progress = Interlocked.Read(ref CurrentProgress);

            StringBuilder text = new StringBuilder();

            if (progress > GB)
                text.Append($"{progress / GB:#,##0.0}GB");
            else if (progress > MB)
                text.Append($"{progress / MB:#,##0.0}MB");
            else if (progress > KB)
                text.Append($"{progress / KB:#,##0}KB");
            else
                text.Append($"{progress:#,##0}B");

            if (TotalDownload > GB)
                text.Append($" / {TotalDownload / GB:#,##0.0}GB");
            else if (TotalDownload > MB)
                text.Append($" / {TotalDownload / MB:#,##0.0}MB");
            else if (TotalDownload > KB)
                text.Append($" / {TotalDownload / KB:#,##0}KB");
            else
                text.Append($" / {TotalDownload:#,##0}B");

            DownloadSizeLabel.Text = text.ToString();

            if (TotalDownload > 0)
                TotalProgressBar.EditValue = Math.Max(0, Math.Min(100, (int)(progress * 100 / TotalDownload)));

            long elapsedTicks = Time.Now.Ticks - LastSpeedCheck.Ticks;
            long speed = elapsedTicks > 0 ? (progress - LastDownloadProcess) * TimeSpan.TicksPerSecond / elapsedTicks : 0;
            LastDownloadProcess = progress;

            if (speed > GB)
                DownloadSpeedLabel.Text = $"{speed / GB:#,##0.0}GBps";
            else if (speed > MB)
                DownloadSpeedLabel.Text = $"{speed / MB:#,##0.0}MBps";
            else if (speed > KB)
                DownloadSpeedLabel.Text = $"{speed / KB:#,##0}KBps";
            else
                DownloadSpeedLabel.Text = $"{speed:#,##0}Bps";

            LastSpeedCheck = Time.Now;
        }

        private async Task<List<PatchInformation>> LoadVersion(IProgress<string> progress)
        {
            List<PatchInformation> list = new List<PatchInformation>();

            try
            {
                if (File.Exists(ClientPath + "Version.bin"))
                {
                    using (MemoryStream mStream = new MemoryStream(await File.ReadAllBytesAsync(ClientPath + "Version.bin")))
                    using (BinaryReader reader = new BinaryReader(mStream))
                        while (reader.BaseStream.Position < reader.BaseStream.Length)
                            list.Add(new PatchInformation(reader));

                    progress.Report("Calculating Patch.");
                    return list;
                }

                progress.Report("Version Info is missing, Running Repairing");
            }
            catch (Exception ex)
            {
                progress.Report(ex.Message);
            }

            return null;
        }
        private async Task<List<PatchInformation>> GetPatchInformation(IProgress<string> progress)
        {
            try
            {
                progress.Report("Downloading Patch Information");

                using (HttpClient client = new HttpClient())
                {
                    if (Config.UseLogin)
                    {
                        var byteArray = Encoding.ASCII.GetBytes($"{Config.Username}:{Config.Password}");
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
                    }

                    using (HttpResponseMessage response = await client.GetAsync(Config.Host + PListFileName + "?nocache=" + Guid.NewGuid().ToString("N")))
                    {
                        response.EnsureSuccessStatusCode();

                        using (Stream contentStream = await response.Content.ReadAsStreamAsync())
                        using (BinaryReader reader = new BinaryReader(contentStream))
                        {
                            List<PatchInformation> list = new List<PatchInformation>();

                            while (reader.BaseStream.Position < reader.BaseStream.Length)
                                list.Add(new PatchInformation(reader));

                            return list;
                        }
                    }
                }

            }
            catch (Exception ex)
            {
                progress.Report(ex.Message);
            }

            return null;
        }
        private async Task<List<PatchInformation>> CalculatePatch(IReadOnlyList<PatchInformation> list, List<PatchInformation> current, IProgress<string> progress)
        {
            List<PatchInformation> patch = new List<PatchInformation>();

            if (list == null) return patch;

            for (int i = 0; i < list.Count; i++)
            {
                progress.Report($"Files Checked: {i + 1} of {list.Count}");

                PatchInformation file = list[i];
                if (current != null && current.Any(x => x.FileName == file.FileName && IsMatch(x.CheckSum, file.CheckSum))) continue;

                if (File.Exists(ClientPath + file.FileName))
                {
                    byte[] CheckSum;
                    using (MD5 md5 = MD5.Create())
                    {
                        using (FileStream stream = File.OpenRead(ClientPath + file.FileName))
                            CheckSum = await md5.ComputeHashAsync(stream);
                    }

                    if (IsMatch(CheckSum, file.CheckSum))
                        continue;
                }

                patch.Add(file);
                TotalDownload += file.CompressedLength;
            }

            return patch;
        }

        public bool IsMatch(byte[] a, byte[] b, long offSet = 0)
        {
            if (b == null || a == null || b.Length + offSet > a.Length || offSet < 0) return false;

            for (int i = 0; i < b.Length; i++)
                if (a[offSet + i] != b[i])
                    return false;

            return true;
        }

        private void SaveVersion(List<PatchInformation> version)
        {
            using (FileStream fStream = File.Create(ClientPath + "Version.bin"))
            using (BinaryWriter writer = new BinaryWriter(fStream))
            {
                foreach (PatchInformation info in version)
                    info.Save(writer);
            }

        }

        private async Task<bool> DownloadPatch(List<PatchInformation> patch, IProgress<string> progress)
        {
            if (patch.Count == 0) return true;

            int completedCount = 0;
            int maxConcurrency = Math.Min(MaximumConcurrentDownloads, Math.Max(MinimumConcurrentDownloads, Config.MaxConcurrentDownloads));

            using HttpClient client = CreateHttpClient();
            using SemaphoreSlim throttler = new SemaphoreSlim(maxConcurrency);

            Task<bool>[] tasks = new Task<bool>[patch.Count];

            for (int i = 0; i < patch.Count; i++)
            {
                PatchInformation file = patch[i];
                tasks[i] = DownloadAndExtract(file, client, throttler, progress, () =>
                {
                    int finished = Interlocked.Increment(ref completedCount);
                    progress.Report($"Patched files: {finished} of {patch.Count}");
                });
            }

            bool[] results = await Task.WhenAll(tasks);
            return results.All(x => x);
        }


        private void StartGameButton_Click(object sender, EventArgs e)
        {
            try
            {
                Process.Start(ClientPath + ClientFileName);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private HttpClient CreateHttpClient()
        {
            HttpClient client = new HttpClient();

            if (Config.UseLogin)
            {
                byte[] byteArray = Encoding.ASCII.GetBytes($"{Config.Username}:{Config.Password}");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
            }

            return client;
        }

        private async Task<bool> DownloadAndExtract(PatchInformation file, HttpClient client, SemaphoreSlim throttler, IProgress<string> progress, Action onCompleted)
        {
            await throttler.WaitAsync();

            try
            {
                if (!await Download(file, client))
                {
                    PatchFailed = true;
                    return false;
                }

                if (!await Extract(file))
                {
                    PatchFailed = true;
                    return false;
                }

                onCompleted();
                return true;
            }
            finally
            {
                throttler.Release();
            }
        }

        private async Task<bool> Download(PatchInformation file, HttpClient client)
        {
            string webFileName = file.FileName.Replace("\\", "-") + ".gz";

            try
            {
                using HttpResponseMessage response = await client.GetAsync(Config.Host + webFileName, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                string patchDirectory = ClientPath + "Patch\\";
                if (!Directory.Exists(patchDirectory))
                    Directory.CreateDirectory(patchDirectory);

                using Stream contentStream = await response.Content.ReadAsStreamAsync();
                using FileStream fileStream = new FileStream($"{patchDirectory}{webFileName}", FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                byte[] buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    Interlocked.Add(ref CurrentProgress, bytesRead);
                }

                return true;
            }
            catch (Exception ex)
            {
                file.CheckSum = Array.Empty<byte>();
                return ReportPatchFailure(file, ex);
            }

            return false;
        }

        private async Task<bool> Extract(PatchInformation file)
        {
            string webFileName = file.FileName.Replace("\\", "-") + ".gz";

            try
            {
                string toPath = ClientPath + file.FileName;

                if (Application.ExecutablePath.EndsWith(file.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    toPath += ".tmp";
                    NeedUpdate = true;
                }


                if (File.Exists(toPath)) File.Delete(toPath);

                await Decompress($"{ClientPath}Patch\\{webFileName}", toPath);
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                file.CheckSum = Array.Empty<byte>();

                if (HasError) return false;
                HasError = true;
                MessageBox.Show(ex.Message + "\n\nFile might be in use, please make sure the game is closed.", "File Error", MessageBoxButtons.OK);
                return false;
            }
            catch (Exception ex)
            {
                file.CheckSum = Array.Empty<byte>();
                return ReportPatchFailure(file, ex);
            }

            return false;
        }

        private bool ReportPatchFailure(PatchInformation file, Exception ex)
        {
            if (HasError) return false;

            HasError = true;
            BeginInvoke(new Action(() =>
            {
                MessageBox.Show($"Failed to patch {file.FileName}\n\n{ex.Message}", "Patch Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }));

            return false;
        }
        private static async Task Decompress(string sourceFile, string destFile)
        {
            if (!Directory.Exists(Path.GetDirectoryName(destFile)))
                Directory.CreateDirectory(Path.GetDirectoryName(destFile));

            using (FileStream tofile = File.Create(destFile))
            using (FileStream fromfile = File.OpenRead(sourceFile))
            using (GZipStream gStream = new GZipStream(fromfile, CompressionMode.Decompress))
            {
                await gStream.CopyToAsync(tofile);
            }
        }
    }
}
