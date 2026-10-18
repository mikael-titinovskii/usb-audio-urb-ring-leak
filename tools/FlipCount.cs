using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.Diagnostics.Tracing;

namespace FocusriteDiagnostics {
    // Fast form of the flip metric: reads only the FocusriteUsb queue provider's
    // completion entries (AudStream task 0x3d) and counts consecutive same-direction
    // completions. No per-event CSV is written, so this runs far faster than the
    // full AnalyzeFocusriteUsbQueue pass while producing the same headline number.
    public static class FlipCount {
        static readonly Guid AudStream = new Guid("0b8e02e8-5b01-3a7d-d401-5531a6a8bdde");

        public static string Analyze(string path) {
            long completions = 0, flips = 0, inCount = 0, outCount = 0;
            byte prev = 0; bool has = false, seen = false;
            double first = 0, last = 0;
            var perMin = new Dictionary<int, long>();
            long lost = 0;
            DateTime sessionStart = DateTime.MinValue, sessionEnd = DateTime.MinValue;

            using (var source = new ETWTraceEventSource(path)) {
                source.AllEvents += delegate(TraceEvent e) {
                    if (e.TaskGuid != AudStream) return;
                    if ((int)e.ID != 0x3d) return;
                    byte[] data = e.EventData();
                    if (data.Length < 21) return;
                    byte d = data[0];
                    completions++;
                    if (d == 0x49) inCount++; else if (d == 0x4f) outCount++;
                    if (has && d == prev) {
                        flips++;
                        int m = (int)(e.TimeStampRelativeMSec / 60000.0);
                        long c; perMin.TryGetValue(m, out c); perMin[m] = c + 1;
                    }
                    prev = d; has = true;
                    if (!seen) { first = e.TimeStampRelativeMSec; seen = true; }
                    last = e.TimeStampRelativeMSec;
                };
                source.Process();
                lost = source.EventsLost;
                sessionStart = source.SessionStartTime;
                sessionEnd = source.SessionEndTime;
            }

            var sb = new StringBuilder();
            double span = seen ? (last - first) / 60000.0 : 0;
            sb.AppendLine("file=" + path);
            sb.AppendLine("session_start=" + sessionStart.ToString("o", CultureInfo.InvariantCulture));
            sb.AppendLine("session_end=" + sessionEnd.ToString("o", CultureInfo.InvariantCulture));
            sb.AppendLine("events_lost=" + lost);
            sb.AppendLine("completions=" + completions + " IN=" + inCount + " OUT=" + outCount);
            sb.AppendLine("first_event_ms=" + first.ToString("F1", CultureInfo.InvariantCulture));
            sb.AppendLine("last_event_ms=" + last.ToString("F1", CultureInfo.InvariantCulture));
            sb.AppendLine("same_direction_pairs=" + flips);
            sb.AppendLine("span_min=" + span.ToString("F3", CultureInfo.InvariantCulture));
            sb.AppendLine("flips_per_min=" + (span > 0 ? (flips / span) : 0).ToString("F4", CultureInfo.InvariantCulture));
            sb.AppendLine("flips_per_1000_completions=" + (completions > 0 ? 1000.0 * flips / completions : 0).ToString("F4", CultureInfo.InvariantCulture));
            if (span > 0 && (last / 60000.0) < span - 0.5)
                sb.AppendLine("WARNING: selected events stop before session end (partial coverage)");
            sb.AppendLine("-- per-minute flips (trace-relative) --");
            foreach (var kv in perMin) sb.AppendLine("min=" + kv.Key + " flips=" + kv.Value);
            return sb.ToString();
        }
    }
}
