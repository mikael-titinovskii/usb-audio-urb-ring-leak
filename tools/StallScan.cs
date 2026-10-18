using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Diagnostics.Tracing;

namespace FocusriteDiagnostics {
    // Single-pass scan of a FocusriteUsb queue trace for the fault chain:
    //   stall (>=5 ms with no completion processed) -> re-prime -> pairing skew -> orphan
    // Reports, without writing a per-event CSV:
    //   * every completion gap above a threshold
    //   * per-minute flip (consecutive same-direction completion) counts
    //   * per-minute pairing-offset histogram   (offset = (input_pair_index - out_index) mod 10)
    //   * per-minute submit-margin histogram for OUT submits
    //   * OUT submit index advance of +2 (orphan candidate)
    public static class StallScan {
        static readonly Guid AudStream = new Guid("0b8e02e8-5b01-3a7d-d401-5531a6a8bdde");
        static readonly Guid AudioEngine = new Guid("ba5912e4-a67b-3b96-db60-afe06000d08c");

        public static string Analyze(string path, double gapMs) { return Analyze(path, gapMs, 0, 0); }

        public static string Analyze(string path, double gapMs, double focusFromS, double focusToS) {
            bool focus = focusToS > focusFromS;
            var secComps = new Dictionary<int, long>();
            var secFlips = new Dictionary<int, long>();
            var secOffset = new Dictionary<int, Dictionary<int, long>>();
            var secMargin = new Dictionary<int, Dictionary<int, long>>();
            var secOut = new Dictionary<int, Dictionary<int, long>>();
            long completions = 0, flips = 0;
            byte prevDir = 0; bool hasPrev = false, seen = false;
            double first = 0, last = 0, prevComp = 0;
            long lost = 0;
            var gaps = new List<double[]>();
            var perMinFlips = new Dictionary<int, long>();
            var perMinComps = new Dictionary<int, long>();
            var perMinOffset = new Dictionary<int, Dictionary<int, long>>();
            var perMinMargin = new Dictionary<int, Dictionary<int, long>>();
            var skips = new List<double[]>();
            var pairs = new List<double[]>();     // (time, offset)
            var outSubmits = new List<double[]>();// (time, index, margin)
            var all = new List<double[]>();       // (time, dirByte) completions

            using (var source = new ETWTraceEventSource(path)) {
                source.AllEvents += delegate(TraceEvent e) {
                    var tg = e.TaskGuid;
                    if (tg != AudStream && tg != AudioEngine) return;
                    double t = e.TimeStampRelativeMSec;
                    byte[] data = e.EventData();
                    int sec = (int)(t / 1000.0);
                    bool inFocus = focus && sec >= (int)focusFromS && sec <= (int)focusToS;
                    if (tg == AudStream) {
                        int id = (int)e.ID;
                        if (id == 0x3d) {                       // Complete
                            if (data.Length < 21) return;
                            byte d = data[0];
                            int m = (int)(t / 60000.0);
                            completions++;
                            all.Add(new double[] { t, d });
                            long c; perMinComps.TryGetValue(m, out c); perMinComps[m] = c + 1;
                            if (inFocus) { long sc; secComps.TryGetValue(sec, out sc); secComps[sec] = sc + 1; }
                            if (hasPrev) {
                                if (d == prevDir) {
                                    flips++; long f; perMinFlips.TryGetValue(m, out f); perMinFlips[m] = f + 1;
                                    if (inFocus) { long sf; secFlips.TryGetValue(sec, out sf); secFlips[sec] = sf + 1; }
                                }
                                double g = t - prevComp;
                                if (g >= gapMs) gaps.Add(new double[] { t, g });
                            }
                            prevDir = d; hasPrev = true; prevComp = t;
                            if (!seen) { first = t; seen = true; }
                            last = t;
                        } else if (id == 0x55) {                // Submit
                            if (data.Length < 21) return;
                            byte d = data[0];
                            int idx = BitConverter.ToInt32(data, 1);
                            int margin = BitConverter.ToInt32(data, 13);
                            if (d == 0x4f) {
                                outSubmits.Add(new double[] { t, idx, margin });
                                if (outSubmits.Count >= 2) {
                                    var a = outSubmits[outSubmits.Count - 2];
                                    int ai = (int)a[1], bi = idx;
                                    if (ai >= 0 && bi >= 0 && (ai + 2) % 10 == bi % 10) skips.Add(new double[] { t, bi, margin });
                                }
                                int m = (int)(t / 60000.0);
                                Dictionary<int, long> h;
                                if (!perMinMargin.TryGetValue(m, out h)) { h = new Dictionary<int, long>(); perMinMargin[m] = h; }
                                long v; h.TryGetValue(margin, out v); h[margin] = v + 1;
                                if (inFocus) {
                                    Dictionary<int, long> sh;
                                    if (!secMargin.TryGetValue(sec, out sh)) { sh = new Dictionary<int, long>(); secMargin[sec] = sh; }
                                    long sv; sh.TryGetValue(margin, out sv); sh[margin] = sv + 1;
                                    Dictionary<int, long> oi;
                                    if (!secOut.TryGetValue(sec, out oi)) { oi = new Dictionary<int, long>(); secOut[sec] = oi; }
                                    long ov; oi.TryGetValue(idx, out ov); oi[idx] = ov + 1;
                                }
                            }
                        }
                    } else {                                    // AudioEngine
                        if ((int)e.ID == 0x1d && data.Length == 8) {
                            int other = BitConverter.ToInt32(data, 0);
                            int idx = BitConverter.ToInt32(data, 4);
                            int off = ((other - idx) % 10 + 10) % 10;
                            pairs.Add(new double[] { t, off });
                            int m = (int)(t / 60000.0);
                            Dictionary<int, long> h;
                            if (!perMinOffset.TryGetValue(m, out h)) { h = new Dictionary<int, long>(); perMinOffset[m] = h; }
                            long v; h.TryGetValue(off, out v); h[off] = v + 1;
                            if (inFocus) {
                                Dictionary<int, long> sh;
                                if (!secOffset.TryGetValue(sec, out sh)) { sh = new Dictionary<int, long>(); secOffset[sec] = sh; }
                                long sv; sh.TryGetValue(off, out sv); sh[off] = sv + 1;
                            }
                        }
                    }
                };
                source.Process();
                lost = source.EventsLost;
            }

            var sb = new StringBuilder();
            double span = seen ? (last - first) / 60000.0 : 0;
            sb.AppendLine("file=" + path);
            sb.AppendLine("events_lost=" + lost);
            sb.AppendLine("completions=" + completions);
            sb.AppendLine("span_min=" + span.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("coverage_first_ms=" + first.ToString("F1", CultureInfo.InvariantCulture)
                        + " last_ms=" + last.ToString("F1", CultureInfo.InvariantCulture));
            sb.AppendLine("flips=" + flips + " flips_per_min=" + (span > 0 ? flips / span : 0).ToString("F4", CultureInfo.InvariantCulture));
            sb.AppendLine("out_index_advance_plus2=" + skips.Count);
            sb.AppendLine("stall_threshold_ms=" + gapMs.ToString(CultureInfo.InvariantCulture)
                        + " stalls=" + gaps.Count);
            long micro = gaps.Count(g => g[1] < 50), mid = gaps.Count(g => g[1] >= 50 && g[1] < 500), big = gaps.Count(g => g[1] >= 500);
            sb.AppendLine("gap_buckets 5-50ms=" + micro + " (stall candidates)  50-500ms=" + mid + "  >500ms=" + big
                        + " (capture coverage holes - investigate before trusting this run)");
            foreach (var g in gaps.Where(g => g[1] < 50))
                sb.AppendLine("  STALL at_s=" + (g[0] / 1000.0).ToString("F3", CultureInfo.InvariantCulture)
                            + " gap_us=" + Math.Round(g[1] * 1000).ToString(CultureInfo.InvariantCulture));
            foreach (var s in skips)
                sb.AppendLine("  ADVANCE+2 at_s=" + (s[0] / 1000.0).ToString("F3", CultureInfo.InvariantCulture)
                            + " index=" + (int)s[1] + " margin=" + (int)s[2]);
            sb.AppendLine("-- per-minute: comps flips offsets(off:count) margins(margin:count) --");
            foreach (var m in perMinComps.Keys.OrderBy(x => x)) {
                string off = perMinOffset.ContainsKey(m)
                    ? string.Join(",", perMinOffset[m].OrderBy(x => x.Key).Select(x => x.Key + ":" + x.Value)) : "-";
                string mg = perMinMargin.ContainsKey(m)
                    ? string.Join(",", perMinMargin[m].OrderBy(x => x.Key).Select(x => x.Key + ":" + x.Value)) : "-";
                long f; perMinFlips.TryGetValue(m, out f);
                sb.AppendLine("min=" + m + " comps=" + perMinComps[m] + " flips=" + f + " offsets=" + off + " margins=" + mg);
            }
            if (focus) {
                sb.AppendLine("-- focus window " + focusFromS.ToString(CultureInfo.InvariantCulture) + "s.." + focusToS.ToString(CultureInfo.InvariantCulture)
                            + "s, per second: comps flips offsets(off:count) margins(m:count) out_submit_indices(i:count) --");
                for (int s = (int)focusFromS; s <= (int)focusToS; s++) {
                    string off = secOffset.ContainsKey(s)
                        ? string.Join(",", secOffset[s].OrderBy(x => x.Key).Select(x => x.Key + ":" + x.Value)) : "-";
                    string mg2 = secMargin.ContainsKey(s)
                        ? string.Join(",", secMargin[s].OrderBy(x => x.Key).Select(x => x.Key + ":" + x.Value)) : "-";
                    string oi = secOut.ContainsKey(s)
                        ? string.Join(",", secOut[s].OrderBy(x => x.Key).Select(x => x.Key + ":" + x.Value)) : "-";
                    long sc, sf; secComps.TryGetValue(s, out sc); secFlips.TryGetValue(s, out sf);
                    sb.AppendLine("sec=" + s + " comps=" + sc + " flips=" + sf + " offsets=" + off + " margins=" + mg2 + " out_idx=" + oi);
                }
            }
            return sb.ToString();
        }
    }
}
