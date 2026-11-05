using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KaruCloud
{
    /// <summary>
    /// Streams one track at a time. The HLS segments are fetched with your own API token, held in memory only
    /// (nothing is written to disk), decoded with Windows Media Foundation and played through WaveOut.
    /// </summary>
    public sealed class Player : IDisposable
    {
        readonly ScApi api;
        WaveOutEvent? output;
        StreamMediaFoundationReader? reader;
        VolumeSampleProvider? volume;
        MemoryStream? data;
        CancellationTokenSource? loadCts;
        float vol = 0.8f;
        TimeSpan apiDuration = TimeSpan.Zero; // length SoundCloud reports for the track (used when the decoder can't tell)

        public event Action? Loading;
        public event Action? Started;
        public event Action? Ended;
        public event Action<string>? Failed;

        public bool IsLoading { get; private set; }
        public bool IsPlaying => output != null && output.PlaybackState == PlaybackState.Playing;
        public bool HasTrack => reader != null;

        public TimeSpan Position
        {
            get { try { return reader?.CurrentTime ?? TimeSpan.Zero; } catch { return TimeSpan.Zero; } }
        }

        /// <summary>
        /// Streams stitched together from HLS segments often have no length header, so the decoder reports 0:00.
        /// Then the timeline never moved and every seek was clamped to the start. Fall back to SoundCloud's own duration.
        /// </summary>
        public TimeSpan Duration
        {
            get
            {
                TimeSpan d = TimeSpan.Zero;
                try { d = reader?.TotalTime ?? TimeSpan.Zero; } catch { }
                return d.TotalSeconds > 1 ? d : apiDuration;
            }
        }

        public float Volume
        {
            get => vol;
            set { vol = Math.Max(0f, Math.Min(1f, value)); if (volume != null) volume.Volume = vol; }
        }

        public Player(ScApi api)
        {
            this.api = api;
            try { MediaFoundationApi.Startup(); } catch { }
        }

        public async Task PlayAsync(ScTrack track)
        {
            Stop();
            var cts = new CancellationTokenSource();
            loadCts = cts;
            var ct = cts.Token;
            IsLoading = true;
            apiDuration = TimeSpan.FromMilliseconds(Math.Max(0, track.Duration));
            Loading?.Invoke();
            try
            {
                var (url, hls) = await api.ResolveStreamAsync(track, ct);
                if (ct.IsCancellationRequested) return;

                MemoryStream ms = hls
                    ? await DownloadHlsAsync(url, ct)
                    : new MemoryStream(await api.GetBytesAsync(url, ct));
                if (ct.IsCancellationRequested) { ms.Dispose(); return; }

                data = ms;
                reader = new StreamMediaFoundationReader(ms);
                volume = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = vol };
                output = new WaveOutEvent { DesiredLatency = 200 };
                output.Init(new SampleToWaveProvider16(volume));
                output.PlaybackStopped += OnStopped;
                output.Play();
                IsLoading = false;
                Started?.Invoke();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return; // a newer track took over
                IsLoading = false;
                Teardown();
                // An HttpClient timeout also surfaces as a cancellation: report it instead of staying on "Loading" forever.
                Store.Log("play " + track.Id + " " + track.Title, ex);
                Failed?.Invoke(ex is OperationCanceledException ? "SoundCloud took too long to respond." : ex.Message);
            }
        }

        void OnStopped(object? sender, StoppedEventArgs e)
        {
            if (!ReferenceEquals(sender, output)) return;
            if (e.Exception != null) Failed?.Invoke(e.Exception.Message);
            else Ended?.Invoke();
        }

        public void Pause() { try { output?.Pause(); } catch { } }
        public void Resume() { try { output?.Play(); } catch { } }

        public void Toggle()
        {
            if (output == null) return;
            if (IsPlaying) Pause(); else Resume();
        }

        public void Seek(TimeSpan t)
        {
            try
            {
                if (reader == null) return;
                if (t < TimeSpan.Zero) t = TimeSpan.Zero;
                var d = Duration;
                if (d.TotalSeconds > 0 && t > d) t = d;
                reader.CurrentTime = t;
            }
            catch { }
        }

        public void Stop()
        {
            try { loadCts?.Cancel(); } catch { }
            IsLoading = false;
            Teardown();
        }

        void Teardown()
        {
            var o = output;
            output = null;
            if (o != null)
            {
                o.PlaybackStopped -= OnStopped;
                try { o.Stop(); } catch { }
                try { o.Dispose(); } catch { }
            }
            try { reader?.Dispose(); } catch { }
            reader = null;
            volume = null;
            try { data?.Dispose(); } catch { }
            data = null;
        }

        // ---------- HLS (audio only, held in memory) ----------
        async Task<MemoryStream> DownloadHlsAsync(string playlistUrl, CancellationToken ct)
        {
            string text = System.Text.Encoding.UTF8.GetString(await api.GetBytesAsync(playlistUrl, ct));
            var baseUri = new Uri(playlistUrl);
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

            // Master playlist -> follow the first variant.
            int variant = lines.FindIndex(l => l.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal));
            if (variant >= 0)
            {
                string next = lines.Skip(variant + 1).FirstOrDefault(l => !l.StartsWith("#")) ?? "";
                if (next.Length == 0) throw new InvalidOperationException("Empty stream playlist.");
                return await DownloadHlsAsync(new Uri(baseUri, next).ToString(), ct);
            }

            // Relative segment links lose the playlist's signed query string (?Policy=...&Signature=...) and then 404: carry it over.
            string baseQuery = baseUri.Query;
            string Resolve(string link)
            {
                var u = new Uri(baseUri, link);
                if (baseQuery.Length > 1 && u.Query.Length == 0 && u.Host == baseUri.Host)
                    return u.GetLeftPart(UriPartial.Path) + baseQuery + u.Fragment;
                return u.ToString();
            }

            // fMP4 streams (AAC) start with an init segment; put it in front of the fragments and the file plays like a normal .m4a
            byte[]? init = null;
            string mapLine = lines.FirstOrDefault(l => l.StartsWith("#EXT-X-MAP", StringComparison.Ordinal)) ?? "";
            if (mapLine.Length > 0)
            {
                var m = System.Text.RegularExpressions.Regex.Match(mapLine, "URI=\"([^\"]+)\"");
                if (!m.Success) throw new NotSupportedException("This stream uses a segment format KaruCloud can't play yet.");
                init = await api.GetBytesAsync(Resolve(m.Groups[1].Value), ct);
            }

            var segs = lines.Where(l => !l.StartsWith("#")).Select(l => Resolve(l)).ToList();
            if (segs.Count == 0) throw new InvalidOperationException("The stream has no audio segments.");

            var parts = new byte[segs.Count][];
            using var gate = new SemaphoreSlim(10);
            var tasks = segs.Select((u, i) => Task.Run(async () =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        try { parts[i] = await api.GetBytesAsync(u, ct); return; }
                        catch (OperationCanceledException) { throw; }
                        catch when (attempt == 0) { await Task.Delay(300, ct); }
                    }
                }
                finally { gate.Release(); }
            }, ct)).ToList();
            await Task.WhenAll(tasks);

            if (parts[0].Length >= 188 && parts[0][0] == 0x47 && parts[0].Length % 188 == 0)
                throw new NotSupportedException("This stream uses MPEG-TS segments, which KaruCloud can't play yet.");

            var ms = new MemoryStream();
            if (init != null) ms.Write(init, 0, init.Length);
            foreach (var p in parts)
            {
                int offset = Id3Length(p);
                ms.Write(p, offset, p.Length - offset);
            }
            ms.Position = 0;
            return ms;
        }

        /// <summary>HLS "packed audio" segments can begin with ID3 timestamp tags; skip them so the frames join cleanly.</summary>
        static int Id3Length(byte[] p)
        {
            int pos = 0;
            while (p.Length - pos > 10 && p[pos] == 0x49 && p[pos + 1] == 0x44 && p[pos + 2] == 0x33)
            {
                int size = ((p[pos + 6] & 0x7F) << 21) | ((p[pos + 7] & 0x7F) << 14) | ((p[pos + 8] & 0x7F) << 7) | (p[pos + 9] & 0x7F);
                bool footer = (p[pos + 5] & 0x10) != 0;
                pos += 10 + size + (footer ? 10 : 0);
            }
            return Math.Min(pos, p.Length);
        }

        public void Dispose()
        {
            Stop();
            try { MediaFoundationApi.Shutdown(); } catch { }
        }
    }
}
