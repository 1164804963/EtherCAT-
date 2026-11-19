using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace EtherCatBench
{
    sealed class PdoTrace
    {
        public const int LongCaptureSeconds = 600;

        public struct Sample
        {
            public long Start, Interval, Duration, Skipped, Al, Send, Receive;
            public long? WaitOffset, DispatchOffset, AlStatusAge;
            public int Wkc, Expected, State, SpinLeadUs;
            public int? AlStatusCode;
        }

        // Assigned before the capture and written on the worker. Saving happens after transfer of ownership.
        readonly Sample[] samples;
        readonly long limitTicks;
        int count;
        long startedAt, elapsedTicks;
        TimeSpan? startedCpu;
        double? cpuPercent;

        public PdoTrace(bool longCapture = false)
        {
            samples = new Sample[longCapture ? 660000 : 120000];
            limitTicks = longCapture ? LongCaptureSeconds * Stopwatch.Frequency : 0;
        }

        public int Count { get { return count; } }

        public void Start(long now)
        {
            startedAt = now;
            startedCpu = ReadCpu();
        }

        public bool HasExpired(long now)
        {
            return limitTicks != 0 && now - startedAt >= limitTicks;
        }

        public double ElapsedSeconds(long now, bool recording)
        {
            return (recording ? now - startedAt : elapsedTicks) / (double)Stopwatch.Frequency;
        }

        public void Finish(long now)
        {
            elapsedTicks = now - startedAt;
            TimeSpan? stoppedCpu = ReadCpu();
            if (startedCpu.HasValue && stoppedCpu.HasValue && elapsedTicks > 0)
                cpuPercent = 100 * (stoppedCpu.Value - startedCpu.Value).TotalSeconds /
                    (ElapsedSeconds(now, false) * Environment.ProcessorCount);
        }

        static TimeSpan? ReadCpu()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess()) return process.TotalProcessorTime;
            }
            catch (Win32Exception) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        public bool Add(Sample sample)
        {
            if (count == samples.Length) return false;
            samples[count++] = sample;
            return count < samples.Length;
        }

        public int Save(string path)
        {
            const string header = "elapsed_us,interval_us,exchange_us,wkc,expected_wkc,skipped_slots,al_state," +
                "timer_return_offset_us,dispatch_late_us,al_before_exchange_us,send_us,receive_us,scheduler_spin_lead_us," +
                "capture_wall_seconds,process_cpu_percent,al_status_code,al_status_age_us";
            using (var output = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                output.WriteLine(header);
                for (int i = 0; i < count; i++)
                {
                    var sample = samples[i];
                    output.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16}",
                        Microseconds(sample.Start - samples[0].Start),
                        Microseconds(i == 0 || sample.Interval <= 0 ? (long?)null : sample.Interval),
                        Microseconds(sample.Duration), sample.Wkc, sample.Expected, sample.Skipped, sample.State,
                        Microseconds(sample.WaitOffset), Microseconds(sample.DispatchOffset), Microseconds(sample.Al),
                        Microseconds(sample.Send < 0 ? (long?)null : sample.Send),
                        Microseconds(sample.Receive < 0 ? (long?)null : sample.Receive), sample.SpinLeadUs,
                        i == 0 ? ElapsedSeconds(0, false).ToString("F3", CultureInfo.InvariantCulture) : "",
                        i == 0 && cpuPercent.HasValue ? cpuPercent.Value.ToString("F3", CultureInfo.InvariantCulture) : "",
                        sample.AlStatusCode.HasValue ? "0x" + sample.AlStatusCode.Value.ToString("X4", CultureInfo.InvariantCulture) : "",
                        Microseconds(sample.AlStatusAge)));
                }
            }
            return count;
        }

        static string Microseconds(long? ticks)
        {
            return ticks.HasValue ? (ticks.Value * (1000000d / Stopwatch.Frequency)).ToString("F3", CultureInfo.InvariantCulture) : "";
        }
    }
}
